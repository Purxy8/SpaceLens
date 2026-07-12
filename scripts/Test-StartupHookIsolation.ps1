[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [string]$RestrictedLauncher = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "SpaceLens executable is missing: $Executable" }
if ($RestrictedLauncher -and -not (Test-Path -LiteralPath $RestrictedLauncher -PathType Leaf)) { throw "Restricted test launcher is missing: $RestrictedLauncher" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 10 SDK is required for the startup-hook regression test.' }

$root = [IO.Path]::GetFullPath((Join-Path $WorkDirectory ('startup-hook-probe-' + [Guid]::NewGuid().ToString('N'))))
$project = Join-Path $root 'StartupHookProbe.csproj'
$source = Join-Path $root 'StartupHook.cs'
$output = Join-Path $root 'out'
$marker = Join-Path $root 'hook-executed.marker'
try {
    New-Item -ItemType Directory -Path $root, $output -Force | Out-Null
    [IO.File]::WriteAllText($project, @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>SpaceLens.StartupHookProbe</AssemblyName>
  </PropertyGroup>
</Project>
'@, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($source, @'
public static class StartupHook
{
    public static void Initialize()
    {
        string? marker = Environment.GetEnvironmentVariable("SPACELENS_STARTUP_HOOK_MARKER");
        if (!string.IsNullOrWhiteSpace(marker)) File.WriteAllText(marker, "executed");
    }
}
'@, [Text.UTF8Encoding]::new($false))

    & dotnet build $project -c Release -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the harmless startup-hook regression fixture.' }
    $hook = Join-Path $output 'SpaceLens.StartupHookProbe.dll'
    if (-not (Test-Path -LiteralPath $hook -PathType Leaf)) { throw 'Startup-hook fixture output is missing.' }

    $oldHook = [Environment]::GetEnvironmentVariable('DOTNET_STARTUP_HOOKS', [EnvironmentVariableTarget]::Process)
    $oldMarker = [Environment]::GetEnvironmentVariable('SPACELENS_STARTUP_HOOK_MARKER', [EnvironmentVariableTarget]::Process)
    $poisonNames = @('COR_ENABLE_PROFILING', 'CORECLR_ENABLE_PROFILING', 'CORECLR_PROFILER', 'COMPlus_ReadyToRun', 'DOTNET_ADDITIONAL_DEPS', 'SPACELENS_RECYCLE_INTEGRATION_TEST')
    $oldPoison = @{}
    foreach ($name in $poisonNames) { $oldPoison[$name] = [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::Process) }
    try {
        [Environment]::SetEnvironmentVariable('DOTNET_STARTUP_HOOKS', $hook, [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable('SPACELENS_STARTUP_HOOK_MARKER', $marker, [EnvironmentVariableTarget]::Process)
        if ($RestrictedLauncher) {
            [Environment]::SetEnvironmentVariable('COR_ENABLE_PROFILING', '1', [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable('CORECLR_ENABLE_PROFILING', '1', [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable('CORECLR_PROFILER', '{00000000-0000-0000-0000-000000000001}', [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable('COMPlus_ReadyToRun', '0', [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable('DOTNET_ADDITIONAL_DEPS', (Join-Path $root 'missing.deps.json'), [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable('SPACELENS_RECYCLE_INTEGRATION_TEST', '1', [EnvironmentVariableTarget]::Process)
        }
        $resolvedExecutable = [IO.Path]::GetFullPath($Executable)
        if ($RestrictedLauncher) {
            if ($resolvedExecutable.Contains('"')) { throw 'SpaceLens startup-hook test path contains an unsupported quote.' }
            $savedInjection = @{}
            try {
                foreach ($entry in [Environment]::GetEnvironmentVariables([EnvironmentVariableTarget]::Process).GetEnumerator()) {
                    $name = [string]$entry.Key
                    $dangerous = $false
                    foreach ($prefix in @('COR_', 'CORECLR_', 'COMPlus_', 'DOTNET_', 'SPACELENS_')) { if ($name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $dangerous = $true; break } }
                    if ($dangerous -and $name -cne 'DOTNET_STARTUP_HOOKS' -and $name -cne 'SPACELENS_STARTUP_HOOK_MARKER') {
                        $savedInjection[$name] = [string]$entry.Value
                        [Environment]::SetEnvironmentVariable($name, $null, [EnvironmentVariableTarget]::Process)
                    }
                }
                $process = Start-Process -FilePath ([IO.Path]::GetFullPath($RestrictedLauncher)) -ArgumentList ('"' + $resolvedExecutable + '" "--self-test"') -PassThru -Wait -NoNewWindow
            }
            finally { foreach ($entry in $savedInjection.GetEnumerator()) { [Environment]::SetEnvironmentVariable([string]$entry.Key, [string]$entry.Value, [EnvironmentVariableTarget]::Process) } }
        }
        else {
            $process = Start-Process -FilePath $resolvedExecutable -ArgumentList '"--self-test"' -PassThru -Wait -WindowStyle Hidden
        }
        try { $exitCode = $process.ExitCode } finally { $process.Dispose() }
    }
    finally {
        [Environment]::SetEnvironmentVariable('DOTNET_STARTUP_HOOKS', $oldHook, [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable('SPACELENS_STARTUP_HOOK_MARKER', $oldMarker, [EnvironmentVariableTarget]::Process)
        foreach ($name in $poisonNames) { [Environment]::SetEnvironmentVariable($name, $oldPoison[$name], [EnvironmentVariableTarget]::Process) }
    }

    if ($exitCode -ne 0) { throw "SpaceLens startup-hook isolation self-test failed with exit code $exitCode." }
    if (Test-Path -LiteralPath $marker) { throw 'DOTNET_STARTUP_HOOKS executed inside the production SpaceLens apphost.' }
    Write-Host 'Startup-hook isolation regression test passed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
