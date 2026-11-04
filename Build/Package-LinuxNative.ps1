param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts'),
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $Version) { $Version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'A three-component release version is required.' }
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$stage = Join-Path $outputRoot ('.native-packages-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stage) | Out-Null

function Get-PackagingTool {
    $hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    $asset, $digest = if ($IsWindows -and $hostArchitecture -eq 'X64') {
        'nfpm_2.48.0_Windows_x86_64.zip'; '8ce12620212f5db557e370e35e2279ac7b106ea51e6a546a2be37a1a5d5bd8de'
    } elseif ($IsLinux -and $hostArchitecture -eq 'X64') {
        'nfpm_2.48.0_Linux_x86_64.tar.gz'; '6d7f9a28b2f397934403950fcbba83f598b08d02edbe79a0815b3b4897341750'
    } elseif ($IsLinux -and $hostArchitecture -eq 'Arm64') {
        'nfpm_2.48.0_Linux_arm64.tar.gz'; 'a9a75213763783b7669719e505474c748c5ad7dc66591a195619deb50f85c2f4'
    } else { throw 'Build native Linux packages on Windows x64 or Linux x64/ARM64.' }
    $cache = Join-Path $projectRoot 'Temp/package-tools'
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    $archive = Join-Path $cache $asset
    if (-not (Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest -Uri "https://github.com/goreleaser/nfpm/releases/download/v2.48.0/$asset" -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $digest) { throw 'Packaging tool checksum mismatch.' }
    $directory = Join-Path $stage 'nfpm'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    if ($IsWindows) { Expand-Archive -LiteralPath $archive -DestinationPath $directory }
    else {
        & tar -xzf $archive -C $directory
        if ($LASTEXITCODE -ne 0) { throw 'Packaging tool extraction failed.' }
    }
    $tool = Join-Path $directory $(if ($IsWindows) { 'nfpm.exe' } else { 'nfpm' })
    if ($IsLinux) { & chmod 755 $tool; if ($LASTEXITCODE -ne 0) { throw 'Packaging tool permissions failed.' } }
    return $tool
}

function Add-PackageFile([object]$Files, [string]$Source, [string]$Destination, [int]$Mode = 420) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Package input missing: $Source" }
    $Files.Add(@{ src = $Source; dst = $Destination; file_info = @{ mode = $Mode; owner = 'root'; group = 'root' } })
}

try {
    $tool = Get-PackagingTool
    foreach ($architecture in @('x64', 'arm64')) {
        $binary = Join-Path $outputRoot "DungeonRunnersLauncher-linux-$architecture"
        $stream = [IO.File]::OpenRead($binary)
        try { $header = [byte[]]::new(20); if ($stream.Read($header, 0, $header.Length) -ne 20) { throw 'Truncated launcher.' } }
        finally { $stream.Dispose() }
        $machine = if ($architecture -eq 'x64') { 62 } else { 183 }
        if ($header[0] -ne 127 -or $header[1] -ne 69 -or $header[2] -ne 76 -or $header[3] -ne 70 -or $header[4] -ne 2 -or $header[5] -ne 1 -or [BitConverter]::ToUInt16($header, 18) -ne $machine) { throw "Wrong Linux executable architecture: $architecture" }
        $files = [Collections.Generic.List[object]]::new()
        Add-PackageFile $files $binary '/usr/lib/dungeon-runners-launcher/DungeonRunnersLauncher' 493
        Add-PackageFile $files (Join-Path $PSScriptRoot 'linux/native.desktop') '/usr/share/applications/dungeon-runners-launcher.desktop'
        Add-PackageFile $files (Join-Path $projectRoot 'src/Client.Launcher/Assets/DungeonRunners.png') '/usr/share/pixmaps/dungeon-runners-launcher.png'
        Add-PackageFile $files (Join-Path $projectRoot 'LICENSE') '/usr/share/doc/dungeon-runners-launcher/copyright'
        Add-PackageFile $files (Join-Path $projectRoot 'NOTICE.txt') '/usr/share/doc/dungeon-runners-launcher/NOTICE.txt'
        foreach ($notice in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'licenses') -File) {
            Add-PackageFile $files $notice.FullName ("/usr/share/doc/dungeon-runners-launcher/licenses/" + $notice.Name)
        }
        $config = @{
            name = 'dungeon-runners-launcher'; arch = $(if ($architecture -eq 'x64') { 'amd64' } else { 'arm64' }); platform = 'linux'
            version = $Version; release = '1'; section = 'games'; priority = 'optional'; license = 'MIT'
            maintainer = 'MaxmilianBaron <281639680+MaxmilianBaron@users.noreply.github.com>'
            homepage = 'https://github.com/MaxmilianBaron/Dungeon-Runners-Launcher'
            description = 'Install, update and play Dungeon Runners Reborn'; contents = $files.ToArray()
            overrides = @{
                deb = @{ depends = @('libc6 (>= 2.31)', 'libgcc-s1', 'libstdc++6', 'libfontconfig1', 'libice6', 'libsm6', 'libx11-6', 'libxrandr2', 'wmctrl', 'libssl3t64 | libssl3 | libssl1.1') }
                rpm = @{ depends = @('glibc >= 2.31', 'libgcc_s.so.1()(64bit)', 'libstdc++.so.6()(64bit)', 'libfontconfig.so.1()(64bit)', 'libICE.so.6()(64bit)', 'libSM.so.6()(64bit)', 'libX11.so.6()(64bit)', 'libXrandr.so.2()(64bit)', 'wmctrl', '(libssl.so.3()(64bit) or libssl.so.1.1()(64bit))') }
            }
            deb = @{ compression = 'gzip' }; rpm = @{ compression = 'gzip' }
        }
        $configuration = Join-Path $stage "nfpm-$architecture.json"
        [IO.File]::WriteAllText($configuration, ($config | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
        foreach ($format in @('deb', 'rpm')) {
            $suffix = if ($architecture -eq 'x64') { '' } else { '-arm64' }
            $destination = Join-Path $outputRoot "Dungeon-Runners-Launcher-Linux$suffix.$format"
            & $tool package --config $configuration --packager $format --target $destination
            if ($LASTEXITCODE -ne 0) { throw "Native package creation failed: $architecture/$format" }
            Get-FileHash -LiteralPath $destination -Algorithm SHA256
        }
    }
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $prefix = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedStage) -notmatch '^\.native-packages-[0-9a-f]{32}$') { throw 'Invalid native package staging directory.' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
