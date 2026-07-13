[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreparedReleaseZip,
    [Parameter(Mandatory)][ValidatePattern('^(sha256:)?[0-9A-Fa-f]{64}$')][string]$ExpectedPreparedDigest,
    [Parameter(Mandatory)][string]$ReleaseSigner,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedSignerSha256,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9 ._-]{1,98}[A-Za-z0-9]$')][string]$CngKeyName = 'SpaceLens Update Signing v2'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$releaseRoot = Join-Path $artifacts 'release'
$propertiesPath = Join-Path $repository 'Directory.Build.props'
$publicKey = Join-Path $repository 'src\SpaceLens\assets\update-public-key.pem'
$securityModule = Join-Path $PSScriptRoot 'ReleaseSecurity.psm1'

foreach ($required in @($propertiesPath, $publicKey, $securityModule, $PreparedReleaseZip, $ReleaseSigner)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file is missing: $required" }
}
Import-Module $securityModule -Force
Assert-PublicSpkiPemFile -Path $publicKey | Out-Null

# The CNG key is opened only by the single locked native signer invocation.
$resolvedKey = $CngKeyName
$signerMode = 'sign-release-cng'
$initialProvenance = Get-CleanReleaseProvenance -Repository $repository

[xml]$properties = Get-Content -Raw -LiteralPath $propertiesPath
$versionNodes = @($properties.SelectNodes('/Project/PropertyGroup/SpaceLensVersion'))
if ($versionNodes.Count -ne 1) { throw 'Directory.Build.props must contain exactly one SpaceLensVersion element.' }
$version = $versionNodes[0].InnerText.Trim()
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "Invalid SpaceLens version: $version" }

function Get-OwnedChildPath {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Parent)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    if (-not $fullPath.StartsWith($fullParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) {
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
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing to manage a reparse point: $current" }
        if ($item.PSIsContainer) { foreach ($child in Get-ChildItem -LiteralPath $current -Force) { $pending.Push($child.FullName) } }
    }
}

function Assert-DirectoryContainsOnly {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string[]]$Names)
    $entries = @(Get-ChildItem -LiteralPath $Directory -Force)
    if (@($entries | Where-Object PSIsContainer).Count -ne 0) { throw "Unexpected subdirectory in $Directory." }
    $difference = @(Compare-Object -ReferenceObject @($Names | Sort-Object) -DifferenceObject @($entries.Name | Sort-Object) -CaseSensitive)
    if ($difference.Count -ne 0) { throw "Unexpected release input contents: $($difference | Out-String)" }
}

function Assert-HashFile {
    param([Parameter(Mandatory)][string]$Path)
    $record = Get-ReleaseFileRecord -Path $Path
    $expected = "$($record.sha256)  $($record.name)`r`n"
    $actual = [IO.File]::ReadAllText("$Path.sha256", [Text.Encoding]::ASCII)
    if (-not [string]::Equals($actual, $expected, [StringComparison]::Ordinal)) { throw "SHA-256 sidecar does not match $($record.name)." }
    return $record
}

$expectedDigest = if ($ExpectedPreparedDigest.StartsWith('sha256:', [StringComparison]::OrdinalIgnoreCase)) { $ExpectedPreparedDigest.Substring(7) } else { $ExpectedPreparedDigest }

New-Item -ItemType Directory -Path $artifacts, $releaseRoot -Force | Out-Null
Assert-ReleasePathHasNoReparsePoints -Path $artifacts -StopAt $repository
Assert-ReleasePathHasNoReparsePoints -Path $releaseRoot -StopAt $repository
$inputDirectory = Get-OwnedChildPath -Path (Join-Path $artifacts ('.offline-input-' + [Guid]::NewGuid().ToString('N'))) -Parent $artifacts
$staging = Get-OwnedChildPath -Path (Join-Path $releaseRoot (".$version-" + [Guid]::NewGuid().ToString('N') + '.staging')) -Parent $releaseRoot
$releaseDirectory = Get-OwnedChildPath -Path (Join-Path $releaseRoot $version) -Parent $releaseRoot
$previous = ''
$signerLock = $null
$zipLock = $null
$inputNames = @('RELEASE-NOTES.md', 'SpaceLens.exe', 'SpaceLens.exe.sha256', 'SpaceLens-Setup.exe', 'SpaceLens-Setup.exe.sha256', 'update.unsigned.json', 'release-provenance.json')

