param(
    [ValidateSet('Windows', 'Mac', 'Linux')][string]$Platform = 'Windows',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts')
)

$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $outputRoot ('.smoke-' + [Guid]::NewGuid().ToString('N'))
$archive = switch ($Platform) { Windows { 'Dungeon-Runners-Launcher.zip' } Mac { 'Dungeon-Runners-Launcher-Mac.zip' } Linux { 'Dungeon-Runners-Launcher-Linux.zip' } }
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
try {
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $outputRoot $archive), $stage)
    if ($Platform -eq 'Windows') {
        $executable = Join-Path $stage 'DungeonRunnersLauncher.exe'
        $start = [Diagnostics.ProcessStartInfo]::new($executable)
    } elseif ($Platform -eq 'Mac') {
        $executable = Join-Path $stage "Dungeon Runners Launcher.app/Contents/Resources/bin/osx-$architecture/DungeonRunnersLauncher"
        & chmod 755 $executable
        & codesign --verify --strict $executable
        if ($LASTEXITCODE -ne 0) { throw 'Packaged signature verification failed.' }
        $start = [Diagnostics.ProcessStartInfo]::new($executable)
    } else {
        $executable = Join-Path $stage "bin/linux-$architecture/DungeonRunnersLauncher"
        & chmod 755 $executable
        $start = [Diagnostics.ProcessStartInfo]::new('xvfb-run')
        $start.ArgumentList.Add('-a')
        $start.ArgumentList.Add($executable)
    }
    $start.ArgumentList.Add('--smoke-test')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw 'Packaged startup timed out.' }
        if ($process.ExitCode -ne 0) { throw "Packaged startup failed: $($process.ExitCode)" }
    } finally { $process.Dispose() }
    Write-Output "PASS $Platform packaged graphical startup."
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $prefix = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedStage) -notmatch '^\.smoke-[0-9a-f]{32}$') { throw 'Invalid staging directory.' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
