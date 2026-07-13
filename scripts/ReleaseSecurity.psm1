Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NormalizedReleasePath {
    param([Parameter(Mandatory)][string]$Path)

    return [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ))
}

function Test-ReleasePathWithin {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root
    )

    $fullPath = Get-NormalizedReleasePath -Path $Path
    $fullRoot = Get-NormalizedReleasePath -Path $Root
    return [string]::Equals($fullPath, $fullRoot, [StringComparison]::Ordinal) -or
        $fullPath.StartsWith($fullRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)
}

function Get-ReleaseWorkspaceRoots {
    param([Parameter(Mandatory)][string]$Repository)

    $repositoryRoot = Get-NormalizedReleasePath -Path $Repository
    $roots = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [void]$roots.Add($repositoryRoot)

    # Agent workspaces can contain a nested source repository.
    # Treat every marked ancestor as a workspace boundary so a sibling path is
    # never mistaken for safe offline key storage.
    $ancestor = Get-Item -LiteralPath $repositoryRoot -Force
    while ($null -ne $ancestor) {
        if (Test-Path -LiteralPath (Join-Path $ancestor.FullName '.agents')) {
            [void]$roots.Add((Get-NormalizedReleasePath -Path $ancestor.FullName))
        }
        $ancestor = $ancestor.Parent
    }

    $githubWorkspace = [Environment]::GetEnvironmentVariable('GITHUB_WORKSPACE')
    if (-not [string]::IsNullOrWhiteSpace($githubWorkspace)) {
        [void]$roots.Add((Get-NormalizedReleasePath -Path $githubWorkspace))
    }

    if (Get-Command git -ErrorAction SilentlyContinue) {
        $gitSafety = "safe.directory=$($repositoryRoot.Replace('\', '/'))"
        $gitRoot = (& git -c $gitSafety -C $repositoryRoot rev-parse --show-toplevel 2>$null)
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($gitRoot)) {
            [void]$roots.Add((Get-NormalizedReleasePath -Path $gitRoot.Trim()))
        }
    }

    return @($roots)
}

function Assert-PrivateKeyOutsideWorkspace {
    param(
        [Parameter(Mandatory)][string]$SigningKey,
        [Parameter(Mandatory)][string]$Repository
    )

    if (-not (Test-Path -LiteralPath $SigningKey -PathType Leaf)) {
        throw "Signing key is not an existing file: $SigningKey"
    }

    $resolvedKey = Get-NormalizedReleasePath -Path (Resolve-Path -LiteralPath $SigningKey).Path
    foreach ($root in Get-ReleaseWorkspaceRoots -Repository $Repository) {
        if (Test-ReleasePathWithin -Path $resolvedKey -Root $root) {
            throw "Refusing to access a private signing key inside the repository or workspace: $resolvedKey"
        }
    }

    # Reparse-point aliases make a lexical outside-workspace check ambiguous. Reject
    # them rather than risk following a junction or symlink back into the workspace.
    $keyItem = Get-Item -LiteralPath $resolvedKey -Force
    if (($keyItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to access a private signing key through a reparse point: $($keyItem.FullName)"
    }
    $currentDirectory = $keyItem.Directory
    while ($null -ne $currentDirectory) {
        if (($currentDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to access a private signing key through a reparse point: $($currentDirectory.FullName)"
        }
        $currentDirectory = $currentDirectory.Parent
    }

    return $resolvedKey
}

function Assert-NewKeyPathOutsideWorkspace {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Repository
    )

    $fullPath = Get-NormalizedReleasePath -Path $Path
    if (Test-Path -LiteralPath $fullPath) {
        throw "Refusing to overwrite an existing key file: $fullPath"
    }
    foreach ($root in Get-ReleaseWorkspaceRoots -Repository $Repository) {
        if (Test-ReleasePathWithin -Path $fullPath -Root $root) {
            throw "Refusing to create key material inside the repository or workspace: $fullPath"
        }
    }

    $parentPath = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $parentPath -PathType Container)) {
        throw "Key destination directory does not exist: $parentPath"
    }
    $currentDirectory = Get-Item -LiteralPath $parentPath -Force
    while ($null -ne $currentDirectory) {
        if (($currentDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to create key material through a reparse point: $($currentDirectory.FullName)"
        }
        $currentDirectory = $currentDirectory.Parent
    }
    return $fullPath
}