try {
    $zipLock = Open-VerifiedReleaseFile -Path $PreparedReleaseZip -ExpectedSha256 $expectedDigest
    if ($zipLock.Stream.Length -le 0 -or $zipLock.Stream.Length -gt 500MB) { throw 'The prepared release ZIP is empty or unexpectedly large.' }
    $resolvedZip = $zipLock.FullPath
    $actualDigest = $zipLock.Sha256

    New-Item -ItemType Directory -Path $inputDirectory, $staging | Out-Null
    Assert-ReleasePathHasNoReparsePoints -Path $inputDirectory -StopAt $repository
    Assert-ReleasePathHasNoReparsePoints -Path $staging -StopAt $repository
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipLock.Stream.Position = 0
    $archive = [IO.Compression.ZipArchive]::new($zipLock.Stream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try {
        $entries = @($archive.Entries)
        if ($entries.Count -ne $inputNames.Count) { throw 'The prepared release ZIP has an unexpected entry count.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $totalLength = 0L
        foreach ($entry in $entries) {
            if (-not $entry.Name -or -not [string]::Equals($entry.FullName, $entry.Name, [StringComparison]::Ordinal) -or -not $seen.Add($entry.Name) -or $entry.Length -gt 300MB) {
                throw "Unsafe or unexpected ZIP entry: $($entry.FullName)"
            }
            $totalLength += $entry.Length
            if ($totalLength -gt 600MB) { throw 'The expanded prepared release is unexpectedly large.' }
        }
        $difference = @(Compare-Object -ReferenceObject @($inputNames | Sort-Object) -DifferenceObject @($entries.Name | Sort-Object) -CaseSensitive)
        if ($difference.Count -ne 0) { throw "The prepared release ZIP has unexpected files: $($difference | Out-String)" }
        $remainingExtractionBytes = 600MB
        foreach ($entry in $entries) {
            $destination = Get-OwnedChildPath -Path (Join-Path $inputDirectory $entry.Name) -Parent $inputDirectory
            Copy-ReleaseZipEntryExact -Entry $entry -Destination $destination -MaximumEntryBytes 300MB -RemainingTotalBytes ([ref]$remainingExtractionBytes)
        }
    }
    finally { $archive.Dispose() }

    Assert-DirectoryContainsOnly -Directory $inputDirectory -Names $inputNames
    $app = Join-Path $inputDirectory 'SpaceLens.exe'
    $setup = Join-Path $inputDirectory 'SpaceLens-Setup.exe'
    $appRecord = Assert-HashFile -Path $app
    $setupRecord = Assert-HashFile -Path $setup

    $metadata = Get-Content -Raw -LiteralPath (Join-Path $inputDirectory 'release-provenance.json') | ConvertFrom-Json
    if ($metadata.schemaVersion -ne 1 -or $metadata.product -ne 'SpaceLens' -or $metadata.version -ne $version -or
        $metadata.sourceRepository -ne 'https://github.com/Purxy8/SpaceLens' -or $metadata.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
        $metadata.sourceCommit -ne $initialProvenance.Commit) {
        throw 'Prepared release provenance identifies a different source, product, version, or commit.'
    }
    if (@($metadata.inputs).Count -ne 6) { throw 'Prepared release provenance has an unexpected input count.' }
    foreach ($name in $inputNames | Where-Object { $_ -ne 'release-provenance.json' }) {
        $record = @($metadata.inputs | Where-Object { $_.name -ceq $name })
        if ($record.Count -ne 1) { throw "Prepared release provenance is missing one exact record for $name." }
        [void](Assert-ReleaseFileRecord -Path (Join-Path $inputDirectory $name) -Record $record[0])
    }

    $manifestPath = Join-Path $inputDirectory 'update.unsigned.json'
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 32KB) { throw 'Unsigned update manifest is unexpectedly large.' }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.version -ne $version -or $manifest.tag -ne "v$version" -or
        $manifest.assetName -ne 'SpaceLens-Setup.exe' -or $manifest.sha256 -cne $setupRecord.sha256 -or
        [long]$manifest.sizeBytes -ne $setupRecord.sizeBytes -or $manifest.signature -ne '' -or
        $manifest.notes -isnot [string] -or $manifest.notes.Length -gt 4000) {
        throw 'Unsigned update manifest does not match the exact prepared Setup input.'
    }

    if ((Get-AuthenticodeSignature -LiteralPath $app).Status -ne [System.Management.Automation.SignatureStatus]::NotSigned -or
        (Get-AuthenticodeSignature -LiteralPath $setup).Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw 'The local offline release path accepts only the unsigned executables produced by Build-Release.ps1.'
    }

    # Every build and product execution occurs before the private key is opened.
    $signer = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ReleaseSigner).Path)
    Assert-NativeReleaseSignerDirectory -ExecutablePath $signer
    $signerLock = Open-VerifiedReleaseExecutable -Path $signer -ExpectedSha256 $ExpectedSignerSha256
    Invoke-LockedReleaseSigner -Lock $signerLock -Arguments @('self-test')

    Copy-Item -LiteralPath $app -Destination (Join-Path $staging 'SpaceLens.exe')
    Copy-Item -LiteralPath "$app.sha256" -Destination (Join-Path $staging 'SpaceLens.exe.sha256')
    Copy-Item -LiteralPath $setup -Destination (Join-Path $staging 'SpaceLens-Setup.exe')
    Copy-Item -LiteralPath "$setup.sha256" -Destination (Join-Path $staging 'SpaceLens-Setup.exe.sha256')
    Copy-Item -LiteralPath (Join-Path $inputDirectory 'RELEASE-NOTES.md') -Destination (Join-Path $staging 'RELEASE-NOTES.md')
    $signedManifest = Join-Path $staging 'update.json'

    $lastProvenance = Get-CleanReleaseProvenance -Repository $repository
    if ($lastProvenance.Commit -ne $initialProvenance.Commit) { throw 'The source commit changed during offline release validation.' }

    # SECURITY BOUNDARY: this is the only operation that opens the private key.
    # ReleaseSigner signs and verifies in one process. No application, Setup,
    # build tool, Git command, or other produced executable is run afterward.
    Invoke-LockedReleaseSigner -Lock $signerLock -Arguments @($signerMode, $resolvedKey, $publicKey, $manifestPath, $signedManifest)
    if (-not (Test-Path -LiteralPath $signedManifest -PathType Leaf)) { throw 'ReleaseSigner did not create a verified signed manifest.' }
    Assert-SignedUpdateManifest -PublicKey $publicKey -Manifest $signedManifest -ExpectedVersion $version -ExpectedSetupSha256 $setupRecord.sha256 -ExpectedSetupSize $setupRecord.sizeBytes

    $finalManifest = Get-Content -Raw -LiteralPath $signedManifest | ConvertFrom-Json
    $signatureBytes = [Convert]::FromBase64String([string]$finalManifest.signature)
    if ($signatureBytes.Length -ne 64 -or $finalManifest.version -ne $version -or $finalManifest.sha256 -cne $setupRecord.sha256 -or [long]$finalManifest.sizeBytes -ne $setupRecord.sizeBytes) {
        throw 'The signed manifest changed release identity or has an invalid signature encoding.'
    }
    $finalAppRecord = Assert-HashFile -Path (Join-Path $staging 'SpaceLens.exe')
    $finalSetupRecord = Assert-HashFile -Path (Join-Path $staging 'SpaceLens-Setup.exe')
    if ($finalAppRecord.sha256 -ne $appRecord.sha256 -or $finalAppRecord.sizeBytes -ne $appRecord.sizeBytes -or
        $finalSetupRecord.sha256 -ne $setupRecord.sha256 -or $finalSetupRecord.sizeBytes -ne $setupRecord.sizeBytes) {
        throw 'A release executable or its sidecar was replaced after private-key access.'
    }

    $releaseProvenance = [ordered]@{
        schemaVersion = 1
        product = 'SpaceLens'
        version = $version
        sourceRepository = [string]$metadata.sourceRepository
        sourceCommit = [string]$metadata.sourceCommit
        preparedArchiveSha256 = $actualDigest
        releaseArtifacts = @(
            Get-ReleaseFileRecord -Path (Join-Path $staging 'RELEASE-NOTES.md')
            $finalAppRecord
            Get-ReleaseFileRecord -Path (Join-Path $staging 'SpaceLens.exe.sha256')
            $finalSetupRecord
            Get-ReleaseFileRecord -Path (Join-Path $staging 'SpaceLens-Setup.exe.sha256')
            Get-ReleaseFileRecord -Path $signedManifest
        )
    }
    [IO.File]::WriteAllText(
        (Join-Path $staging 'release-provenance.json'),
        (($releaseProvenance | ConvertTo-Json -Depth 5) + [Environment]::NewLine),
        [Text.UTF8Encoding]::new($false)
    )
    Assert-DirectoryContainsOnly -Directory $staging -Names @('RELEASE-NOTES.md', 'SpaceLens.exe', 'SpaceLens.exe.sha256', 'SpaceLens-Setup.exe', 'SpaceLens-Setup.exe.sha256', 'update.json', 'release-provenance.json')

    if (Test-Path -LiteralPath $releaseDirectory) {
        $resolvedRelease = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $releaseDirectory).Path)
        [void](Get-OwnedChildPath -Path $resolvedRelease -Parent $releaseRoot)
        Assert-ReleasePathHasNoReparsePoints -Path $resolvedRelease -StopAt $repository
        Assert-ReleaseTreeHasNoReparsePoints -Path $resolvedRelease
        $previous = Get-OwnedChildPath -Path (Join-Path $releaseRoot (".$version-" + [Guid]::NewGuid().ToString('N') + '.previous')) -Parent $releaseRoot
        Move-Item -LiteralPath $resolvedRelease -Destination $previous
        Assert-ReleasePathHasNoReparsePoints -Path $previous -StopAt $repository
    }
    Assert-ReleasePathHasNoReparsePoints -Path $staging -StopAt $repository
    try { Move-Item -LiteralPath $staging -Destination $releaseDirectory; Assert-ReleasePathHasNoReparsePoints -Path $releaseDirectory -StopAt $repository }
    catch {
        if ($previous -and (Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $releaseDirectory)) { Move-Item -LiteralPath $previous -Destination $releaseDirectory }
        throw
    }
    if ($previous -and (Test-Path -LiteralPath $previous)) { Assert-ReleasePathHasNoReparsePoints -Path $previous -StopAt $repository; Assert-ReleaseTreeHasNoReparsePoints -Path $previous; Remove-Item -LiteralPath $previous -Recurse -Force }
}
finally {
    if ($null -ne $signerLock) { $signerLock.Stream.Dispose() }
    if ($null -ne $zipLock) { $zipLock.Stream.Dispose() }
    foreach ($directory in @($inputDirectory, $staging)) {
        if (Test-Path -LiteralPath $directory) { Assert-ReleasePathHasNoReparsePoints -Path $directory -StopAt $repository; Assert-ReleaseTreeHasNoReparsePoints -Path $directory; Remove-Item -LiteralPath $directory -Recurse -Force }
    }
}

Write-Host "Offline-signed SpaceLens v$version from $($initialProvenance.Commit) is ready in $releaseDirectory" -ForegroundColor Green
