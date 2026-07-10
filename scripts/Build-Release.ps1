[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$SigningKey = '',
    [string]$NuGetConfig = '',
    [ValidateLength(1, 4000)]
    [string]$Notes = 'Includes SpaceLens improvements and fixes.'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$versionProperties = Join-Path $repository 'Directory.Build.props'
$appProject = Join-Path $repository 'src\SpaceLens\SpaceLens.csproj'
$setupProject = Join-Path $repository 'src\SpaceLens.Setup\SpaceLens.Setup.csproj'
$signerProject = Join-Path $repository 'tools\ReleaseSigner\ReleaseSigner.csproj'
$publicKey = Join-Path $repository 'src\SpaceLens\assets\update-public-key.pem'

foreach ($required in @($versionProperties, $appProject, $setupProject, $signerProject, $publicKey)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required file is missing: $required"
    }
}

[xml]$propertiesDocument = Get-Content -Raw -LiteralPath $versionProperties
$versionNodes = @($propertiesDocument.SelectNodes('/Project/PropertyGroup/SpaceLensVersion'))
if ($versionNodes.Count -ne 1) {
    throw 'Directory.Build.props must contain exactly one SpaceLensVersion element.'
}

$declaredVersion = $versionNodes[0].InnerText.Trim()
$semanticVersionPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$'
if ($declaredVersion -notmatch $semanticVersionPattern) {
    throw "Directory.Build.props contains an invalid SpaceLensVersion: $declaredVersion"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $declaredVersion
}
elseif ($Version -notmatch $semanticVersionPattern) {
    throw "Version must use strict MAJOR.MINOR.PATCH format: $Version"
}
elseif (-not [string]::Equals($Version, $declaredVersion, [StringComparison]::Ordinal)) {
    throw "Requested version $Version does not match Directory.Build.props version $declaredVersion."
}

$releaseNotes = Join-Path $repository "release-notes\v$Version.md"
if (-not (Test-Path -LiteralPath $releaseNotes -PathType Leaf)) {
    throw "Release notes are missing: $releaseNotes"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required and dotnet was not found on PATH.'
}

