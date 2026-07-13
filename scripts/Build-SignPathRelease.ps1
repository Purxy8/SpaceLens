[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'ResolveVersion',
        'ValidateConfiguration',
        'BuildApp',
        'ValidateSignedApp',
        'BuildSetup',
        'ValidateSignedSetup',
        'Assemble'
    )]
    [string]$Stage,

    [string]$GitHubOutput = '',

    [string]$ExpectedVersion = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$signPathRoot = [IO.Path]::GetFullPath((Join-Path $artifacts 'signpath'))
$versionProperties = Join-Path $repository 'Directory.Build.props'
$appProject = Join-Path $repository 'src\SpaceLens\SpaceLens.csproj'
$setupProject = Join-Path $repository 'src\SpaceLens.Setup\SpaceLens.Setup.csproj'
$publicKey = Join-Path $repository 'src\SpaceLens\assets\update-public-key.pem'
$securityModule = Join-Path $PSScriptRoot 'ReleaseSecurity.psm1'

$unsignedAppDirectory = Join-Path $signPathRoot 'unsigned-app'
$signedAppDirectory = Join-Path $signPathRoot 'signed-app'
$unsignedSetupDirectory = Join-Path $signPathRoot 'unsigned-setup'
$signedSetupDirectory = Join-Path $signPathRoot 'signed-setup'
$appPublishDirectory = Join-Path $signPathRoot 'app-publish'
$setupPublishDirectory = Join-Path $signPathRoot 'setup-publish'
$finalDirectory = Join-Path $signPathRoot 'final'

foreach ($required in @($versionProperties, $appProject, $setupProject, $publicKey, $securityModule)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required file is missing: $required"
    }
}
Import-Module $securityModule -Force
Assert-PublicSpkiPemFile -Path $publicKey | Out-Null

[xml]$propertiesDocument = Get-Content -Raw -LiteralPath $versionProperties
$versionNodes = @($propertiesDocument.SelectNodes('/Project/PropertyGroup/SpaceLensVersion'))
if ($versionNodes.Count -ne 1) {
    throw 'Directory.Build.props must contain exactly one SpaceLensVersion element.'
}

$version = $versionNodes[0].InnerText.Trim()
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "Directory.Build.props contains an invalid SpaceLensVersion: $version"
}

function Get-OwnedChildPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd([char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ))
    $prefix = $fullParent + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::Ordinal)) {
        throw "Refusing to manage a path outside $fullParent`: $fullPath"
    }

    return $fullPath
}

function Assert-NoReparsePointTree {
    param([Parameter(Mandatory)][string]$Path)

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Path))
    while ($pending.Count -ne 0) {
        $current = $pending.Pop()
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to recursively manage a reparse point: $current"
        }
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $current -Force) {
                $pending.Push($child.FullName)
            }
        }
    }
}

function Reset-OwnedDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent
    )

    $safePath = Get-OwnedChildPath -Path $Path -Parent $Parent
    if (Test-Path -LiteralPath $safePath) {
        $resolvedPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $safePath).Path)
        [void](Get-OwnedChildPath -Path $resolvedPath -Parent $Parent)
        Assert-NoReparsePointTree -Path $resolvedPath
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $safePath -Force | Out-Null
    return $safePath
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Invoke-PackagedExecutable {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description,
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 90
    )

    $process = Start-Process -FilePath $Path -ArgumentList '"--self-test"' -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw "$Description timed out after $TimeoutSeconds seconds."
        }

        $process.Refresh()
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    if ($exitCode -ne 0) {
        throw "$Description failed with exit code $exitCode."
    }
}

function Write-HashFile {
    param([Parameter(Mandatory)][string]$Path)

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    $name = [IO.Path]::GetFileName($Path)
    [IO.File]::WriteAllText("$Path.sha256", "$hash  $name`r`n", [Text.Encoding]::ASCII)
    return $hash
}

function Assert-UnsignedExecutable {
    param([Parameter(Mandatory)][string]$Path)

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "Expected an unsigned executable, but $([IO.Path]::GetFileName($Path)) has Authenticode status $($signature.Status)."
    }
}