function Get-CleanReleaseProvenance {
    param([Parameter(Mandatory)][string]$Repository)

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw 'Git is required to verify release provenance.'
    }

    $repositoryRoot = Get-NormalizedReleasePath -Path $Repository
    $gitSafety = "safe.directory=$($repositoryRoot.Replace('\', '/'))"
    $worktree = (& git -c $gitSafety -C $repositoryRoot rev-parse --show-toplevel).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($worktree)) {
        throw 'Git could not resolve the release worktree.'
    }
    $worktree = Get-NormalizedReleasePath -Path $worktree
    if (-not (Test-ReleasePathWithin -Path $repositoryRoot -Root $worktree)) {
        throw 'The release repository is not inside the resolved Git worktree.'
    }

    $changes = @(& git -c $gitSafety -C $repositoryRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw 'Git could not inspect the release source tree.'
    }
    if ($changes.Count -ne 0) {
        throw "Release finalization requires a clean committed worktree.`n$($changes -join [Environment]::NewLine)"
    }

    $commit = (& git -c $gitSafety -C $repositoryRoot rev-parse HEAD).Trim().ToLowerInvariant()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
        throw 'Git could not resolve the release source commit.'
    }

    return [pscustomobject]@{
        Worktree = $worktree
        Commit = $commit
    }
}

function Get-ReleaseFileRecord {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Release input is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release inputs may not be reparse points: $($item.FullName)"
    }

    return [pscustomobject]@{
        name = $item.Name
        sizeBytes = [long]$item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}

function Assert-PublicSpkiPemFile {
    param([Parameter(Mandatory)][string]$Path)

    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or $item.Length -le 0 -or $item.Length -gt 4096 -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Public key must be a small regular file: $Path"
    }
    $text = [IO.File]::ReadAllText($item.FullName, [Text.UTF8Encoding]::new($false, $true)).Trim()
    if ($text.IndexOf('PRIVATE KEY', [StringComparison]::Ordinal) -ge 0 -or
        $text -notmatch '\A-----BEGIN PUBLIC KEY-----\r?\n(?<body>[A-Za-z0-9+/=\r\n]+)\r?\n-----END PUBLIC KEY-----\z') {
        throw 'Tracked update key must contain exactly one public-only SPKI PUBLIC KEY PEM block.'
    }
    try { $der = [Convert]::FromBase64String(($Matches.body -replace '\s', '')) }
    catch { throw 'Tracked update public key contains invalid base64.' }
    if ($der.Length -lt 80 -or $der.Length -gt 512) { throw 'Tracked update public key has an invalid SPKI size.' }
    return $item.FullName
}

function Assert-ReleaseTreeHasNoReparsePoints {
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
            foreach ($child in Get-ChildItem -LiteralPath $current -Force) { $pending.Push($child.FullName) }
        }
    }
}

function Assert-ReleasePathHasNoReparsePoints {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$StopAt
    )

    $fullPath = Get-NormalizedReleasePath -Path $Path
    $fullStop = Get-NormalizedReleasePath -Path $StopAt
    if (-not (Test-ReleasePathWithin -Path $fullPath -Root $fullStop)) {
        throw "Path is outside its expected owner: $fullPath"
    }
    $current = Get-Item -LiteralPath $fullPath -Force
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to manage a path through a reparse point: $($current.FullName)"
        }
        if ([string]::Equals((Get-NormalizedReleasePath -Path $current.FullName), $fullStop, [StringComparison]::Ordinal)) { return }
        $current = if ($current -is [IO.DirectoryInfo]) { $current.Parent } else { $current.Directory }
    }
    throw "Path ancestry did not reach its expected owner: $fullPath"
}

