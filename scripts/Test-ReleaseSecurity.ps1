[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $PSScriptRoot 'ReleaseSecurity.psm1') -Force

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$ExpectedPattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $ExpectedPattern) {
            throw "Expected error /$ExpectedPattern/, got: $($_.Exception.Message)"
        }
        return
    }
    throw "Expected action to fail with /$ExpectedPattern/."
}

function Remove-DisposableTestDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$OwnedRoot,
        [Parameter(Mandatory)][string]$ExpectedLeafPattern,
        [switch]$AllowSandboxAccessDenied
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($OwnedRoot).TrimEnd([char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ))
    $leaf = Split-Path -Leaf $fullPath
    if ([string]::Equals($fullPath.TrimEnd('\', '/'), $fullRoot, [StringComparison]::Ordinal) -or
        -not (Test-ReleasePathWithin -Path $fullPath -Root $fullRoot) -or
        $leaf -notmatch $ExpectedLeafPattern) {
        throw "Refusing to remove a path outside the verified disposable test boundary: $fullPath"
    }

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        if (-not (Test-Path -LiteralPath $fullPath)) {
            return
        }
        try {
            Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
        }
        catch {
            # Antivirus and filesystem filters can report a transient Access Denied
            # after the tree was already removed. Verify absence before retrying.
            if (-not (Test-Path -LiteralPath $fullPath)) {
                return
            }
            if ($attempt -eq 5) {
                if ($AllowSandboxAccessDenied -and $_.Exception.Message -match '(?i)access is denied') {
                    Write-Warning "The sandbox could not remove verified disposable test data; CI runners normally can: $fullPath"
                    return
                }
                throw "Failed to clean verified disposable test directory after $attempt attempts: $fullPath. $($_.Exception.Message)"
            }
            Start-Sleep -Milliseconds (50 * $attempt)
        }
    }
}

