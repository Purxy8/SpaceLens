[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$NuGetConfig = '',
    [switch]$PrepareOfflineRelease,
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
$restrictedLauncherProject = Join-Path $repository 'tools\RestrictedProcessLauncher\RestrictedProcessLauncher.csproj'
$publicKey = Join-Path $repository 'src\SpaceLens\assets\update-public-key.pem'
$releaseSecurityModule = Join-Path $PSScriptRoot 'ReleaseSecurity.psm1'

foreach ($required in @($versionProperties, $appProject, $setupProject, $signerProject, $restrictedLauncherProject, $publicKey, $releaseSecurityModule)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required file is missing: $required"
    }
}

Import-Module $releaseSecurityModule -Force
Assert-PublicSpkiPemFile -Path $publicKey | Out-Null

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

$sourceProvenance = $null
if ($PrepareOfflineRelease) {
    $sourceProvenance = Get-CleanReleaseProvenance -Repository $repository
    Write-Host "Preparing offline release inputs from commit $($sourceProvenance.Commit)"
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

function Reset-OwnedDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Parent
    )

    $safePath = Get-OwnedChildPath -Path $Path -Parent $Parent
    if (Test-Path -LiteralPath $safePath) {
        $resolvedPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $safePath).Path)
        [void](Get-OwnedChildPath -Path $resolvedPath -Parent $Parent)
        Assert-ReleasePathHasNoReparsePoints -Path $resolvedPath -StopAt $repository
        Assert-ReleaseTreeHasNoReparsePoints -Path $resolvedPath
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $safePath | Out-Null
    Assert-ReleasePathHasNoReparsePoints -Path $safePath -StopAt $repository
    return $safePath
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Test-CurrentProcessAdministrator {
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try { return [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
        finally { $identity.Dispose() }
    }
    catch { throw "Could not determine build-host elevation safely: $($_.Exception.Message)" }
}

$script:restrictedTestLauncher = ''

function Test-ManagedInjectionEnvironmentName {
    param([Parameter(Mandatory)][string]$Name)
    foreach ($prefix in @('COR_', 'CORECLR_', 'COMPlus_', 'DOTNET_', 'SPACELENS_')) {
        if ($Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Invoke-PackagedExecutable {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Description,
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 60
    )

    $launchPath = $Path
    $launchArguments = $Arguments
    if ($script:restrictedTestLauncher) {
        if ($Arguments.Count -ne 1 -or -not [string]::Equals($Arguments[0], '--self-test', [StringComparison]::Ordinal)) {
            throw 'The restricted CI launcher accepts only the exact packaged --self-test command.'
        }
        $launchPath = $script:restrictedTestLauncher
        $launchArguments = @([IO.Path]::GetFullPath($Path), '--self-test')
    }

    foreach ($argument in $launchArguments) {
        if ($argument.Contains('"')) {
            throw "$Description contains an unsupported quote in an argument."
        }
    }
    $argumentLine = ($launchArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $savedInjectionEnvironment = @{}
    try {
        if ($script:restrictedTestLauncher) {
            foreach ($entry in [Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Process).GetEnumerator()) {
                $name = [string]$entry.Key
                if (Test-ManagedInjectionEnvironmentName -Name $name) {
                    $savedInjectionEnvironment[$name] = [string]$entry.Value
                    [Environment]::SetEnvironmentVariable($name, $null, [EnvironmentVariableTarget]::Process)
                }
            }
        }
        $process = Start-Process -FilePath $launchPath -ArgumentList $argumentLine -PassThru -WindowStyle Hidden
    }
    finally {
        foreach ($entry in $savedInjectionEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable([string]$entry.Key, [string]$entry.Value, [EnvironmentVariableTarget]::Process)
        }
    }
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
Assert-ReleasePathHasNoReparsePoints -Path $artifacts -StopAt $repository
$intermediate = Reset-OwnedDirectory -Path (Join-Path $artifacts 'intermediate') -Parent $artifacts

$appPublish = Join-Path $intermediate 'app-publish'
$setupPublish = Join-Path $intermediate 'setup-publish'
$package = Join-Path $intermediate 'package'
$signerPublish = Join-Path $intermediate 'native-release-signer'
New-Item -ItemType Directory -Force -Path $appPublish, $setupPublish, $package, $signerPublish | Out-Null

$resolvedNuGetConfig = ''
$appRestoreArguments = @('restore', $appProject, '-r', 'win-x64', '-p:SpaceLensEnableDiagnostics=false', '--nologo')
if ($NuGetConfig) {
    $resolvedNuGetConfig = (Resolve-Path -LiteralPath $NuGetConfig).Path
    if (-not (Test-Path -LiteralPath $resolvedNuGetConfig -PathType Leaf)) {
        throw "NuGet configuration is not a file: $resolvedNuGetConfig"
    }
    $appRestoreArguments += @('--configfile', $resolvedNuGetConfig)
}

if (Test-CurrentProcessAdministrator) {
    $restrictedLauncherPublish = Join-Path $intermediate 'restricted-process-launcher'
    New-Item -ItemType Directory -Force -Path $restrictedLauncherPublish | Out-Null
    $launcherRestoreArguments = @('restore', $restrictedLauncherProject, '-r', 'win-x64', '--nologo')
    if ($resolvedNuGetConfig) { $launcherRestoreArguments += @('--configfile', $resolvedNuGetConfig) }
    Invoke-DotNet -Arguments $launcherRestoreArguments
    Invoke-DotNet -Arguments @(
        'publish', $restrictedLauncherProject,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $restrictedLauncherPublish,
        '--no-restore',
        '--nologo'
    )
    $script:restrictedTestLauncher = Join-Path $restrictedLauncherPublish 'RestrictedProcessLauncher.exe'
    $launcherItem = Get-Item -LiteralPath $script:restrictedTestLauncher -Force
    if ($launcherItem.PSIsContainer -or ($launcherItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $launcherItem.Length -le 0) {
        throw 'The restricted packaged-self-test launcher is missing or unsafe.'
    }
    Write-Host 'Elevated build host detected; packaged self-tests will run under a verified restricted LUA token.'
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
    '-p:SpaceLensEnableDiagnostics=false',
    '-o', $appPublish,
    '--no-restore',
    '--nologo'
)

$publishedApp = Join-Path $appPublish 'SpaceLens.exe'
$app = Join-Path $package 'SpaceLens.exe'
Copy-Item -LiteralPath $publishedApp -Destination $app
Write-HashFile -Path $app
Invoke-PackagedExecutable -Path $app -Arguments @('--self-test') -Description 'SpaceLens packaged self-test' -TimeoutSeconds 180
$startupHookTest = Join-Path $PSScriptRoot 'Test-StartupHookIsolation.ps1'
if ($script:restrictedTestLauncher) {
    & $startupHookTest -Executable $app -WorkDirectory $intermediate -RestrictedLauncher $script:restrictedTestLauncher
}
else { & $startupHookTest -Executable $app -WorkDirectory $intermediate }

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
Invoke-PackagedExecutable -Path $setup -Arguments @('--self-test') -Description 'SpaceLens Setup packaged self-test' -TimeoutSeconds 180

Invoke-DotNet -Arguments @(
    'publish', $signerProject,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishAot=true',
    '-p:DebugSymbols=false',
    '-o', $signerPublish,
    '--nologo'
)
$nativeSigner = Join-Path $signerPublish 'ReleaseSigner.exe'
Assert-NativeReleaseSignerDirectory -ExecutablePath $nativeSigner
$nativeSignerLock = Open-VerifiedReleaseExecutable -Path $nativeSigner
try {
    Invoke-LockedReleaseSigner -Lock $nativeSignerLock -Arguments @('self-test')
    $nativeSignerHash = $nativeSignerLock.Sha256
}
finally { $nativeSignerLock.Stream.Dispose() }
[IO.File]::WriteAllText(
    (Join-Path $intermediate 'ReleaseSigner.exe.sha256'),
    "$nativeSignerHash  ReleaseSigner.exe`r`n",
    [Text.Encoding]::ASCII
)

Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $package 'RELEASE-NOTES.md')

$setupInfo = Get-Item -LiteralPath $setup
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToUpperInvariant()
$unsignedManifest = Join-Path $intermediate 'update.unsigned.json'
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

if (-not $PrepareOfflineRelease) {
    Write-Host "Unsigned build and packaged self-tests succeeded. Intermediate files are in $intermediate." -ForegroundColor Green
    return
}

$postBuildProvenance = Get-CleanReleaseProvenance -Repository $repository
if (-not [string]::Equals($postBuildProvenance.Commit, $sourceProvenance.Commit, [StringComparison]::Ordinal)) {
    throw 'The source commit changed while release inputs were being built.'
}

$preparedDirectory = Join-Path $intermediate 'prepared-release'
New-Item -ItemType Directory -Path $preparedDirectory | Out-Null
$preparedInputNames = @(
    'RELEASE-NOTES.md',
    'SpaceLens-Setup.exe',
    'SpaceLens-Setup.exe.sha256',
    'SpaceLens.exe',
    'SpaceLens.exe.sha256',
    'update.unsigned.json'
) | Sort-Object

foreach ($name in $preparedInputNames) {
    $source = if ($name -eq 'update.unsigned.json') { $unsignedManifest } else { Join-Path $package $name }
    Copy-Item -LiteralPath $source -Destination (Join-Path $preparedDirectory $name)
}

$inputRecords = @($preparedInputNames | ForEach-Object {
    Get-ReleaseFileRecord -Path (Join-Path $preparedDirectory $_)
})
$provenance = [ordered]@{
    schemaVersion = 1
    product = 'SpaceLens'
    version = $Version
    sourceRepository = 'https://github.com/Purxy8/SpaceLens'
    sourceCommit = $sourceProvenance.Commit
    generatedUtc = (Get-Date).ToUniversalTime().ToString('O')
    inputs = $inputRecords
}
[IO.File]::WriteAllText(
    (Join-Path $preparedDirectory 'release-provenance.json'),
    (($provenance | ConvertTo-Json -Depth 5) + [Environment]::NewLine),
    [Text.UTF8Encoding]::new($false)
)

$preparedZip = Join-Path $intermediate "SpaceLens-release-input-v$Version.zip"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $preparedDirectory,
    $preparedZip,
    [IO.Compression.CompressionLevel]::Optimal,
    $false
)
$preparedDigest = (Get-FileHash -LiteralPath $preparedZip -Algorithm SHA256).Hash.ToUpperInvariant()
[IO.File]::WriteAllText("$preparedZip.sha256", "$preparedDigest  $([IO.Path]::GetFileName($preparedZip))`r`n", [Text.Encoding]::ASCII)

$finalProvenance = Get-CleanReleaseProvenance -Repository $repository
if (-not [string]::Equals($finalProvenance.Commit, $sourceProvenance.Commit, [StringComparison]::Ordinal)) {
    throw 'The source commit changed while the prepared release archive was being created.'
}

Write-Host "Prepared and self-tested release inputs for v$Version from $($sourceProvenance.Commit):" -ForegroundColor Green
Write-Host $preparedZip
Write-Host "SHA-256: $preparedDigest"
Write-Host 'Finalize on the offline signing machine with Finalize-Release.ps1. Build-Release.ps1 never accesses a private key.'