function Get-LockedReleaseExecutableHash {
    param([Parameter(Mandatory)]$Lock)

    if ($null -eq $Lock.Stream -or -not $Lock.Stream.CanRead) { throw 'Release executable lock is not readable.' }
    $Lock.Stream.Position = 0
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash($Lock.Stream) } finally { $sha.Dispose(); $Lock.Stream.Position = 0 }
    return ([BitConverter]::ToString($bytes)).Replace('-', '')
}

function Open-VerifiedReleaseFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$ExpectedSha256 = ''
    )

    $fullPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
    $item = Get-Item -LiteralPath $fullPath -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release input must be a regular file: $fullPath"
    }
    $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $lock = [pscustomobject]@{ FullPath = $fullPath; Stream = $stream; Sha256 = '' }
        $lock.Sha256 = Get-LockedReleaseExecutableHash -Lock $lock
        if ($ExpectedSha256 -and -not [string]::Equals($lock.Sha256, $ExpectedSha256.ToUpperInvariant(), [StringComparison]::Ordinal)) {
            throw 'Locked release input does not match the expected SHA-256.'
        }
        return $lock
    }
    catch { $stream.Dispose(); throw }
}

function Open-VerifiedReleaseExecutable {
    param(
        [Parameter(Mandatory)][string]$Path,
        [string]$ExpectedSha256 = ''
    )
    return Open-VerifiedReleaseFile -Path $Path -ExpectedSha256 $ExpectedSha256
}

function Assert-NativeReleaseSignerDirectory {
    param([Parameter(Mandatory)][string]$ExecutablePath)

    $fullPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ExecutablePath).Path)
    if (-not [string]::Equals([IO.Path]::GetFileName($fullPath), 'ReleaseSigner.exe', [StringComparison]::Ordinal)) {
        throw 'Sensitive signing requires the native ReleaseSigner.exe.'
    }
    $directory = [IO.Path]::GetDirectoryName($fullPath)
    $entries = @(Get-ChildItem -LiteralPath $directory -Force)
    if ($entries.Count -ne 1 -or $entries[0].PSIsContainer -or
        -not [string]::Equals($entries[0].FullName, $fullPath, [StringComparison]::Ordinal) -or
        ($entries[0].Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Sensitive signer directory must contain exactly one regular file: ReleaseSigner.exe.'
    }
    $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $first = $stream.ReadByte(); $second = $stream.ReadByte(); $length = $stream.Length } finally { $stream.Dispose() }
    if ($length -lt 4096 -or $first -ne 0x4D -or $second -ne 0x5A) {
        throw 'ReleaseSigner is not a plausible native Windows PE executable.'
    }
}

function Invoke-LockedReleaseSigner {
    param(
        [Parameter(Mandatory)]$Lock,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    if (-not [string]::Equals((Get-LockedReleaseExecutableHash -Lock $Lock), [string]$Lock.Sha256, [StringComparison]::Ordinal)) {
        throw 'Locked ReleaseSigner changed before launch.'
    }
    foreach ($argument in $Arguments) {
        if ($argument.Contains('"')) { throw 'ReleaseSigner arguments may not contain quote characters.' }
    }
    $dangerousEnvironment = @(
        'COR_ENABLE_PROFILING', 'COR_PROFILER', 'COR_PROFILER_PATH',
        'CORECLR_ENABLE_PROFILING', 'CORECLR_PROFILER', 'CORECLR_PROFILER_PATH',
        'DOTNET_STARTUP_HOOKS', 'DOTNET_ADDITIONAL_DEPS', 'DOTNET_SHARED_STORE',
        'DOTNET_HOST_PATH', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROOT_X86',
        'DOTNET_ROLL_FORWARD', 'DOTNET_ROLL_FORWARD_TO_PRERELEASE'
    )
    $saved = @{}
    foreach ($name in $dangerousEnvironment) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable($name, $null, [EnvironmentVariableTarget]::Process)
    }
    foreach ($name in @('DOTNET_EnableDiagnostics', 'COMPlus_EnableDiagnostics')) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable($name, '0', [EnvironmentVariableTarget]::Process)
    }
    try {
        # The call operator launches the exact locked path without a shell and
        # preserves argument boundaries. It also avoids a process-cmdlet
        # framework bug when an inherited environment contains case-duplicate
        # names such as Path/PATH.
        & $Lock.FullPath @Arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        foreach ($entry in $saved.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, [EnvironmentVariableTarget]::Process)
        }
    }
    if ($exitCode -ne 0) { throw "ReleaseSigner failed with exit code $exitCode." }
    if (-not [string]::Equals((Get-LockedReleaseExecutableHash -Lock $Lock), [string]$Lock.Sha256, [StringComparison]::Ordinal)) {
        throw 'Locked ReleaseSigner changed during launch.'
    }
}