$insideDirectory = Join-Path $repository ('artifacts\release-security-test-' + [Guid]::NewGuid().ToString('N'))
$outsideDirectory = Join-Path ([IO.Path]::GetTempPath()) ('spacelens-release-security-' + [Guid]::NewGuid().ToString('N'))
$syntheticWorkspace = Join-Path ([IO.Path]::GetTempPath()) ('spacelens-agent-root-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $insideDirectory, $outsideDirectory -Force | Out-Null
    $insideKey = Join-Path $insideDirectory 'dummy-private.pem'
    $outsideKey = Join-Path $outsideDirectory 'dummy-private.pem'
    [IO.File]::WriteAllText($insideKey, 'test-only-no-private-material', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($outsideKey, 'test-only-no-private-material', [Text.UTF8Encoding]::new($false))

    Assert-Throws -Action { Assert-PrivateKeyOutsideWorkspace -SigningKey $insideKey -Repository $repository } -ExpectedPattern 'inside the repository or workspace'
    Assert-Throws -Action { Assert-NewKeyPathOutsideWorkspace -Path (Join-Path $insideDirectory 'new-private.pem') -Repository $repository } -ExpectedPattern 'inside the repository or workspace'
    $resolvedOutside = Assert-PrivateKeyOutsideWorkspace -SigningKey $outsideKey -Repository $repository
    if (-not [string]::Equals($resolvedOutside, [IO.Path]::GetFullPath($outsideKey), [StringComparison]::Ordinal)) {
        throw 'Outside-workspace key path was not normalized exactly.'
    }

    $syntheticRepository = Join-Path $syntheticWorkspace 'nested\repo'
    $syntheticSibling = Join-Path $syntheticWorkspace 'sibling'
    New-Item -ItemType Directory -Path (Join-Path $syntheticWorkspace '.agents'), $syntheticRepository, $syntheticSibling -Force | Out-Null
    $syntheticExistingKey = Join-Path $syntheticSibling 'private.pem'
    [IO.File]::WriteAllText($syntheticExistingKey, 'test-only-no-private-material', [Text.UTF8Encoding]::new($false))
    Assert-Throws -Action { Assert-PrivateKeyOutsideWorkspace -SigningKey $syntheticExistingKey -Repository $syntheticRepository } -ExpectedPattern 'inside the repository or workspace'
    Assert-Throws -Action { Assert-NewKeyPathOutsideWorkspace -Path (Join-Path $syntheticSibling 'new-private.pem') -Repository $syntheticRepository } -ExpectedPattern 'inside the repository or workspace'

    $inputFile = Join-Path $outsideDirectory 'input.bin'
    [IO.File]::WriteAllBytes($inputFile, [byte[]](1, 2, 3, 4))
    $record = Get-ReleaseFileRecord -Path $inputFile
    [void](Assert-ReleaseFileRecord -Path $inputFile -Record $record)
    [IO.File]::WriteAllBytes($inputFile, [byte[]](1, 2, 3, 5))
    Assert-Throws -Action { Assert-ReleaseFileRecord -Path $inputFile -Record $record } -ExpectedPattern 'does not match'

    $lockFile = Join-Path $outsideDirectory 'locked-signer.exe'
    $lockBytes = [byte[]]::new(4096); $lockBytes[0] = 0x4D; $lockBytes[1] = 0x5A
    [IO.File]::WriteAllBytes($lockFile, $lockBytes)
    $locked = Open-VerifiedReleaseFile -Path $lockFile
    try {
        $writeBlocked = $false
        try { $writer = [IO.File]::Open($lockFile, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None); $writer.Dispose() }
        catch [IO.IOException] { $writeBlocked = $true }
        $deleteBlocked = $false
        try { [IO.File]::Delete($lockFile) }
        catch [IO.IOException] { $deleteBlocked = $true }
        catch [UnauthorizedAccessException] { $deleteBlocked = $true }
        if (-not $writeBlocked -or -not $deleteBlocked -or -not (Test-Path -LiteralPath $lockFile)) {
            throw 'Verified signer lock did not deny write/delete replacement.'
        }
    }
    finally { $locked.Stream.Dispose() }

    $nativeProbeDirectory = Join-Path $outsideDirectory 'native-probe'
    New-Item -ItemType Directory -Path $nativeProbeDirectory | Out-Null
    $companion = Join-Path $nativeProbeDirectory 'ReleaseSigner.dll'
    $nativeProbe = Join-Path $nativeProbeDirectory 'ReleaseSigner.exe'
    [IO.File]::WriteAllBytes($nativeProbe, $lockBytes)
    [IO.File]::WriteAllBytes($companion, [byte[]](1))
    Assert-Throws -Action { Assert-NativeReleaseSignerDirectory -ExecutablePath $nativeProbe } -ExpectedPattern 'must contain exactly one regular file'
    Remove-Item -LiteralPath $companion -Force
    Assert-NativeReleaseSignerDirectory -ExecutablePath $nativeProbe

    [void](Assert-PublicSpkiPemFile -Path (Join-Path $repository 'src\SpaceLens\assets\update-public-key.pem'))
    $fakePrivate = Join-Path $outsideDirectory 'fake-private.pem'
    [IO.File]::WriteAllText($fakePrivate, "-----BEGIN PRIVATE KEY-----`nAAAA`n-----END PRIVATE KEY-----`n", [Text.Encoding]::ASCII)
    Assert-Throws -Action { Assert-PublicSpkiPemFile -Path $fakePrivate } -ExpectedPattern 'public-only SPKI'

    if (-not (Test-ReleasePathWithin -Path (Join-Path $repository 'child') -Root $repository)) {
        throw 'Child-path containment test failed.'
    }
    if (Test-ReleasePathWithin -Path ($repository + '-sibling') -Root $repository) {
        throw 'Prefix-sibling path was incorrectly treated as contained.'
    }
    if (Test-ReleasePathWithin -Path 'C:\CaseRoot\child' -Root 'C:\caseroot') {
        throw 'Case-only sibling path was incorrectly treated as contained.'
    }

    $buildScript = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Build-Release.ps1')
    if ($buildScript -match '(?i)SigningKey') {
        throw 'Build-Release.ps1 must never accept or access a signing key.'
    }
    foreach ($reparseControl in @('Assert-ReleasePathHasNoReparsePoints', 'Assert-ReleaseTreeHasNoReparsePoints')) {
        if ($buildScript -notmatch [regex]::Escape($reparseControl)) { throw "Build-Release.ps1 is missing recursive-delete control $reparseControl." }
    }

    foreach ($finalizerName in @('Finalize-Release.ps1', 'Finalize-SignPathRelease.ps1')) {
        $finalizer = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot $finalizerName)
        if ($finalizer -match '(?i)SigningKey' -or $finalizer -notmatch 'sign-release-cng') {
            throw "$finalizerName must be CNG-only."
        }
        foreach ($requiredControl in @('ExpectedSignerSha256', 'Assert-NativeReleaseSignerDirectory', 'Open-VerifiedReleaseFile', 'Open-VerifiedReleaseExecutable', 'Invoke-LockedReleaseSigner', 'Copy-ReleaseZipEntryExact', 'Assert-ReleasePathHasNoReparsePoints', 'Assert-ReleaseTreeHasNoReparsePoints', 'Assert-SignedUpdateManifest')) {
            if ($finalizer -notmatch [regex]::Escape($requiredControl)) { throw "$finalizerName is missing locked native-signer control $requiredControl." }
        }
        if ($finalizer -match '\[IO\.Compression\.ZipFile\]::OpenRead' -or $finalizer -match 'Get-FileHash\s+-LiteralPath\s+\$resolvedZip') {
            throw "$finalizerName must hash and extract the ZIP through one continuously locked file handle."
        }
        if ($finalizer -match '(?im)^\s*(?:&\s*dotnet\b|Invoke-DotNet\b)') {
            throw "$finalizerName must never build a signer during sensitive finalization."
        }
        if ($finalizer -match '(?im)Invoke-PackagedSelfTest|Start-Process') {
            throw "$finalizerName must never execute prepared product bytes during offline finalization."
        }
        $signBoundary = $finalizer.IndexOf('Invoke-LockedReleaseSigner -Lock $signerLock -Arguments @($signerMode', [StringComparison]::Ordinal)
        if ($signBoundary -lt 0) {
            throw "$finalizerName does not use the locked one-process CNG sign-and-verify boundary."
        }
        $afterPrivateKeyAccess = $finalizer.Substring($signBoundary)
        if ($afterPrivateKeyAccess -match '(?im)^\s*(Invoke-PackagedSelfTest|Start-Process|&\s+dotnet|&\s+git)\b') {
            throw "$finalizerName executes a build, Git, or packaged binary after private-key access."
        }
    }

    $signWorkflow = Get-Content -Raw -LiteralPath (Join-Path $repository '.github\workflows\sign-release.yml')
    if ($signWorkflow -notmatch '(?m)^\s*if:\s*\$\{\{\s*false\s*\}\}\s*$') {
        throw 'The SignPath workflow safety lock is missing.'
    }
    foreach ($line in ($signWorkflow -split "`r?`n")) {
        if ($line -match '^\s*uses:\s*(?<action>\S+)') {
            $action = $Matches.action
            if ($action -notmatch '@[0-9a-fA-F]{40}$') {
                throw "Third-party action is not pinned to a full commit SHA: $action"
            }
        }
    }

    foreach ($workflow in Get-ChildItem -LiteralPath (Join-Path $repository '.github\workflows') -Filter '*.yml' -File) {
        $workflowText = Get-Content -Raw -LiteralPath $workflow.FullName
        if ($workflowText -match '(?m)^\s*pull_request_target\s*:') {
            throw "$($workflow.Name) uses the privileged pull_request_target event."
        }
        $knownTokenPermissions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($knownPermission in @(
            'actions', 'artifact-metadata', 'attestations', 'checks', 'code-quality', 'contents',
            'deployments', 'discussions', 'id-token', 'issues', 'models', 'packages', 'pages',
            'pull-requests', 'repository-projects', 'security-events', 'statuses', 'vulnerability-alerts'
        )) {
            [void]$knownTokenPermissions.Add($knownPermission)
        }
        $writePermissionPattern = '(?im)^\s*(?:"(?<permission>[a-z-]+)"|''(?<permission>[a-z-]+)''|(?<permission>[a-z-]+))\s*:\s*(?:"write"|''write''|write)\s*(?:#.*)?$'
        foreach ($writePermission in [regex]::Matches($workflowText, $writePermissionPattern)) {
            $permissionName = $writePermission.Groups['permission'].Value
            if (-not $knownTokenPermissions.Contains($permissionName)) {
                continue
            }
            $isNarrowCodeQlUpload = $workflow.Name -eq 'codeql.yml' -and $permissionName -eq 'security-events'
            if (-not $isNarrowCodeQlUpload) {
                throw "$($workflow.Name) grants a write permission outside the approved CodeQL SARIF upload boundary: $permissionName"
            }
        }
        $nonEmptyInlinePermissions = [regex]::Matches($workflowText, '(?im)^\s*permissions\s*:\s*(?<mapping>\{[^}\r\n]*\})\s*(?:#.*)?$') |
            Where-Object { $_.Groups['mapping'].Value -notmatch '^\{\s*\}$' }
        if ($workflowText -match '(?im)^\s*permissions\s*:\s*(?:"write-all"|''write-all''|write-all)\s*(?:#.*)?$' -or
            @($nonEmptyInlinePermissions).Count -ne 0) {
            throw "$($workflow.Name) grants a broad or inline write permission."
        }
        if ($workflowText -match '\$\{\{\s*secrets\.' -and $workflowText -match '(?m)^\s*pull_request\s*:') {
            throw "$($workflow.Name) combines pull-request code execution with repository secrets."
        }
        foreach ($line in ($workflowText -split "`r?`n")) {
            if ($line -match '^\s*uses:\s*(?<action>\S+)' -and $Matches.action -notmatch '@[0-9a-fA-F]{40}$') {
                throw "$($workflow.Name) contains an unpinned action: $($Matches.action)"
            }
        }
    }

    $ciWorkflow = Get-Content -Raw -LiteralPath (Join-Path $repository '.github\workflows\ci.yml')
    $uploadAction = 'actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a'
    if ([regex]::Matches($ciWorkflow, [regex]::Escape($uploadAction)).Count -ne 2) {
        throw 'CI must use exactly two independently named artifacts through the reviewed upload-artifact v7 commit.'
    }
    $canonicalPushGate = "if: `${{ github.event_name == 'push' && github.repository == 'Purxy8/SpaceLens' }}"
    if ([regex]::Matches($ciWorkflow, [regex]::Escape($canonicalPushGate)).Count -ne 2) {
        throw 'Every signer artifact upload must be gated to canonical-repository push events, never pull requests.'
    }
    foreach ($requiredUploadControl in @(
        'name: SpaceLens-ReleaseSigner-${{ github.sha }}',
        'path: artifacts/intermediate/native-release-signer/ReleaseSigner.exe',
        'name: SpaceLens-ReleaseSigner-SHA256-${{ github.sha }}',
        'path: artifacts/intermediate/ReleaseSigner.exe.sha256'
    )) {
        if ([regex]::Matches($ciWorkflow, [regex]::Escape($requiredUploadControl)).Count -ne 1) {
            throw "CI signer delivery is missing or duplicates exact control: $requiredUploadControl"
        }
    }
    foreach ($repeatedUploadControl in @('retention-days: 3', 'overwrite: false', 'if-no-files-found: error')) {
        if ([regex]::Matches($ciWorkflow, [regex]::Escape($repeatedUploadControl)).Count -ne 2) {
            throw "Both signer artifacts must enforce: $repeatedUploadControl"
        }
    }
    if ($ciWorkflow -match '\$\{\{\s*secrets\.' -or $ciWorkflow -match '(?im)^\s*[a-z-]+\s*:\s*write\s*$') {
        throw 'Read-only CI signer delivery must not access secrets or request write permissions.'
    }

    $codeQlWorkflow = Get-Content -Raw -LiteralPath (Join-Path $repository '.github\workflows\codeql.yml')
    $codeQlWritePermissions = [regex]::Matches($codeQlWorkflow, '(?im)^\s*security-events\s*:\s*write\s*(?:#.*)?$')
    if ($codeQlWritePermissions.Count -ne 2) {
        throw 'CodeQL must grant security-events: write exactly once per isolated analysis job.'
    }
    if ($codeQlWorkflow -notmatch '(?m)^permissions:\s*\{\s*\}\s*$') {
        throw 'CodeQL must default the workflow token to no permissions.'
    }
    if ($codeQlWorkflow -notmatch '(?m)^\s*pull_request\s*:\s*$' -or $codeQlWorkflow -match '(?m)^\s*pull_request_target\s*:') {
        throw 'CodeQL must use the fork-safe pull_request event, never pull_request_target.'
    }
    if ($codeQlWorkflow -match '(?im)^\s*run\s*:') {
        throw 'CodeQL must not execute repository-controlled shell commands in its write-scoped jobs.'
    }
    if ($codeQlWorkflow -match '\$\{\{\s*secrets\.') {
        throw 'CodeQL must not access repository secrets.'
    }
    if ($codeQlWorkflow -notmatch '(?m)^\s*build-mode:\s*none\s*$') {
        throw 'The C# CodeQL job must use no-build extraction to avoid executing pull-request code.'
    }
    if ($codeQlWorkflow -notmatch '(?m)^\s*languages:\s*csharp\s*$' -or $codeQlWorkflow -notmatch '(?m)^\s*languages:\s*actions\s*$') {
        throw 'CodeQL must analyze both C# and GitHub Actions.'
    }
    $codeQlCheckoutsWithoutCredentials = [regex]::Matches($codeQlWorkflow, '(?m)^\s*persist-credentials:\s*false\s*$')
    if ($codeQlCheckoutsWithoutCredentials.Count -ne 2) {
        throw 'Every CodeQL checkout must explicitly avoid persisting the GitHub token.'
    }

    $dependabot = Get-Content -Raw -LiteralPath (Join-Path $repository '.github\dependabot.yml')
    if ($dependabot -notmatch '(?m)^version:\s*2\s*$' -or
        $dependabot -notmatch '(?m)^\s*-\s*package-ecosystem:\s*github-actions\s*$' -or
        $dependabot -notmatch '(?m)^\s*directory:\s*/\s*$' -or
        $dependabot -notmatch '(?m)^\s*interval:\s*weekly\s*$') {
        throw 'Dependabot must monitor pinned GitHub Actions on a weekly schedule.'
    }
    if ($dependabot -match '(?im)^\s*(registries|insecure-external-code-execution)\s*:') {
        throw 'Dependabot must not be granted registry credentials or external code execution.'
    }

    $signerProject = Get-Content -Raw -LiteralPath (Join-Path $repository 'tools\ReleaseSigner\ReleaseSigner.csproj')
    foreach ($nativeProperty in @('<PublishAot>true</PublishAot>', '<SelfContained>true</SelfContained>', '<RuntimeIdentifier>win-x64</RuntimeIdentifier>')) {
        if ($signerProject -notmatch [regex]::Escape($nativeProperty)) { throw "ReleaseSigner lacks NativeAOT property $nativeProperty." }
    }
    $signerSource = Get-Content -Raw -LiteralPath (Join-Path $repository 'tools\ReleaseSigner\Program.cs')
    if ($signerSource -notmatch 'ForceHighProtection' -or $signerSource -notmatch 'Standalone/exportable key generation is disabled') {
        throw 'ReleaseSigner does not enforce high-protection CNG-only private signing.'
    }
    foreach ($rollbackControl in @('restore update-selftest.json', 'restore update-public-key.pem', 'delete incomplete CNG key', 'verify restored trust asset pair')) {
        if ($signerSource -notmatch [regex]::Escape($rollbackControl)) { throw "ReleaseSigner rotation recovery is missing independent step: $rollbackControl." }
    }
    foreach ($targetControl in @('AssertTrustTargetSet', 'AssertNoReparseAncestors', 'Directory.GetFileSystemEntries')) {
        if ($signerSource -notmatch [regex]::Escape($targetControl)) { throw "ReleaseSigner rotation target validation is missing $targetControl." }
    }
    $nativeSecurity = Get-Content -Raw -LiteralPath (Join-Path $repository 'tools\ReleaseSigner\NativeSecurity.cs')
    if ($nativeSecurity -notmatch 'DefaultDllImportSearchPaths\(DllImportSearchPath\.System32\)') {
        throw 'ReleaseSigner does not restrict assembly-owned native DLL resolution to System32.'
    }
    $rotationScript = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Rotate-UpdateTrust.ps1')
    $rotationBoundary = $rotationScript.IndexOf("@('rotate-cng'", [StringComparison]::Ordinal)
    if ($rotationBoundary -lt 0 -or $rotationScript.Substring($rotationBoundary) -match '(?im)^\s*(Start-Process|&\s+\S+)\b') {
        throw 'Trust-rotation handoff is missing or executes another process after CNG key creation.'
    }
    if ($rotationScript -notmatch "sandbox\|runner\|service" -or $rotationScript -notmatch "update-public-key\.pem" -or $rotationScript -notmatch "update-selftest\.json") {
        throw 'Trust-rotation handoff lacks the non-user identity guard or exact trust-asset targets.'
    }
    foreach ($targetControl in @('Assert-TrackedTrustAssets', 'Assert-ReleasePathHasNoReparsePoints', 'Compare-Object')) {
        if ($rotationScript -notmatch [regex]::Escape($targetControl)) { throw "Trust-rotation handoff lacks target ancestry/set validation $targetControl." }
    }
    $releaseSecurityModule = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'ReleaseSecurity.psm1')
    $lockedLaunchBoundary = $releaseSecurityModule.IndexOf('function Invoke-LockedReleaseSigner', [StringComparison]::Ordinal)
    if ($lockedLaunchBoundary -lt 0) { throw 'Release security module is missing the locked signer launch boundary.' }
    $lockedLaunchSource = $releaseSecurityModule.Substring($lockedLaunchBoundary)
    if ($lockedLaunchSource -notmatch '(?m)^\s*&\s+\$Lock\.FullPath\s+@Arguments\s*$' -or $lockedLaunchSource -match '(?im)Start-Process|cmd\.exe|powershell') {
        throw 'Locked ReleaseSigner must use the exact executable path and array arguments without a shell or Start-Process.'
    }

    $launcherProject = Get-Content -Raw -LiteralPath (Join-Path $repository 'tools\RestrictedProcessLauncher\RestrictedProcessLauncher.csproj')
    if ($launcherProject -notmatch '<SelfContained>false</SelfContained>' -or
        $launcherProject -notmatch 'System\.StartupHookProvider\.IsSupported" Value="false"') {
        throw 'The CI-only restricted launcher must remain framework-dependent and immune to inherited startup hooks.'
    }
    $launcherSource = Get-Content -Raw -LiteralPath (Join-Path $repository 'tools\RestrictedProcessLauncher\Program.cs')
    foreach ($launcherControl in @(
        'CreateRestrictedToken', 'DisableMaxPrivilege | LuaToken', 'TokenAssignPrimary | TokenDuplicate | TokenQuery', 'CreateSuspended',
        'OpenProcessToken(process.Process, TokenDuplicate | TokenQuery', 'GetTokenElevation', 'TokenElevationTypeDefault', 'TokenElevationTypeLimited',
        'VerifyNoPowerfulEnabledPrivileges', 'TokenPrivileges', 'SePrivilegeEnabled', 'LookupPrivilegeValue', 'SeChangeNotifyPrivilege',
        'DuplicateTokenEx', 'CheckTokenMembership(impersonationToken',
        'AssignProcessToJobObject', 'JobObjectLimitKillOnJobClose', 'TerminateJobObject', 'ResumeThread',
        'BuildSanitizedEnvironmentBlock', 'COREHOST_', 'APP_CONTEXT_', 'NATIVE_DLL_SEARCH_DIRECTORIES',
        'DOTNET_STARTUP_HOOKS', 'SPACELENS_STARTUP_HOOK_MARKER',
        'SpaceLens.exe', 'SpaceLens-Setup.exe', 'args[1], "--self-test"'
    )) {
        if ($launcherSource -notmatch [regex]::Escape($launcherControl)) { throw "Restricted launcher is missing security control: $launcherControl" }
    }
    $childTokenCheck = $launcherSource.IndexOf('VerifyRestrictedNonAdministrator(childToken', [StringComparison]::Ordinal)
    $childResume = $launcherSource.IndexOf('ResumeThread(process.Thread)', [StringComparison]::Ordinal)
    if ($childTokenCheck -lt 0 -or $childResume -le $childTokenCheck) { throw 'The suspended child token must be verified before its first instruction is resumed.' }
    if ($launcherSource -notmatch '(?s)commandLine,\s*IntPtr\.Zero,\s*IntPtr\.Zero,\s*false,\s*CreateSuspended \| CreateUnicodeEnvironment \| CreateNoWindow,\s*environment,') {
        throw 'The sanitized environment block must be passed as lpEnvironment, never as process/thread security attributes.'
    }
    if ($launcherSource -match '(?im)Process\.Start|UseShellExecute|cmd\.exe|powershell') { throw 'Restricted launcher must never invoke a shell or managed process launcher.' }
    $launcherNativeSecurity = Get-Content -Raw -LiteralPath (Join-Path $repository 'tools\RestrictedProcessLauncher\NativeSecurity.cs')
    if ($launcherNativeSecurity -notmatch 'DefaultDllImportSearchPaths\(DllImportSearchPath\.System32\)') { throw 'Restricted launcher must resolve its native APIs only from System32.' }

    $releaseBuild = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Build-Release.ps1')
    foreach ($buildControl in @('Test-CurrentProcessAdministrator', 'restrictedTestLauncher', 'RestrictedProcessLauncher.csproj', 'The restricted CI launcher accepts only the exact packaged --self-test command')) {
        if ($releaseBuild -notmatch [regex]::Escape($buildControl)) { throw "Build-Release lacks restricted elevated-host test control: $buildControl" }
    }
    $startupHookTest = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Test-StartupHookIsolation.ps1')
    if ($startupHookTest -notmatch '\[string\]\$RestrictedLauncher' -or $startupHookTest -notmatch 'Start-Process -FilePath \(\[IO\.Path\]::GetFullPath\(\$RestrictedLauncher\)\)') {
        throw 'Startup-hook isolation must support the same restricted packaged-test launcher on elevated hosts.'
    }
    foreach ($productionSource in Get-ChildItem -LiteralPath (Join-Path $repository 'src') -Filter '*.cs' -File -Recurse) {
        if ((Get-Content -Raw -LiteralPath $productionSource.FullName) -match 'RestrictedProcessLauncher') {
            throw "Production source must not reference the CI-only restricted launcher: $($productionSource.FullName)"
        }
    }

    $builtLauncher = Join-Path $repository 'artifacts\intermediate\restricted-process-launcher\RestrictedProcessLauncher.exe'
    if (Test-Path -LiteralPath $builtLauncher -PathType Leaf) {
        $negative = Start-Process -FilePath $builtLauncher -PassThru -Wait -WindowStyle Hidden
        try { $negativeExit = $negative.ExitCode } finally { $negative.Dispose() }
        if ($negativeExit -ne 125) { throw "Restricted launcher did not fail closed for an invalid invocation: $negativeExit" }
    }

    $appProgram = Get-Content -Raw -LiteralPath (Join-Path $repository 'src\SpaceLens\Program.cs')
    $appElevationGate = $appProgram.IndexOf('TryGetCurrentProcessElevation', [StringComparison]::Ordinal)
    foreach ($sensitiveEntry in @('CrashLog.Initialize()', 'args.Contains("--self-test")', '"--integration-test-elevated-scan"', '"--verify-update-manifest"', '"--uninstall-helper"')) {
        $entryIndex = $appProgram.IndexOf($sensitiveEntry, [StringComparison]::Ordinal)
        if ($appElevationGate -lt 0 -or $entryIndex -lt 0 -or $entryIndex -lt $appElevationGate) {
            throw "SpaceLens elevation refusal must precede production command entry $sensitiveEntry."
        }
    }
    $setupProgram = Get-Content -Raw -LiteralPath (Join-Path $repository 'src\SpaceLens.Setup\Program.cs')
    $setupElevationGate = $setupProgram.IndexOf('SetupProcessSecurity.IsElevated', [StringComparison]::Ordinal)
    $setupSelfTest = $setupProgram.IndexOf('args.Contains("--self-test")', [StringComparison]::Ordinal)
    if ($setupElevationGate -lt 0 -or $setupSelfTest -lt 0 -or $setupSelfTest -lt $setupElevationGate) {
        throw 'Setup elevation refusal must precede its self-test command entry.'
    }

    Write-Host 'Release security self-tests passed.' -ForegroundColor Green
}
finally {
    Remove-DisposableTestDirectory -Path $insideDirectory -OwnedRoot (Join-Path $repository 'artifacts') -ExpectedLeafPattern '^release-security-test-[0-9a-f]{32}$'
    Remove-DisposableTestDirectory -Path $outsideDirectory -OwnedRoot ([IO.Path]::GetTempPath()) -ExpectedLeafPattern '^spacelens-release-security-[0-9a-f]{32}$' -AllowSandboxAccessDenied
    Remove-DisposableTestDirectory -Path $syntheticWorkspace -OwnedRoot ([IO.Path]::GetTempPath()) -ExpectedLeafPattern '^spacelens-agent-root-[0-9a-f]{32}$' -AllowSandboxAccessDenied
}

# GitHub's pwsh wrapper propagates a stale native child exit code even when
# every PowerShell assertion and cleanup above succeeded. Publish the script's
# actual result directly; every failure path throws before this point.
exit 0
