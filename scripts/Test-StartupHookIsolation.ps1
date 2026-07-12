[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$WorkDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw "SpaceLens executable is missing: $Executable" }
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
    try {
        [Environment]::SetEnvironmentVariable('DOTNET_STARTUP_HOOKS', $hook, [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable('SPACELENS_STARTUP_HOOK_MARKER', $marker, [EnvironmentVariableTarget]::Process)
        $process = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList '"--self-test"' -PassThru -Wait -WindowStyle Hidden
        try { $exitCode = $process.ExitCode } finally { $process.Dispose() }
    }
    finally {
        [Environment]::SetEnvironmentVariable('DOTNET_STARTUP_HOOKS', $oldHook, [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable('SPACELENS_STARTUP_HOOK_MARKER', $oldMarker, [EnvironmentVariableTarget]::Process)
    }

    if ($exitCode -ne 0) { throw "SpaceLens startup-hook isolation self-test failed with exit code $exitCode." }
    if (Test-Path -LiteralPath $marker) { throw 'DOTNET_STARTUP_HOOKS executed inside the production SpaceLens apphost.' }
    Write-Host 'Startup-hook isolation regression test passed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