function Assert-TrustedAuthenticode {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$OriginalFilename
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Signed executable is missing: $Path"
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode validation failed for $([IO.Path]::GetFileName($Path)): $($signature.Status) - $($signature.StatusMessage)"
    }
    if ($null -eq $signature.SignerCertificate) {
        throw "Authenticode validation did not return a signer certificate for $([IO.Path]::GetFileName($Path))."
    }
    $publisher = $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if (-not [string]::Equals($publisher, 'SignPath Foundation', [StringComparison]::Ordinal)) {
        throw "$([IO.Path]::GetFileName($Path)) is validly signed, but not by SignPath Foundation."
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "$([IO.Path]::GetFileName($Path)) does not have a trusted timestamp."
    }
    if ($signature.PSObject.Properties.Name -contains 'SignatureType' -and
        -not [string]::Equals([string]$signature.SignatureType, 'Authenticode', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$([IO.Path]::GetFileName($Path)) is not protected by an embedded Authenticode signature."
    }
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    if ($info.ProductName -ne 'SpaceLens' -or $info.ProductVersion -ne $version -or $info.FileVersion -ne "$version.0" -or $info.CompanyName -ne 'SpaceLens' -or $info.OriginalFilename -ne $OriginalFilename) {
        throw "$([IO.Path]::GetFileName($Path)) metadata does not match the approved SpaceLens artifact configuration."
    }

    return $signature
}

function Assert-DirectoryContainsOnly {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string[]]$Names
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "Expected directory is missing: $Directory"
    }

    $entries = @(Get-ChildItem -LiteralPath $Directory -Force)
    if (@($entries | Where-Object { $_.PSIsContainer }).Count -ne 0) {
        throw "Unexpected subdirectory found in $Directory."
    }

    $expected = @($Names | Sort-Object)
    $actual = @($entries | ForEach-Object Name | Sort-Object)
    $difference = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual -CaseSensitive)
    if ($difference.Count -ne 0) {
        throw "Unexpected files in $Directory`: $($difference | Out-String)"
    }
}

function Get-SignerMetadata {
    param([Parameter(Mandatory)][System.Management.Automation.Signature]$Signature)

    $timestampSubject = $null
    $timestampThumbprint = $null
    if ($null -ne $Signature.TimeStamperCertificate) {
        $timestampSubject = $Signature.TimeStamperCertificate.Subject
        $timestampThumbprint = $Signature.TimeStamperCertificate.Thumbprint
    }

    return [ordered]@{
        subject = $Signature.SignerCertificate.Subject
        thumbprint = $Signature.SignerCertificate.Thumbprint
        timestampSubject = $timestampSubject
        timestampThumbprint = $timestampThumbprint
    }
}

function Get-SourceCommit {
    $sourceCommit = [Environment]::GetEnvironmentVariable('GITHUB_SHA')
    if ($sourceCommit -match '^[0-9a-fA-F]{40}$') {
        return $sourceCommit.ToLowerInvariant()
    }

    if (Get-Command git -ErrorAction SilentlyContinue) {
        $gitSafety = "safe.directory=$($repository.Replace('\', '/'))"
        $sourceCommit = (& git -c $gitSafety -C $repository rev-parse HEAD).Trim()
        if ($LASTEXITCODE -eq 0 -and $sourceCommit -match '^[0-9a-fA-F]{40}$') {
            return $sourceCommit.ToLowerInvariant()
        }
    }

    throw 'The source commit could not be resolved.'
}

