param(
    [ValidateSet('Windows', 'Mac', 'Linux')][string]$Platform = 'Windows',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts')
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$stage = Join-Path $outputRoot ('.package-' + [Guid]::NewGuid().ToString('N'))
$distribution = Join-Path $stage 'distribution'
[IO.Directory]::CreateDirectory($distribution) | Out-Null
$runtimes = switch ($Platform) { Windows { @('win-x64') } Mac { @('osx-x64', 'osx-arm64') } Linux { @('linux-x64', 'linux-arm64') } }
$bundle = Join-Path $distribution 'Dungeon Runners Launcher.app'
$resourceRoot = Join-Path $bundle 'Contents/Resources'
$outputs = @()

function Copy-PackageNotices([string]$Runtime, [string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    $assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src/Client.Launcher/obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        $directory = $null
        foreach ($cache in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $cache $library.Value.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $directory = $candidate; break }
        }
        if (-not $directory) { throw "Package unavailable: $($library.Name)" }
        foreach ($file in $library.Value.files) {
            if ([IO.Path]::GetFileName($file) -match '^(LICENSE|LICENCE|THIRD.PARTY.NOTICES)(\.[a-z]+)?$') {
                $name = ($library.Name + '-' + $file) -replace '[/\\]', '-'
                Copy-Item -LiteralPath (Join-Path $directory $file) -Destination (Join-Path $Destination $name) -Force
            }
        }
    }
    $package = "Microsoft.NETCore.App.Runtime.$Runtime"
    $dependencies = @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies)
    $ranges = @($dependencies | Where-Object name -eq $package | Select-Object -ExpandProperty version -Unique)
    if ($ranges.Count -ne 1 -or $ranges[0] -notmatch '^\[(\d+\.\d+\.\d+),\s*\1\]$') { throw "Runtime version unavailable: $package" }
    $version = $Matches[1]
    $directory = $null
    foreach ($cache in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $cache ($package.ToLowerInvariant() + '/' + $version)
        if (Test-Path -LiteralPath $candidate -PathType Container) { $directory = $candidate; break }
    }
    if (-not $directory) { throw "Runtime package unavailable: $package" }
    Copy-Item -LiteralPath (Join-Path $directory 'LICENSE.TXT') -Destination (Join-Path $Destination "dotnet-$Runtime-LICENSE.txt")
    Copy-Item -LiteralPath (Join-Path $directory 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $Destination "dotnet-$Runtime-NOTICES.txt")
}

function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Sha256) {
    Invoke-WebRequest -Uri $Url -OutFile $Path
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) { throw "Download checksum mismatch: $Url" }
}