function Copy-ReleaseZipEntryExact {
    param(
        [Parameter(Mandatory)]$Entry,
        [Parameter(Mandatory)][string]$Destination,
        [ValidateRange(1, 1073741824)][long]$MaximumEntryBytes,
        [Parameter(Mandatory)][ref]$RemainingTotalBytes
    )

    $declaredLength = [long]$Entry.Length
    if ($declaredLength -lt 0 -or $declaredLength -gt $MaximumEntryBytes -or $declaredLength -gt [long]$RemainingTotalBytes.Value) {
        throw "ZIP entry has an invalid declared length: $($Entry.FullName)"
    }
    $source = $Entry.Open()
    $output = $null
    $written = 0L
    $buffer = [byte[]]::new(64KB)
    try {
        $output = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        while ($written -lt $declaredLength) {
            $request = [int][Math]::Min($buffer.Length, $declaredLength - $written)
            $read = $source.Read($buffer, 0, $request)
            if ($read -le 0) { throw "ZIP entry ended before its declared length: $($Entry.FullName)" }
            $output.Write($buffer, 0, $read)
            $written += $read
        }
        if ($source.ReadByte() -ne -1) { throw "ZIP entry exceeded its declared length: $($Entry.FullName)" }
        $output.Flush($true)
    }
    catch {
        Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
        throw
    }
    finally {
        if ($null -ne $output) { $output.Dispose() }
        if ($null -ne $source) { $source.Dispose() }
    }
    if ((Get-Item -LiteralPath $Destination).Length -ne $declaredLength) { throw "Extracted ZIP entry length mismatch: $($Entry.FullName)" }
    $RemainingTotalBytes.Value = [long]$RemainingTotalBytes.Value - $written
}