switch ($Stage) {
    'ResolveVersion' {
        if ($ExpectedVersion -and -not [string]::Equals($ExpectedVersion, $version, [StringComparison]::Ordinal)) {
            throw "Requested version $ExpectedVersion does not match Directory.Build.props version $version."
        }
        if ([Environment]::GetEnvironmentVariable('GITHUB_ACTIONS') -eq 'true') {
            $expectedRef = "refs/tags/v$version"
            $actualRef = [Environment]::GetEnvironmentVariable('GITHUB_REF')
            if (-not [string]::Equals($actualRef, $expectedRef, [StringComparison]::Ordinal)) {
                throw "Release signing must run from immutable tag $expectedRef, not $actualRef."
            }
        }
        if ($GitHubOutput) {
            $outputPath = [IO.Path]::GetFullPath($GitHubOutput)
            [IO.File]::AppendAllText($outputPath, "version=$version`n", [Text.UTF8Encoding]::new($false))
        }
        Write-Host "SpaceLens version: $version"
    }

    'ValidateConfiguration' {
        $requiredConfiguration = @(
            'SIGNPATH_API_TOKEN',
            'SIGNPATH_ORGANIZATION_ID',
            'SIGNPATH_PROJECT_SLUG',
            'SIGNPATH_SIGNING_POLICY_SLUG',
            'SIGNPATH_APP_ARTIFACT_CONFIG',
            'SIGNPATH_SETUP_ARTIFACT_CONFIG'
        )
        $missing = @($requiredConfiguration | Where-Object {
            [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_))
        })
        if ($missing.Count -ne 0) {
            throw "Required SignPath secrets or repository variables are missing: $($missing -join ', ')"
        }

        Write-Host 'SignPath workflow configuration is present.'
    }

    'BuildApp' {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw 'The .NET 10 SDK is required and dotnet was not found on PATH.'
        }

        New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
        [void](Reset-OwnedDirectory -Path $signPathRoot -Parent $artifacts)
        New-Item -ItemType Directory -Path $unsignedAppDirectory, $appPublishDirectory -Force | Out-Null

        Invoke-DotNet -Arguments @(
            'restore', $appProject,
            '-r', 'win-x64',
            '-p:SpaceLensEnableDiagnostics=false',
            '--nologo'
        )
        Invoke-DotNet -Arguments @(
            'publish', $appProject,
            '-c', 'Release',
            '-r', 'win-x64',
            '--self-contained', 'true',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true',
            '-p:DebugType=None',
            '-p:DebugSymbols=false',
            '-p:SpaceLensEnableDiagnostics=false',
            '-o', $appPublishDirectory,
            '--no-restore',
            '--nologo'
        )

        $publishedApp = Join-Path $appPublishDirectory 'SpaceLens.exe'
        $unsignedApp = Join-Path $unsignedAppDirectory 'SpaceLens.exe'
        if (-not (Test-Path -LiteralPath $publishedApp -PathType Leaf)) {
            throw "Published application is missing: $publishedApp"
        }
        Copy-Item -LiteralPath $publishedApp -Destination $unsignedApp
        Assert-UnsignedExecutable -Path $unsignedApp
        Invoke-PackagedExecutable -Path $unsignedApp -Description 'Unsigned SpaceLens packaged self-test'
        & (Join-Path $PSScriptRoot 'Test-StartupHookIsolation.ps1') -Executable $unsignedApp -WorkDirectory $signPathRoot
        Assert-DirectoryContainsOnly -Directory $unsignedAppDirectory -Names @('SpaceLens.exe')
    }

    'ValidateSignedApp' {
        Assert-DirectoryContainsOnly -Directory $signedAppDirectory -Names @('SpaceLens.exe')
        $signedApp = Join-Path $signedAppDirectory 'SpaceLens.exe'
        [void](Assert-TrustedAuthenticode -Path $signedApp -OriginalFilename 'SpaceLens.dll')
        Invoke-PackagedExecutable -Path $signedApp -Description 'Signed SpaceLens packaged self-test'
        [void](Assert-TrustedAuthenticode -Path $signedApp -OriginalFilename 'SpaceLens.dll')
    }

    'BuildSetup' {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw 'The .NET 10 SDK is required and dotnet was not found on PATH.'
        }

        $signedApp = Join-Path $signedAppDirectory 'SpaceLens.exe'
        [void](Assert-TrustedAuthenticode -Path $signedApp -OriginalFilename 'SpaceLens.dll')
        Invoke-PackagedExecutable -Path $signedApp -Description 'Signed SpaceLens pre-package self-test'
        [void](Assert-TrustedAuthenticode -Path $signedApp -OriginalFilename 'SpaceLens.dll')
        [void](Write-HashFile -Path $signedApp)

        [void](Reset-OwnedDirectory -Path $unsignedSetupDirectory -Parent $signPathRoot)
        [void](Reset-OwnedDirectory -Path $setupPublishDirectory -Parent $signPathRoot)
        $payloadProperty = "-p:SpaceLensPayload=$signedApp"

        Invoke-DotNet -Arguments @(
            'restore', $setupProject,
            '-r', 'win-x64',
            $payloadProperty,
            '--nologo'
        )
        Invoke-DotNet -Arguments @(
            'publish', $setupProject,
            '-c', 'Release',
            '-r', 'win-x64',
            '--self-contained', 'true',
            '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true',
            '-p:DebugType=None',
            '-p:DebugSymbols=false',
            $payloadProperty,
            '-o', $setupPublishDirectory,
            '--no-restore',
            '--nologo'
        )

        $publishedSetup = Join-Path $setupPublishDirectory 'SpaceLens-Setup.exe'
        $unsignedSetup = Join-Path $unsignedSetupDirectory 'SpaceLens-Setup.exe'
        if (-not (Test-Path -LiteralPath $publishedSetup -PathType Leaf)) {
            throw "Published Setup is missing: $publishedSetup"
        }
        Copy-Item -LiteralPath $publishedSetup -Destination $unsignedSetup
        Assert-UnsignedExecutable -Path $unsignedSetup
        Invoke-PackagedExecutable -Path $unsignedSetup -Description 'Unsigned SpaceLens Setup packaged self-test'
        Assert-DirectoryContainsOnly -Directory $unsignedSetupDirectory -Names @('SpaceLens-Setup.exe')
    }

    'ValidateSignedSetup' {
        Assert-DirectoryContainsOnly -Directory $signedSetupDirectory -Names @('SpaceLens-Setup.exe')
        $signedSetup = Join-Path $signedSetupDirectory 'SpaceLens-Setup.exe'
        [void](Assert-TrustedAuthenticode -Path $signedSetup -OriginalFilename 'SpaceLens-Setup.dll')
        Invoke-PackagedExecutable -Path $signedSetup -Description 'Signed SpaceLens Setup packaged self-test'
        [void](Assert-TrustedAuthenticode -Path $signedSetup -OriginalFilename 'SpaceLens-Setup.dll')
    }

    'Assemble' {
        $signedApp = Join-Path $signedAppDirectory 'SpaceLens.exe'
        $signedSetup = Join-Path $signedSetupDirectory 'SpaceLens-Setup.exe'
        $initialAppHash = (Get-FileHash -LiteralPath $signedApp -Algorithm SHA256).Hash.ToUpperInvariant()
        $initialSetupHash = (Get-FileHash -LiteralPath $signedSetup -Algorithm SHA256).Hash.ToUpperInvariant()
        [void](Assert-TrustedAuthenticode -Path $signedApp -OriginalFilename 'SpaceLens.dll')
        [void](Assert-TrustedAuthenticode -Path $signedSetup -OriginalFilename 'SpaceLens-Setup.dll')
        Invoke-PackagedExecutable -Path $signedApp -Description 'Final signed SpaceLens self-test'
        Invoke-PackagedExecutable -Path $signedSetup -Description 'Final signed SpaceLens Setup self-test'
        $postTestAppHash = (Get-FileHash -LiteralPath $signedApp -Algorithm SHA256).Hash.ToUpperInvariant()
        $postTestSetupHash = (Get-FileHash -LiteralPath $signedSetup -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($postTestAppHash -ne $initialAppHash -or $postTestSetupHash -ne $initialSetupHash) {
            throw 'A signed executable changed while its packaged self-test was running.'
        }
        [void](Assert-TrustedAuthenticode -Path $signedApp -OriginalFilename 'SpaceLens.dll')
        [void](Assert-TrustedAuthenticode -Path $signedSetup -OriginalFilename 'SpaceLens-Setup.dll')

        [void](Reset-OwnedDirectory -Path $finalDirectory -Parent $signPathRoot)
        $finalApp = Join-Path $finalDirectory 'SpaceLens.exe'
        $finalSetup = Join-Path $finalDirectory 'SpaceLens-Setup.exe'
        Copy-Item -LiteralPath $signedApp -Destination $finalApp
        Copy-Item -LiteralPath $signedSetup -Destination $finalSetup
        $appHash = Write-HashFile -Path $finalApp
        $setupHash = Write-HashFile -Path $finalSetup
        if ($appHash -ne $postTestAppHash -or $setupHash -ne $postTestSetupHash) {
            throw 'A signed executable changed while the final workflow artifact was assembled.'
        }
        $appSignature = Assert-TrustedAuthenticode -Path $finalApp -OriginalFilename 'SpaceLens.dll'
        $setupSignature = Assert-TrustedAuthenticode -Path $finalSetup -OriginalFilename 'SpaceLens-Setup.dll'

        $sourceRepository = [Environment]::GetEnvironmentVariable('GITHUB_REPOSITORY')
        if ([string]::IsNullOrWhiteSpace($sourceRepository)) {
            $sourceRepository = 'Purxy8/SpaceLens'
        }
        $sourceCommit = Get-SourceCommit
        $serverUrl = [Environment]::GetEnvironmentVariable('GITHUB_SERVER_URL')
        if ([string]::IsNullOrWhiteSpace($serverUrl)) {
            $serverUrl = 'https://github.com'
        }
        $workflowRunId = [Environment]::GetEnvironmentVariable('GITHUB_RUN_ID')
        $workflowRunAttempt = [Environment]::GetEnvironmentVariable('GITHUB_RUN_ATTEMPT')
        $workflowUrl = if ($workflowRunId) { "$serverUrl/$sourceRepository/actions/runs/$workflowRunId" } else { $null }

        $metadata = [ordered]@{
            schemaVersion = 1
            product = 'SpaceLens'
            version = $version
            sourceRepository = "$serverUrl/$sourceRepository"
            sourceCommit = $sourceCommit
            sourceRef = [Environment]::GetEnvironmentVariable('GITHUB_REF')
            workflowRunId = $workflowRunId
            workflowRunAttempt = $workflowRunAttempt
            workflowUrl = $workflowUrl
            generatedUtc = (Get-Date).ToUniversalTime().ToString('O')
            signingRequests = [ordered]@{
                application = [Environment]::GetEnvironmentVariable('SIGNPATH_APP_REQUEST_ID')
                setup = [Environment]::GetEnvironmentVariable('SIGNPATH_SETUP_REQUEST_ID')
            }
            artifacts = @(
                [ordered]@{
                    name = 'SpaceLens.exe'
                    sizeBytes = (Get-Item -LiteralPath $finalApp).Length
                    sha256 = $appHash
                    authenticode = (Get-SignerMetadata -Signature $appSignature)
                },
                [ordered]@{
                    name = 'SpaceLens-Setup.exe'
                    sizeBytes = (Get-Item -LiteralPath $finalSetup).Length
                    sha256 = $setupHash
                    authenticode = (Get-SignerMetadata -Signature $setupSignature)
                }
            )
        }
        $metadataJson = $metadata | ConvertTo-Json -Depth 8
        [IO.File]::WriteAllText(
            (Join-Path $finalDirectory 'release-metadata.json'),
            $metadataJson + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false)
        )

        Assert-DirectoryContainsOnly -Directory $finalDirectory -Names @(
            'SpaceLens.exe',
            'SpaceLens.exe.sha256',
            'SpaceLens-Setup.exe',
            'SpaceLens-Setup.exe.sha256',
            'release-metadata.json'
        )
        Write-Host "Trusted SignPath release artifact for SpaceLens $version is ready in $finalDirectory" -ForegroundColor Green
    }
}
