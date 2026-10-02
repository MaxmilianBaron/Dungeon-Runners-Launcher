param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$Directory)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory)
$source = [IO.Path]::GetFullPath($Executable)
$name = if ($IsWindows) { 'DungeonRunnersLauncher.exe' } else { 'DungeonRunnersLauncher' }
$work = Join-Path $root '.dr-client'
$stageName = 'launcher-update-' + [Guid]::NewGuid().ToString('N')
$stage = Join-Path $work $stageName
[IO.Directory]::CreateDirectory($stage) | Out-Null
$target = Join-Path $root $name
$helper = Join-Path $stage $name
[IO.File]::WriteAllText($target, 'previous launcher fixture')
[IO.File]::WriteAllText((Join-Path $root 'keep.txt'), 'user file')
[IO.File]::WriteAllText((Join-Path $work 'owner'), 'Dungeon-Runners-Launcher:1')
[IO.File]::WriteAllText((Join-Path $work 'manifest.json'), '{}')
$before = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $work 'launcher.sha256'), $before)
Copy-Item -LiteralPath $source -Destination $helper
if (-not $IsWindows) { & chmod 755 $helper; if ($LASTEXITCODE -ne 0) { throw 'Update helper permissions failed.' } }
$parentScript = Join-Path $stage 'parent.ps1'
[IO.File]::WriteAllText($parentScript, 'param([string]$StopFile) for ($i=0; $i -lt 600; $i++) { if (Test-Path -LiteralPath $StopFile) { exit 0 }; Start-Sleep -Milliseconds 100 }; exit 1')
$stopFile = Join-Path $stage 'parent-exit'
$parentStart = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
$parentStart.UseShellExecute = $false
$parentStart.CreateNoWindow = $true
foreach ($argument in @('-NoLogo', '-NoProfile', '-File', $parentScript, '-StopFile', $stopFile)) { $parentStart.ArgumentList.Add($argument) }
$parent = [Diagnostics.Process]::Start($parentStart)
$worker = $null
try {
    $stamp = $parent.StartTime.ToUniversalTime().Ticks
    if ($IsLinux) {
        $stat = [IO.File]::ReadAllText("/proc/$($parent.Id)/stat")
        $stamp = [long]$stat.Substring($stat.LastIndexOf(')') + 2).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[19]
    }
    $plan = @{ root=$root; directory=$stageName; previousHash=$before; newHash=$expected; parentId=$parent.Id; parentStart=$stamp }
    $planPath = Join-Path $stage 'plan.json'
    [IO.File]::WriteAllText($planPath, ($plan | ConvertTo-Json -Compress))
    $start = [Diagnostics.ProcessStartInfo]::new($helper)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $root
    $start.ArgumentList.Add('--apply-launcher-update')
    $start.ArgumentList.Add($planPath)
    $worker = [Diagnostics.Process]::Start($start)
    $ready = Join-Path $stage 'ready'
    for ($i=0; $i -lt 150 -and -not (Test-Path -LiteralPath $ready); $i++) {
        if ($worker.HasExited) { throw "Update worker failed before readiness: $($worker.ExitCode)" }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $ready)) { throw 'Update worker did not become ready.' }
    if ($worker.HasExited -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $before) { throw 'Update did not wait for the previous process.' }
    [IO.File]::WriteAllText($stopFile, '')
    if (-not $parent.WaitForExit(10000) -or $parent.ExitCode -ne 0) { throw 'Previous process did not exit.' }
    if (-not $worker.WaitForExit(45000) -or $worker.ExitCode -ne 0) { throw 'Update worker did not complete.' }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected) { throw 'Updated executable does not match the download.' }
    if ((Get-FileHash -LiteralPath (Join-Path $stage 'previous') -Algorithm SHA256).Hash -ne $before) { throw 'Previous executable backup is missing.' }
    if ([IO.File]::ReadAllText((Join-Path $work 'launcher.sha256')) -ne $expected) { throw 'Launcher ownership marker is incorrect.' }
    if ([IO.File]::ReadAllText((Join-Path $root 'keep.txt')) -ne 'user file') { throw 'Unrelated file changed.' }
    $started = $false
    foreach ($process in [Diagnostics.Process]::GetProcesses()) {
        try { if ($process.MainModule.FileName -eq $target) { $started = $true } } catch { } finally { $process.Dispose() }
    }
    if (-not $started) { throw 'Updated launcher was not restarted.' }
    Write-Output 'PASS packaged launcher update: wait, verified replacement, backup, restart and unrelated-file preservation.'
} catch {
    $receipt = Join-Path $work 'launcher-update-result.json'
    if (Test-Path -LiteralPath $receipt) { Write-Output ([IO.File]::ReadAllText($receipt)) }
    throw
} finally {
    if (-not $parent.HasExited) { $parent.Kill($true); $parent.WaitForExit(5000) | Out-Null }
    $parent.Dispose()
    if ($worker) { if (-not $worker.HasExited) { $worker.Kill($true); $worker.WaitForExit(5000) | Out-Null }; $worker.Dispose() }
    foreach ($process in [Diagnostics.Process]::GetProcesses()) {
        try { if ($process.MainModule.FileName -eq $target) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null } } catch { } finally { $process.Dispose() }
    }
}