function Assert-SignedUpdateManifest {
    param(
        [Parameter(Mandatory)][string]$PublicKey,
        [Parameter(Mandatory)][string]$Manifest,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][ValidatePattern('^[0-9A-F]{64}$')][string]$ExpectedSetupSha256,
        [Parameter(Mandatory)][long]$ExpectedSetupSize
    )

    Assert-PublicSpkiPemFile -Path $PublicKey | Out-Null
    $manifestItem = Get-Item -LiteralPath $Manifest -Force
    if ($manifestItem.Length -le 0 -or $manifestItem.Length -gt 32KB -or ($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Signed update manifest is empty, too large, or a reparse point.'
    }
    $json = [IO.File]::ReadAllText($manifestItem.FullName, [Text.UTF8Encoding]::new($false, $true))
    $document = [Text.Json.JsonDocument]::Parse($json)
    try {
        $root = $document.RootElement
        if ($root.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Signed update manifest is not an object.' }
        $expectedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($name in @('schemaVersion','version','tag','assetName','sha256','sizeBytes','publishedUtc','notes','signature')) { [void]$expectedNames.Add($name) }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $root.EnumerateObject()) {
            if (-not $seen.Add($property.Name) -or -not $expectedNames.Contains($property.Name)) { throw 'Signed update manifest contains duplicate or unexpected fields.' }
        }
        if ($seen.Count -ne $expectedNames.Count) { throw 'Signed update manifest is missing a required field.' }

        $schemaVersion = $root.GetProperty('schemaVersion').GetInt32()
        $version = $root.GetProperty('version').GetString()
        $tag = $root.GetProperty('tag').GetString()
        $assetName = $root.GetProperty('assetName').GetString()
        $sha256 = $root.GetProperty('sha256').GetString()
        $sizeBytes = $root.GetProperty('sizeBytes').GetInt64()
        $publishedText = $root.GetProperty('publishedUtc').GetString()
        $notes = $root.GetProperty('notes').GetString()
        $signatureText = $root.GetProperty('signature').GetString()
        if ($schemaVersion -ne 1 -or $version -cne $ExpectedVersion -or $tag -cne "v$ExpectedVersion" -or
            $assetName -cne 'SpaceLens-Setup.exe' -or $sha256 -cne $ExpectedSetupSha256 -or
            $sizeBytes -ne $ExpectedSetupSize -or $null -eq $notes -or $notes.Length -gt 4000) {
            throw 'Signed update manifest does not match the exact release identity.'
        }
        $published = [DateTimeOffset]::Parse($publishedText, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
        $signature = [Convert]::FromBase64String($signatureText)
        if ($signature.Length -ne 64) { throw 'Signed update manifest has an invalid P-256 signature length.' }
        $canonical = [string]::Join("`n", @(
            'spacelens-update-v1',
            '1',
            $version,
            $tag,
            $assetName,
            $sha256.ToUpperInvariant(),
            $sizeBytes.ToString([Globalization.CultureInfo]::InvariantCulture),
            $published.ToUniversalTime().ToString('O', [Globalization.CultureInfo]::InvariantCulture),
            [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($notes))
        ))
        $key = [Security.Cryptography.ECDsa]::Create()
        try {
            $key.ImportFromPem([IO.File]::ReadAllText($PublicKey))
            $valid = $key.VerifyData(
                [Text.Encoding]::UTF8.GetBytes($canonical),
                $signature,
                [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation
            )
        }
        finally { $key.Dispose(); [Security.Cryptography.CryptographicOperations]::ZeroMemory($signature) }
        if (-not $valid) { throw 'Signed update manifest failed independent public-key verification.' }
    }
    finally { $document.Dispose() }
}

function Assert-ReleaseFileRecord {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]$Record
    )

    $actual = Get-ReleaseFileRecord -Path $Path
    if ($Record.name -isnot [string] -or
        -not [string]::Equals([string]$Record.name, $actual.name, [StringComparison]::Ordinal) -or
        [long]$Record.sizeBytes -ne $actual.sizeBytes -or
        [string]$Record.sha256 -notmatch '^[0-9A-F]{64}$' -or
        -not [string]::Equals([string]$Record.sha256, $actual.sha256, [StringComparison]::Ordinal)) {
        throw "Release provenance does not match $($actual.name)."
    }
    return $actual
}

Export-ModuleMember -Function @(
    'Assert-NewKeyPathOutsideWorkspace',
    'Assert-NativeReleaseSignerDirectory',
    'Assert-PrivateKeyOutsideWorkspace',
    'Assert-PublicSpkiPemFile',
    'Assert-ReleaseFileRecord',
    'Assert-ReleasePathHasNoReparsePoints',
    'Assert-ReleaseTreeHasNoReparsePoints',
    'Assert-SignedUpdateManifest',
    'Copy-ReleaseZipEntryExact',
    'Get-CleanReleaseProvenance',
    'Get-NormalizedReleasePath',
    'Get-ReleaseFileRecord',
    'Get-ReleaseWorkspaceRoots',
    'Invoke-LockedReleaseSigner',
    'Open-VerifiedReleaseFile',
    'Open-VerifiedReleaseExecutable',
    'Test-ReleasePathWithin'
)
