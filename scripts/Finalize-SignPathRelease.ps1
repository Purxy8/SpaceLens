[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SignedArtifactZip,
    [Parameter(Mandatory)][ValidatePattern('^(sha256:)?[0-9A-Fa-f]{64}$')][string]$ExpectedArtifactDigest,
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string]$ExpectedWorkflowRunId,
    [Parameter(Mandatory)][string]$SigningKey,
    [ValidateLength(1, 4000)][string]$Notes = 'Includes SpaceLens improvements and fixes.'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$releaseRoot = Join-Path $artifacts 'release'
$propertiesPath = Join-Path $repository 'Directory.Build.props'
$signerProject = Join-Path $repository 'tools\ReleaseSigner\ReleaseSigner.csproj'
$publicKey = Join-Path $repository 'src\SpaceLens\assets\update-public-key.pem'

foreach ($required in @($propertiesPath, $signerProject, $publicKey, $SigningKey)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file is missing: $required" }
}
if (-not (Test-Path -LiteralPath $SignedArtifactZip -PathType Leaf)) { throw "Signed artifact ZIP is missing: $SignedArtifactZip" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK is required and dotnet was not found on PATH.' }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is required to verify signed build provenance.' }

[xml]$properties = Get-Content -Raw -LiteralPath $propertiesPath
$versionNodes = @($properties.SelectNodes('/Project/PropertyGroup/SpaceLensVersion'))
if ($versionNodes.Count -ne 1) { throw 'Directory.Build.props must contain exactly one SpaceLensVersion element.' }
$version = $versionNodes[0].InnerText.Trim()
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "Invalid SpaceLens version: $version" }

$releaseNotes = Join-Path $repository "release-notes\v$version.md"
if (-not (Test-Path -LiteralPath $releaseNotes -PathType Leaf)) { throw "Release notes are missing: $releaseNotes" }