$resolvedKey = ''
$sourceCommit = ''
if ($SigningKey) {
    if (-not (Test-Path -LiteralPath $SigningKey -PathType Leaf)) {
        throw "Signing key is not a file: $SigningKey"
    }
    $resolvedKey = (Resolve-Path -LiteralPath $SigningKey).Path
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw 'Git is required for a signed release so source provenance can be verified.'
    }
    $gitSafety = "safe.directory=$($repository.Replace('\', '/'))"
    $workingChanges = @(& git -c $gitSafety -C $repository status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Git could not inspect the release source tree.' }
    if ($workingChanges.Count -ne 0) { throw "Signed releases require a clean committed source tree.`n$($workingChanges -join [Environment]::NewLine)" }
    $sourceCommit = (& git -c $gitSafety -C $repository rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'Git could not resolve the release source commit.' }
    Write-Host "Preparing signed release from commit $sourceCommit"
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
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to manage a path outside $fullParent`: $fullPath"
    }

    return $fullPath
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
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $safePath | Out-Null
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
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Description,
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 60
    )

    foreach ($argument in $Arguments) {
        if ($argument.Contains('"')) {
            throw "$Description contains an unsupported quote in an argument."
        }
    }
    $argumentLine = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath $Path -ArgumentList $argumentLine -PassThru -WindowStyle Hidden
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
}

function Assert-HashFile {
    param([Parameter(Mandatory)][string]$Path)

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    $name = [IO.Path]::GetFileName($Path)
    $expected = "$hash  $name`r`n"
    $actual = [IO.File]::ReadAllText("$Path.sha256", [Text.Encoding]::ASCII)
    if (-not [string]::Equals($actual, $expected, [StringComparison]::Ordinal)) {
        throw "SHA-256 sidecar does not match $name."
    }
}

$artifacts = Get-OwnedChildPath -Path $artifacts -Parent $repository
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$intermediate = Reset-OwnedDirectory -Path (Join-Path $artifacts 'intermediate') -Parent $artifacts
$releaseRoot = Join-Path $artifacts 'release'
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
$releaseDirectory = Get-OwnedChildPath -Path (Join-Path $releaseRoot $Version) -Parent $releaseRoot

$appPublish = Join-Path $intermediate 'app-publish'
$setupPublish = Join-Path $intermediate 'setup-publish'
$package = Join-Path $intermediate 'package'
New-Item -ItemType Directory -Force -Path $appPublish, $setupPublish, $package | Out-Null

$resolvedNuGetConfig = ''
$appRestoreArguments = @('restore', $appProject, '-r', 'win-x64', '--nologo')
if ($NuGetConfig) {
    $resolvedNuGetConfig = (Resolve-Path -LiteralPath $NuGetConfig).Path
    if (-not (Test-Path -LiteralPath $resolvedNuGetConfig -PathType Leaf)) {
        throw "NuGet configuration is not a file: $resolvedNuGetConfig"
    }
    $appRestoreArguments += @('--configfile', $resolvedNuGetConfig)
}
Invoke-DotNet -Arguments $appRestoreArguments

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
    '-o', $appPublish,
    '--no-restore',
    '--nologo'
)

$publishedApp = Join-Path $appPublish 'SpaceLens.exe'
$app = Join-Path $package 'SpaceLens.exe'
Copy-Item -LiteralPath $publishedApp -Destination $app
Write-HashFile -Path $app
Invoke-PackagedExecutable -Path $app -Arguments @('--self-test') -Description 'SpaceLens packaged self-test'

$payloadProperty = "-p:SpaceLensPayload=$app"
$setupRestoreArguments = @('restore', $setupProject, '-r', 'win-x64', $payloadProperty, '--nologo')
if ($resolvedNuGetConfig) {
    $setupRestoreArguments += @('--configfile', $resolvedNuGetConfig)
}
Invoke-DotNet -Arguments $setupRestoreArguments

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
    '-o', $setupPublish,
    '--no-restore',
    '--nologo'
)

$publishedSetup = Join-Path $setupPublish 'SpaceLens-Setup.exe'
$setup = Join-Path $package 'SpaceLens-Setup.exe'
Copy-Item -LiteralPath $publishedSetup -Destination $setup
Write-HashFile -Path $setup
Invoke-PackagedExecutable -Path $setup -Arguments @('--self-test') -Description 'SpaceLens Setup packaged self-test'

Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $package 'RELEASE-NOTES.md')

$setupInfo = Get-Item -LiteralPath $setup
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToUpperInvariant()
$unsignedManifest = Join-Path $intermediate 'update.unsigned.json'
$signedManifest = Join-Path $package 'update.json'
$manifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    tag = "v$Version"
    assetName = 'SpaceLens-Setup.exe'
    sha256 = $setupHash
    sizeBytes = $setupInfo.Length
    publishedUtc = (Get-Date).ToUniversalTime().ToString('O')
    notes = $Notes
    signature = ''
}
$manifestJson = $manifest | ConvertTo-Json
[IO.File]::WriteAllText($unsignedManifest, $manifestJson + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

if (-not $SigningKey) {
    Write-Warning "Unsigned build and packaged self-tests succeeded. Intermediate files are in $intermediate; no final release directory was created."
    return
}

Invoke-DotNet -Arguments @('run', '--project', $signerProject, '-c', 'Release', '--', 'sign', $resolvedKey, $unsignedManifest, $signedManifest)
Invoke-DotNet -Arguments @('run', '--project', $signerProject, '-c', 'Release', '--', 'verify', $publicKey, $signedManifest)
Invoke-PackagedExecutable `
    -Path $app `
    -Arguments @('--verify-update-manifest', $signedManifest, '--installer', $setup) `
    -Description 'SpaceLens production update verifier'

$postBuildChanges = @(& git -c $gitSafety -C $repository status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $postBuildChanges.Count -ne 0) { throw 'The source tree changed while the signed release was building.' }
$postBuildCommit = (& git -c $gitSafety -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or -not [string]::Equals($postBuildCommit, $sourceCommit, [StringComparison]::OrdinalIgnoreCase)) { throw 'The source commit changed while the signed release was building.' }

$expectedReleaseAssets = @(
    'RELEASE-NOTES.md',
    'SpaceLens-Setup.exe',
    'SpaceLens-Setup.exe.sha256',
    'SpaceLens.exe',
    'SpaceLens.exe.sha256',
    'update.json'
) | Sort-Object
$releaseStaging = Get-OwnedChildPath -Path (Join-Path $releaseRoot (".$Version-" + [Guid]::NewGuid().ToString('N') + '.staging')) -Parent $releaseRoot
$previousRelease = ''
try {
    New-Item -ItemType Directory -Path $releaseStaging | Out-Null
    foreach ($assetName in $expectedReleaseAssets) {
        Copy-Item -LiteralPath (Join-Path $package $assetName) -Destination (Join-Path $releaseStaging $assetName)
    }

    Assert-HashFile -Path (Join-Path $releaseStaging 'SpaceLens.exe')
    Assert-HashFile -Path (Join-Path $releaseStaging 'SpaceLens-Setup.exe')

    $actualEntries = @(Get-ChildItem -LiteralPath $releaseStaging -Force)
    if (@($actualEntries | Where-Object { $_.PSIsContainer }).Count -ne 0) {
        throw 'The staged release unexpectedly contains a subdirectory.'
    }
    $actualReleaseAssets = @($actualEntries | ForEach-Object Name | Sort-Object)
    $assetDifference = @(Compare-Object -ReferenceObject $expectedReleaseAssets -DifferenceObject $actualReleaseAssets -CaseSensitive)
    if ($assetDifference.Count -ne 0) {
        throw "The staged release does not contain exactly the six expected assets: $($assetDifference | Out-String)"
    }

    if (Test-Path -LiteralPath $releaseDirectory) {
        $resolvedReleaseDirectory = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $releaseDirectory).Path)
        [void](Get-OwnedChildPath -Path $resolvedReleaseDirectory -Parent $releaseRoot)
        $previousRelease = Get-OwnedChildPath -Path (Join-Path $releaseRoot (".$Version-" + [Guid]::NewGuid().ToString('N') + '.previous')) -Parent $releaseRoot
        Move-Item -LiteralPath $resolvedReleaseDirectory -Destination $previousRelease
    }
    try {
        [void](Get-OwnedChildPath -Path $releaseStaging -Parent $releaseRoot)
        [void](Get-OwnedChildPath -Path $releaseDirectory -Parent $releaseRoot)
        Move-Item -LiteralPath $releaseStaging -Destination $releaseDirectory
    }
    catch {
        if ($previousRelease -and (Test-Path -LiteralPath $previousRelease) -and -not (Test-Path -LiteralPath $releaseDirectory)) {
            [void](Get-OwnedChildPath -Path $previousRelease -Parent $releaseRoot)
            Move-Item -LiteralPath $previousRelease -Destination $releaseDirectory
        }
        throw
    }
    if ($previousRelease -and (Test-Path -LiteralPath $previousRelease)) {
        $resolvedPrevious = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $previousRelease).Path)
        [void](Get-OwnedChildPath -Path $resolvedPrevious -Parent $releaseRoot)
        Remove-Item -LiteralPath $resolvedPrevious -Recurse -Force
    }
}
finally {
    if (Test-Path -LiteralPath $releaseStaging) {
        $resolvedStaging = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $releaseStaging).Path)
        [void](Get-OwnedChildPath -Path $resolvedStaging -Parent $releaseRoot)
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}

Write-Host "Signed and production-verified release v$Version from $sourceCommit is ready in $releaseDirectory" -ForegroundColor Green
