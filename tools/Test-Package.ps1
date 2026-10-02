param(
    [ValidateSet('Windows', 'Mac', 'Linux')][string]$Platform = 'Windows',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts')
)

$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $outputRoot ('.smoke-' + [Guid]::NewGuid().ToString('N'))
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$mounted = $false
[IO.Directory]::CreateDirectory($stage) | Out-Null

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $stage
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Packaged startup timed out.' }
        if ($process.ExitCode -ne 0) { throw "Packaged startup failed: $($process.ExitCode)" }
    } finally { $process.Dispose() }
}

try {
    $notices = Join-Path $stage 'notices.txt'
    if ($Platform -eq 'Windows') {
        $executable = Join-Path $stage 'DungeonRunnersLauncher.exe'
        Copy-Item -LiteralPath (Join-Path $outputRoot 'DungeonRunnersLauncher.exe') -Destination $executable
        Invoke-Checked $executable @('--licenses', $notices)
        Invoke-Checked $executable @('--smoke-test')
    } elseif ($Platform -eq 'Mac') {
        $mount = Join-Path $stage 'volume'
        & hdiutil attach -readonly -nobrowse -mountpoint $mount (Join-Path $outputRoot 'Dungeon-Runners-Launcher-Mac.dmg')
        if ($LASTEXITCODE -ne 0) { throw 'DMG mount failed.' }
        $mounted = $true
        $bundle = Join-Path $mount 'Dungeon Runners Launcher.app'
        & codesign --verify --deep --strict $bundle
        if ($LASTEXITCODE -ne 0) { throw 'Packaged signature verification failed.' }
        $executable = Join-Path $bundle "Contents/Resources/bin/osx-$architecture/DungeonRunnersLauncher"
        Invoke-Checked $executable @('--licenses', $notices)
        Invoke-Checked (Join-Path $bundle 'Contents/MacOS/DungeonRunnersLauncher') @('--smoke-test')
        Invoke-Checked '/usr/bin/open' @('-W', '-n', $bundle, '--args', '--smoke-test')
    } else {
        $name = if ($architecture -eq 'x64') { 'Dungeon-Runners-Launcher-Linux.AppImage' } else { 'Dungeon-Runners-Launcher-Linux-arm64.AppImage' }
        $executable = Join-Path $stage $name
        Copy-Item -LiteralPath (Join-Path $outputRoot $name) -Destination $executable
        & chmod 755 $executable
        if ($LASTEXITCODE -ne 0) { throw 'AppImage permissions failed.' }
        Invoke-Checked $executable @('--appimage-extract-and-run', '--licenses', $notices)
        Invoke-Checked 'xvfb-run' @('-a', $executable, '--appimage-extract-and-run', '--smoke-test')
    }
    $content = Get-Content -LiteralPath $notices -Raw
    foreach ($required in @('MIT License', 'Avalonia', 'SIL OPEN FONT LICENSE', 'dotnet-', 'NOTICES.txt')) {
        if (-not $content.Contains($required, [StringComparison]::OrdinalIgnoreCase)) { throw "Embedded notice missing: $required" }
    }
    $updateBinary = if ($Platform -eq 'Windows') { Join-Path $outputRoot 'DungeonRunnersLauncher.exe' } elseif ($Platform -eq 'Mac') { Join-Path $outputRoot "DungeonRunnersLauncher-osx-$architecture" } else { Join-Path $outputRoot "DungeonRunnersLauncher-linux-$architecture" }
    if ($Platform -eq 'Mac' -and (Get-FileHash -LiteralPath $updateBinary).Hash -ne (Get-FileHash -LiteralPath $executable).Hash) { throw 'Mac update differs from the packaged launcher.' }
    $testScript = Join-Path $PSScriptRoot 'Test-LauncherUpdate.ps1'
    $testRoot = Join-Path $stage 'update test'
    if ($IsLinux) {
        & xvfb-run -a pwsh -NoLogo -NoProfile -File $testScript -Executable $updateBinary -Directory $testRoot
        if ($LASTEXITCODE -ne 0) { throw 'Packaged update test failed.' }
    } else { & $testScript -Executable $updateBinary -Directory $testRoot }
    Write-Output "PASS $Platform standalone package, embedded notices and graphical startup."
} finally {
    if ($mounted) {
        for ($attempt = 0; $attempt -lt 3; $attempt++) {
            & hdiutil detach $mount
            if ($LASTEXITCODE -eq 0) { break }
            Start-Sleep -Seconds 2
        }
        if ($LASTEXITCODE -ne 0) { & hdiutil detach -force $mount }
        if ($LASTEXITCODE -ne 0) { throw 'DMG unmount failed; temporary mount preserved.' }
    }
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $prefix = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedStage) -notmatch '^\.smoke-[0-9a-f]{32}$') { throw 'Invalid staging directory.' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