function Get-OwnedChildPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Parent)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    if (-not $fullPath.StartsWith($fullParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to manage a path outside $fullParent`: $fullPath" }
    return $fullPath
}

function Assert-NoReparsePointTree {
    param([Parameter(Mandatory)][string]$Path)
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Path))
    while ($pending.Count -ne 0) {
        $current = $pending.Pop()
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing to recursively manage a reparse point: $current" }
        if ($item.PSIsContainer) { foreach ($child in Get-ChildItem -LiteralPath $current -Force) { $pending.Push($child.FullName) } }
    }
}

function Assert-DirectoryContainsOnly {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string[]]$Names)
    $entries = @(Get-ChildItem -LiteralPath $Directory -Force)
    if (@($entries | Where-Object PSIsContainer).Count -ne 0) { throw "Unexpected subdirectory in $Directory." }
    $difference = @(Compare-Object -ReferenceObject @($Names | Sort-Object) -DifferenceObject @($entries.Name | Sort-Object) -CaseSensitive)
    if ($difference.Count -ne 0) { throw "Unexpected signed artifact contents: $($difference | Out-String)" }
}

function Assert-HashFile {
    param([Parameter(Mandatory)][string]$Path)
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    $name = [IO.Path]::GetFileName($Path)
    $expected = "$hash  $name`r`n"
    $actual = [IO.File]::ReadAllText("$Path.sha256", [Text.Encoding]::ASCII)
    if (-not [string]::Equals($actual, $expected, [StringComparison]::Ordinal)) { throw "SHA-256 sidecar does not match $name." }
    return $hash
}

function Write-HashFile {
    param([Parameter(Mandatory)][string]$Path)
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    [IO.File]::WriteAllText("$Path.sha256", "$hash  $([IO.Path]::GetFileName($Path))`r`n", [Text.Encoding]::ASCII)
    return $hash
}

function Assert-SignPathExecutable {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$OriginalFilename)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or $null -eq $signature.SignerCertificate) { throw "Authenticode validation failed for $([IO.Path]::GetFileName($Path)): $($signature.Status) - $($signature.StatusMessage)" }
    $publisher = $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    if (-not [string]::Equals($publisher, 'SignPath Foundation', [StringComparison]::Ordinal)) { throw "$([IO.Path]::GetFileName($Path)) is validly signed, but not by SignPath Foundation." }
    if ($null -eq $signature.TimeStamperCertificate) { throw "$([IO.Path]::GetFileName($Path)) does not have a trusted timestamp." }
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    if ($info.ProductName -ne 'SpaceLens' -or $info.ProductVersion -ne $version -or $info.FileVersion -ne "$version.0" -or $info.CompanyName -ne 'SpaceLens' -or $info.OriginalFilename -ne $OriginalFilename) { throw "$([IO.Path]::GetFileName($Path)) metadata does not match the approved SpaceLens artifact configuration." }
    return $signature
}

function Invoke-PackagedSelfTest {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Description)
    $process = Start-Process -FilePath $Path -ArgumentList '"--self-test"' -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(90000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; throw "$Description timed out." }
        $process.Refresh(); $exitCode = $process.ExitCode
    }
    finally { $process.Dispose() }
    if ($exitCode -ne 0) { throw "$Description failed with exit code $exitCode." }
}

New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$resolvedZip = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $SignedArtifactZip).Path)
if ((Get-Item -LiteralPath $resolvedZip).Length -gt 500MB) { throw 'The signed GitHub artifact ZIP is unexpectedly large.' }
$actualArtifactDigest = (Get-FileHash -LiteralPath $resolvedZip -Algorithm SHA256).Hash.ToUpperInvariant()
$normalizedExpectedDigest = if ($ExpectedArtifactDigest.StartsWith('sha256:', [StringComparison]::OrdinalIgnoreCase)) { $ExpectedArtifactDigest.Substring(7).ToUpperInvariant() } else { $ExpectedArtifactDigest.ToUpperInvariant() }
if (-not [string]::Equals($actualArtifactDigest, $normalizedExpectedDigest, [StringComparison]::Ordinal)) { throw 'The downloaded artifact does not match the SHA-256 shown in the trusted GitHub workflow summary.' }

$resolvedInput = Get-OwnedChildPath -Path (Join-Path $artifacts ('.signpath-input-' + [Guid]::NewGuid().ToString('N'))) -Parent $artifacts
New-Item -ItemType Directory -Path $resolvedInput | Out-Null
$inputNames = @('SpaceLens.exe', 'SpaceLens.exe.sha256', 'SpaceLens-Setup.exe', 'SpaceLens-Setup.exe.sha256', 'release-metadata.json')
try {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($resolvedZip)
    try {
    $entries = @($archive.Entries)
    if ($entries.Count -ne $inputNames.Count) { throw 'The signed GitHub artifact ZIP has an unexpected entry count.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $totalLength = 0L
    foreach ($entry in $entries) {
        if (-not $entry.Name -or -not [string]::Equals($entry.FullName, $entry.Name, [StringComparison]::Ordinal) -or -not $seen.Add($entry.Name) -or $entry.Length -gt 300MB) { throw "Unsafe or unexpected ZIP entry: $($entry.FullName)" }
        if ($entry.Length -gt (600MB - $totalLength)) { throw 'The expanded signed GitHub artifact is unexpectedly large.' }
        $totalLength += $entry.Length
        if ($totalLength -gt 600MB) { throw 'The expanded signed GitHub artifact is unexpectedly large.' }
    }
    $difference = @(Compare-Object -ReferenceObject @($inputNames | Sort-Object) -DifferenceObject @($entries.Name | Sort-Object) -CaseSensitive)
    if ($difference.Count -ne 0) { throw "The signed GitHub artifact ZIP has unexpected files: $($difference | Out-String)" }
    foreach ($entry in $entries) {
        $destination = Get-OwnedChildPath -Path (Join-Path $resolvedInput $entry.Name) -Parent $resolvedInput
        $source = $entry.Open(); $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $source.CopyTo($output); $output.Flush($true) } finally { $output.Dispose(); $source.Dispose() }
    }
    }
    finally { $archive.Dispose() }

Assert-DirectoryContainsOnly -Directory $resolvedInput -Names $inputNames
$inputApp = Join-Path $resolvedInput 'SpaceLens.exe'
$inputSetup = Join-Path $resolvedInput 'SpaceLens-Setup.exe'
$appHash = Assert-HashFile -Path $inputApp
$setupHash = Assert-HashFile -Path $inputSetup
$appSignature = Assert-SignPathExecutable -Path $inputApp -OriginalFilename 'SpaceLens.dll'
$setupSignature = Assert-SignPathExecutable -Path $inputSetup -OriginalFilename 'SpaceLens-Setup.dll'

$metadataPath = Join-Path $resolvedInput 'release-metadata.json'
$metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
if ($metadata.schemaVersion -ne 1 -or $metadata.product -ne 'SpaceLens' -or $metadata.version -ne $version) { throw 'SignPath release metadata identifies a different product or version.' }
$expectedRef = "refs/tags/v$version"
$expectedWorkflowUrl = "https://github.com/Purxy8/SpaceLens/actions/runs/$ExpectedWorkflowRunId"
if ($metadata.sourceRepository -ne 'https://github.com/Purxy8/SpaceLens' -or $metadata.sourceCommit -notmatch '^[0-9a-f]{40}$' -or $metadata.sourceRef -ne $expectedRef -or [string]$metadata.workflowRunId -ne $ExpectedWorkflowRunId -or [string]$metadata.workflowRunAttempt -notmatch '^[1-9][0-9]*$' -or $metadata.workflowUrl -ne $expectedWorkflowUrl) { throw 'SignPath release metadata has invalid GitHub source or workflow provenance.' }
$applicationRequestId = [Guid]::Empty
$setupRequestId = [Guid]::Empty
if (-not [Guid]::TryParseExact([string]$metadata.signingRequests.application, 'D', [ref]$applicationRequestId) -or
    $applicationRequestId -eq [Guid]::Empty -or
    -not [Guid]::TryParseExact([string]$metadata.signingRequests.setup, 'D', [ref]$setupRequestId) -or
    $setupRequestId -eq [Guid]::Empty) {
    throw 'SignPath release metadata is missing valid signing request IDs.'
}
foreach ($expected in @(@{ Name = 'SpaceLens.exe'; Hash = $appHash; Path = $inputApp; Signature = $appSignature }, @{ Name = 'SpaceLens-Setup.exe'; Hash = $setupHash; Path = $inputSetup; Signature = $setupSignature })) {
    $entry = @($metadata.artifacts | Where-Object name -eq $expected.Name)
    if ($entry.Count -ne 1 -or $entry[0].sha256 -ne $expected.Hash -or [long]$entry[0].sizeBytes -ne (Get-Item -LiteralPath $expected.Path).Length -or $entry[0].authenticode.thumbprint -ne $expected.Signature.SignerCertificate.Thumbprint) { throw "SignPath release metadata does not match $($expected.Name)." }
}

$gitSafety = "safe.directory=$($repository.Replace('\', '/'))"
$changes = @(& git -c $gitSafety -C $repository status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) { throw "Finalization requires a clean committed source tree.`n$($changes -join [Environment]::NewLine)" }
$sourceCommit = (& git -c $gitSafety -C $repository rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -ne $metadata.sourceCommit) { throw 'The local source commit does not match the GitHub workflow that produced the signed artifacts.' }

Invoke-PackagedSelfTest -Path $inputApp -Description 'Signed SpaceLens self-test'
Invoke-PackagedSelfTest -Path $inputSetup -Description 'Signed SpaceLens Setup self-test'
$postTestAppHash = Assert-HashFile -Path $inputApp
$postTestSetupHash = Assert-HashFile -Path $inputSetup
if ($postTestAppHash -ne $appHash -or $postTestSetupHash -ne $setupHash) { throw 'A signed executable changed while its packaged self-test was running.' }
$appSignature = Assert-SignPathExecutable -Path $inputApp -OriginalFilename 'SpaceLens.dll'
$setupSignature = Assert-SignPathExecutable -Path $inputSetup -OriginalFilename 'SpaceLens-Setup.dll'

New-Item -ItemType Directory -Path $artifacts, $releaseRoot -Force | Out-Null
$staging = Get-OwnedChildPath -Path (Join-Path $releaseRoot (".$version-" + [Guid]::NewGuid().ToString('N') + '.staging')) -Parent $releaseRoot
$releaseDirectory = Get-OwnedChildPath -Path (Join-Path $releaseRoot $version) -Parent $releaseRoot
$previous = ''
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    $app = Join-Path $staging 'SpaceLens.exe'; $setup = Join-Path $staging 'SpaceLens-Setup.exe'
    Copy-Item -LiteralPath $inputApp -Destination $app
    Copy-Item -LiteralPath $inputSetup -Destination $setup
    $stagedAppHash = Write-HashFile -Path $app; $stagedSetupHash = Write-HashFile -Path $setup
    if ($stagedAppHash -ne $appHash -or $stagedSetupHash -ne $setupHash) { throw 'A signed executable changed while the release was staged.' }
    [void](Assert-SignPathExecutable -Path $app -OriginalFilename 'SpaceLens.dll')
    [void](Assert-SignPathExecutable -Path $setup -OriginalFilename 'SpaceLens-Setup.dll')
    Copy-Item -LiteralPath $releaseNotes -Destination (Join-Path $staging 'RELEASE-NOTES.md')

    $unsignedManifest = Join-Path $staging 'update.unsigned.json'
    $signedManifest = Join-Path $staging 'update.json'
    $manifest = [ordered]@{
        schemaVersion = 1; version = $version; tag = "v$version"; assetName = 'SpaceLens-Setup.exe'; sha256 = $setupHash
        sizeBytes = (Get-Item -LiteralPath $setup).Length; publishedUtc = (Get-Date).ToUniversalTime().ToString('O'); notes = $Notes; signature = ''
    }
    [IO.File]::WriteAllText($unsignedManifest, (($manifest | ConvertTo-Json) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    & dotnet run --project $signerProject -c Release -- sign ([IO.Path]::GetFullPath($SigningKey)) $unsignedManifest $signedManifest
    if ($LASTEXITCODE -ne 0) { throw 'ReleaseSigner could not sign update.json.' }
    & dotnet run --project $signerProject -c Release -- verify $publicKey $signedManifest
    if ($LASTEXITCODE -ne 0) { throw 'ReleaseSigner could not verify update.json.' }
    Invoke-PackagedSelfTest -Path $app -Description 'Final SpaceLens self-test'
    Invoke-PackagedSelfTest -Path $setup -Description 'Final SpaceLens Setup self-test'
    $verify = Start-Process -FilePath $app -ArgumentList ('"--verify-update-manifest" "' + $signedManifest + '" "--installer" "' + $setup + '"') -PassThru -WindowStyle Hidden
    try { if (-not $verify.WaitForExit(90000)) { Stop-Process -Id $verify.Id -Force -ErrorAction SilentlyContinue; throw 'Production update verification timed out.' }; $verify.Refresh(); if ($verify.ExitCode -ne 0) { throw "Production update verification failed with exit code $($verify.ExitCode)." } } finally { $verify.Dispose() }
    $finalAppHash = Assert-HashFile -Path $app; $finalSetupHash = Assert-HashFile -Path $setup
    if ($finalAppHash -ne $appHash -or $finalSetupHash -ne $setupHash) { throw 'A signed executable changed during final verification.' }
    [void](Assert-SignPathExecutable -Path $app -OriginalFilename 'SpaceLens.dll')
    [void](Assert-SignPathExecutable -Path $setup -OriginalFilename 'SpaceLens-Setup.dll')
    & dotnet run --project $signerProject -c Release -- verify $publicKey $signedManifest
    if ($LASTEXITCODE -ne 0) { throw 'The final update.json no longer passes ReleaseSigner verification.' }
    Remove-Item -LiteralPath $unsignedManifest -Force

    Assert-DirectoryContainsOnly -Directory $staging -Names @('RELEASE-NOTES.md', 'SpaceLens.exe', 'SpaceLens.exe.sha256', 'SpaceLens-Setup.exe', 'SpaceLens-Setup.exe.sha256', 'update.json')
    if (Test-Path -LiteralPath $releaseDirectory) {
        $resolvedRelease = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $releaseDirectory).Path)
        [void](Get-OwnedChildPath -Path $resolvedRelease -Parent $releaseRoot)
        $previous = Get-OwnedChildPath -Path (Join-Path $releaseRoot (".$version-" + [Guid]::NewGuid().ToString('N') + '.previous')) -Parent $releaseRoot
        Move-Item -LiteralPath $resolvedRelease -Destination $previous
    }
    try {
        [void](Get-OwnedChildPath -Path $staging -Parent $releaseRoot); [void](Get-OwnedChildPath -Path $releaseDirectory -Parent $releaseRoot)
        Move-Item -LiteralPath $staging -Destination $releaseDirectory
    }
    catch {
        if ($previous -and (Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $releaseDirectory)) { [void](Get-OwnedChildPath -Path $previous -Parent $releaseRoot); Move-Item -LiteralPath $previous -Destination $releaseDirectory }
        throw
    }
    if ($previous -and (Test-Path -LiteralPath $previous)) {
        $resolvedPrevious = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $previous).Path)
        [void](Get-OwnedChildPath -Path $resolvedPrevious -Parent $releaseRoot)
        try { Assert-NoReparsePointTree -Path $resolvedPrevious; Remove-Item -LiteralPath $resolvedPrevious -Recurse -Force }
        catch { Write-Warning "The new release is valid and committed, but the previous backup could not be fully removed: $resolvedPrevious" }
    }
}
finally {
    if (Test-Path -LiteralPath $staging) {
        $resolvedStaging = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $staging).Path)
        [void](Get-OwnedChildPath -Path $resolvedStaging -Parent $releaseRoot)
        Assert-NoReparsePointTree -Path $resolvedStaging
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
}
finally {
    if (Test-Path -LiteralPath $resolvedInput) {
        $resolvedInputCleanup = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $resolvedInput).Path)
        [void](Get-OwnedChildPath -Path $resolvedInputCleanup -Parent $artifacts)
        Assert-NoReparsePointTree -Path $resolvedInputCleanup
        Remove-Item -LiteralPath $resolvedInputCleanup -Recurse -Force
    }
}

Write-Host "SignPath-signed and update-verified SpaceLens v$version from $sourceCommit is ready in $releaseDirectory" -ForegroundColor Green
