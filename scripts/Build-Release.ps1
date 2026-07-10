[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version = '1.1.0',
    [string]$SigningKey = '',
    [string]$NuGetConfig = '',
    [ValidateLength(1, 4000)]
    [string]$Notes = 'Includes SpaceLens improvements and fixes.'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$expectedPrefix = $repository.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $artifacts.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The artifacts path is outside the repository.'
}

$appProject = Join-Path $repository 'src\SpaceLens\SpaceLens.csproj'
$setupProject = Join-Path $repository 'src\SpaceLens.Setup\SpaceLens.Setup.csproj'
$updaterSource = Join-Path $repository 'src\SpaceLens\UpdateService.cs'
$signerProject = Join-Path $repository 'tools\ReleaseSigner\ReleaseSigner.csproj'
$publicKey = Join-Path $repository 'src\SpaceLens\assets\update-public-key.pem'
$releaseNotes = Join-Path $repository "release-notes\v$Version.md"

foreach ($required in @($appProject, $setupProject, $updaterSource, $signerProject, $publicKey, $releaseNotes)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required file is missing: $required"
    }
}

$escapedVersion = [Regex]::Escape($Version)
foreach ($project in @($appProject, $setupProject)) {
    if ((Get-Content -Raw -LiteralPath $project) -notmatch "<Version>$escapedVersion</Version>") {
        throw "Project version does not match $Version`: $project"
    }
}
if ((Get-Content -Raw -LiteralPath $updaterSource) -notmatch "CurrentVersionText\s*=\s*`"$escapedVersion`"") {
    throw "UpdateService.CurrentVersionText does not match $Version."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 10 SDK is required and dotnet was not found on PATH.'
}

if (Test-Path -LiteralPath $artifacts) {
    $resolvedArtifacts = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $artifacts).Path)
    if (-not $resolvedArtifacts.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove an artifacts directory outside the repository.'
    }
    Remove-Item -LiteralPath $resolvedArtifacts -Recurse -Force
}

$appPublish = Join-Path $artifacts 'app-publish'
$setupPublish = Join-Path $artifacts 'setup-publish'
New-Item -ItemType Directory -Force -Path $appPublish, $setupPublish | Out-Null

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Write-HashFile {
    param([Parameter(Mandatory)][string]$Path)
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    $name = [IO.Path]::GetFileName($Path)
    [IO.File]::WriteAllText("$Path.sha256", "$hash  $name`r`n", [Text.Encoding]::ASCII)
}

$appRestoreArguments = @('restore', $appProject, '-r', 'win-x64', '--nologo')
if ($NuGetConfig) {
    $resolvedNuGetConfig = (Resolve-Path -LiteralPath $NuGetConfig).Path
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
$app = Join-Path $artifacts 'SpaceLens.exe'
Copy-Item -LiteralPath $publishedApp -Destination $app
Write-HashFile -Path $app

& $app '--self-test'
if ($LASTEXITCODE -ne 0) {
    throw "SpaceLens packaged self-test failed with exit code $LASTEXITCODE."
}

$payloadProperty = "-p:SpaceLensPayload=$app"
$setupRestoreArguments = @('restore', $setupProject, '-r', 'win-x64', $payloadProperty, '--nologo')
if ($NuGetConfig) {
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
$setup = Join-Path $artifacts 'SpaceLens-Setup.exe'
Copy-Item -LiteralPath $publishedSetup -Destination $setup
Write-HashFile -Path $setup

& $setup '--self-test'
if ($LASTEXITCODE -ne 0) {
    throw "SpaceLens Setup packaged self-test failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $artifacts 'RELEASE-NOTES.md')

$setupInfo = Get-Item -LiteralPath $setup
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToUpperInvariant()
$unsignedManifest = Join-Path $artifacts 'update.unsigned.json'
$signedManifest = Join-Path $artifacts 'update.json'
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
$manifest | ConvertTo-Json | Set-Content -LiteralPath $unsignedManifest -Encoding utf8

if ($SigningKey) {
    $resolvedKey = (Resolve-Path -LiteralPath $SigningKey).Path
    Invoke-DotNet -Arguments @('run', '--project', $signerProject, '-c', 'Release', '--', 'sign', $resolvedKey, $unsignedManifest, $signedManifest)
    Invoke-DotNet -Arguments @('run', '--project', $signerProject, '-c', 'Release', '--', 'verify', $publicKey, $signedManifest)
    Write-Host "Signed release is ready in $artifacts" -ForegroundColor Green
}
else {
    Write-Warning 'No signing key was supplied. Binaries and hashes are ready, but update.json was not created.'
}