try {
    if ($Platform -eq 'Mac' -and -not $IsMacOS) { throw 'Build DMGs on macOS.' }
    if ($Platform -eq 'Linux' -and -not $IsLinux) { throw 'Build AppImages on Linux.' }
    if ($Platform -eq 'Linux') {
        $hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        $toolArchitecture, $toolHash = switch ($hostArchitecture) {
            X64 { 'x86_64'; 'ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0' }
            Arm64 { 'aarch64'; 'f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158' }
            default { throw 'AppImage packaging requires x64 or arm64.' }
        }
        $appImageTool = Join-Path $stage 'appimagetool.AppImage'
        Get-VerifiedFile "https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-$toolArchitecture.AppImage" $appImageTool $toolHash
        & chmod 755 $appImageTool
        if ($LASTEXITCODE -ne 0) { throw 'AppImage tool permissions failed.' }
    }
    foreach ($runtime in $runtimes) {
        $publish = Join-Path $stage $runtime
        $notices = Join-Path $stage "notices-$runtime"
        dotnet restore (Join-Path $projectRoot 'src/Client.Launcher') -r $runtime
        if ($LASTEXITCODE -ne 0) { throw "Launcher restore failed: $runtime" }
        Copy-PackageNotices $runtime $notices
        dotnet publish (Join-Path $projectRoot 'src/Client.Launcher') -c Release -r $runtime --no-restore --self-contained true -p:DebugType=None -p:DebugSymbols=false "-p:LauncherNoticeDirectory=$notices" -o $publish
        if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed: $runtime" }
        $executable = if ($Platform -eq 'Windows') { 'DungeonRunnersLauncher.exe' } else { 'DungeonRunnersLauncher' }
        $publishedFiles = @(Get-ChildItem -LiteralPath $publish -File)
        if (-not ($publishedFiles.Name -contains $executable)) { throw "Launcher executable missing: $runtime" }
        foreach ($file in $publishedFiles) {
            if ($file.Name -ne $executable -and $file.Extension -ne '.pdb') { throw "Unexpected unbundled output: $($file.Name)" }
        }
        $binary = Join-Path $publish $executable
        if ($Platform -eq 'Windows') {
            $destination = Join-Path $outputRoot $executable
            & (Join-Path $PSScriptRoot 'Build-WindowsInstaller.ps1') -ModernExecutable $binary -OutputPath $destination
            $outputs += $destination
        } elseif ($Platform -eq 'Mac') {
            $destination = Join-Path $resourceRoot "bin/$runtime"
            [IO.Directory]::CreateDirectory($destination) | Out-Null
            Copy-Item -LiteralPath $binary -Destination $destination
            & chmod 755 (Join-Path $destination $executable)
            if ($LASTEXITCODE -ne 0) { throw 'Executable permission failed.' }
            & codesign --force --sign - (Join-Path $destination $executable)
            if ($LASTEXITCODE -ne 0) { throw 'Local executable signing failed.' }
            Copy-Item -LiteralPath $notices -Destination (Join-Path $resourceRoot "licenses-$runtime") -Recurse
        } else {
            $updateBinary = Join-Path $outputRoot "DungeonRunnersLauncher-$runtime"
            Copy-Item -LiteralPath $binary -Destination $updateBinary -Force
            $outputs += $updateBinary
            $architecture, $runtimeHash = if ($runtime -eq 'linux-x64') {
                'x86_64'; '2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d'
            } else {
                'aarch64'; '00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444'
            }
            $appDir = Join-Path $stage "$runtime.AppDir"
            $bin = Join-Path $appDir 'usr/bin'
            [IO.Directory]::CreateDirectory($bin) | Out-Null
            Copy-Item -LiteralPath $binary -Destination $bin
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'linux/AppRun') -Destination $appDir
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'linux/dungeon-runners-launcher.desktop') -Destination $appDir
            Copy-Item -LiteralPath (Join-Path $projectRoot 'src/Client.Launcher/Assets/DungeonRunners.png') -Destination (Join-Path $appDir 'dungeon-runners-launcher.png')
            Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination (Join-Path $appDir 'licenses') -Recurse
            Copy-Item -LiteralPath $notices -Destination (Join-Path $appDir 'licenses/runtime') -Recurse
            foreach ($name in @('LICENSE', 'NOTICE.txt')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $appDir }
            & chmod 755 (Join-Path $bin $executable) (Join-Path $appDir 'AppRun')
            if ($LASTEXITCODE -ne 0) { throw 'AppDir permissions failed.' }
            $imageRuntime = Join-Path $stage "runtime-$architecture"
            Get-VerifiedFile "https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-$architecture" $imageRuntime $runtimeHash
            $name = if ($runtime -eq 'linux-x64') { 'Dungeon-Runners-Launcher-Linux.AppImage' } else { 'Dungeon-Runners-Launcher-Linux-arm64.AppImage' }
            $destination = Join-Path $outputRoot $name
            $start = [Diagnostics.ProcessStartInfo]::new($appImageTool)
            $start.UseShellExecute = $false
            $start.Environment['ARCH'] = $architecture
            foreach ($argument in @('--appimage-extract-and-run', '--no-appstream', '--runtime-file', $imageRuntime, $appDir, $destination)) { $start.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::Start($start)
            try {
                $process.WaitForExit()
                if ($process.ExitCode -ne 0) { throw "AppImage packaging failed: $runtime" }
            } finally { $process.Dispose() }
            & chmod 755 $destination
            if ($LASTEXITCODE -ne 0) { throw 'AppImage permissions failed.' }
            $outputs += $destination
        }
    }
    if ($Platform -eq 'Linux') {
        & (Join-Path $PSScriptRoot 'Package-LinuxInstaller.ps1') -OutputDirectory $outputRoot
        $outputs += Join-Path $outputRoot 'Dungeon-Runners-Launcher-Linux.run'
        $outputs += Join-Path $outputRoot 'Dungeon-Runners-Launcher-SteamDeck.run'
    }
    if ($Platform -eq 'Mac') {
        Copy-Item -LiteralPath (Join-Path $projectRoot 'src/Client.Launcher/Assets/DungeonRunners.icns') -Destination $resourceRoot
        Copy-Item -LiteralPath (Join-Path $projectRoot 'Launcher.sh') -Destination $resourceRoot
        foreach ($name in @('LICENSE', 'NOTICE.txt')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $resourceRoot }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination (Join-Path $resourceRoot 'licenses') -Recurse
        $macos = Join-Path $bundle 'Contents/MacOS'
        [IO.Directory]::CreateDirectory($macos) | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'macOS/Launch.sh') -Destination (Join-Path $macos 'DungeonRunnersLauncher')
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'macOS/Info.plist') -Destination (Join-Path $bundle 'Contents/Info.plist')
        & chmod 755 (Join-Path $macos 'DungeonRunnersLauncher') (Join-Path $resourceRoot 'Launcher.sh')
        if ($LASTEXITCODE -ne 0) { throw 'App bundle permissions failed.' }
        & codesign --force --deep --sign - $bundle
        if ($LASTEXITCODE -ne 0) { throw 'App bundle signing failed.' }
        & codesign --verify --deep --strict $bundle
        if ($LASTEXITCODE -ne 0) { throw 'App bundle verification failed.' }
        foreach ($runtime in $runtimes) {
            $updateBinary = Join-Path $outputRoot "DungeonRunnersLauncher-$runtime"
            Copy-Item -LiteralPath (Join-Path $resourceRoot "bin/$runtime/DungeonRunnersLauncher") -Destination $updateBinary -Force
            $outputs += $updateBinary
        }
        $image = Join-Path $stage 'Dungeon-Runners-Launcher-Mac.dmg'
        $payloadBytes = (Get-ChildItem -LiteralPath $distribution -File -Recurse | Measure-Object -Property Length -Sum).Sum
        $imageSizeMiB = [long][Math]::Ceiling($payloadBytes / 1MB * 1.2) + 64
        & hdiutil create -ov -fs HFS+ -size ($imageSizeMiB.ToString([Globalization.CultureInfo]::InvariantCulture) + 'm') -format UDZO -volname 'Dungeon Runners Launcher' -srcfolder $distribution $image
        if ($LASTEXITCODE -ne 0) { throw 'DMG creation failed.' }
        $destination = Join-Path $outputRoot 'Dungeon-Runners-Launcher-Mac.dmg'
        Move-Item -LiteralPath $image -Destination $destination -Force
        $outputs += $destination
    }
    $outputs | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 }
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $prefix = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedStage) -notmatch '^\.package-[0-9a-f]{32}$') { throw 'Invalid staging directory.' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
