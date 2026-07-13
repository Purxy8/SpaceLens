[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseSigner,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedSignerSha256,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9 ._-]{1,98}[A-Za-z0-9]$')][string]$CngKeyName = 'SpaceLens Update Signing v2'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') { throw 'Update trust rotation requires Windows.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ($identity.Name -match '(?i)(sandbox|runner|service)') { throw 'Run trust rotation yourself from a normal interactive maintainer account, never an agent, runner, sandbox, or service identity.' }
if (-not [Environment]::UserInteractive) { throw 'Update trust rotation requires an interactive user session.' }
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run trust rotation from a normal non-elevated Windows session.' }

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$securityModule = Join-Path $PSScriptRoot 'ReleaseSecurity.psm1'
Import-Module $securityModule -Force
$initialProvenance = Get-CleanReleaseProvenance -Repository $repository

$signer = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ReleaseSigner).Path)
Assert-NativeReleaseSignerDirectory -ExecutablePath $signer
$signerLock = Open-VerifiedReleaseExecutable -Path $signer -ExpectedSha256 $ExpectedSignerSha256

$publicTarget = Join-Path $repository 'src\SpaceLens\assets\update-public-key.pem'
$fixtureTarget = Join-Path $repository 'src\SpaceLens\assets\update-selftest.json'
$assetsDirectory = Split-Path -Parent $publicTarget

function Assert-TrackedTrustAssets {
    Assert-ReleasePathHasNoReparsePoints -Path $assetsDirectory -StopAt $repository
    $trustEntries = @(Get-ChildItem -LiteralPath $assetsDirectory -Force | Where-Object { $_.Name -like 'update-*' })
    $expectedNames = @('update-public-key.pem', 'update-selftest.json')
    $difference = @(Compare-Object -ReferenceObject @($expectedNames | Sort-Object) -DifferenceObject @($trustEntries.Name | Sort-Object) -CaseSensitive)
    if ($difference.Count -ne 0 -or $trustEntries.Count -ne $expectedNames.Count) { throw 'The assets directory contains an unexpected update-trust target.' }
    foreach ($target in @($publicTarget, $fixtureTarget)) {
        Assert-ReleasePathHasNoReparsePoints -Path $target -StopAt $repository
        $item = Get-Item -LiteralPath $target -Force
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -le 0 -or $item.Length -gt 32KB) {
            throw "Tracked update-trust target is not a bounded regular file: $target"
        }
    }
    [void](Assert-PublicSpkiPemFile -Path $publicTarget)
}

try {
    Assert-TrackedTrustAssets
    # Safe preflight execution happens before the CNG key exists.
    Invoke-LockedReleaseSigner -Lock $signerLock -Arguments @('self-test')
    $finalProvenance = Get-CleanReleaseProvenance -Repository $repository
    if ($finalProvenance.Commit -ne $initialProvenance.Commit) { throw 'The source commit changed during trust-rotation preflight.' }

    # SECURITY BOUNDARY: this single locked native signer process creates the
    # non-exportable key, exports only its public SPKI, signs/verifies a fresh
    # 9.9.9 fixture, and transactionally replaces only the two tracked trust
    # assets. No executable is started after this call returns.
    Assert-TrackedTrustAssets
    Invoke-LockedReleaseSigner -Lock $signerLock -Arguments @('rotate-cng', $CngKeyName, $publicTarget, $fixtureTarget)
}
finally { $signerLock.Stream.Dispose() }

Write-Host 'Trust rotation completed. Review and commit only update-public-key.pem and update-selftest.json.' -ForegroundColor Green
Write-Host 'Keep v1.6.1 as a manual bootstrap release; older clients must not authenticate this key change solely through the old update channel.'
