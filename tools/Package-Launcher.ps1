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
$licenses = Join-Path $distribution 'licenses'
[IO.Directory]::CreateDirectory($licenses) | Out-Null
$runtimes = switch ($Platform) { Windows { @('win-x64') } Mac { @('osx-x64', 'osx-arm64') } Linux { @('linux-x64', 'linux-arm64') } }
$archiveName = switch ($Platform) { Windows { 'Dungeon-Runners-Launcher.zip' } Mac { 'Dungeon-Runners-Launcher-Mac.zip' } Linux { 'Dungeon-Runners-Launcher-Linux.zip' } }
$bundle = Join-Path $distribution 'Dungeon Runners Launcher.app'
$resourceRoot = if ($Platform -eq 'Mac') { Join-Path $bundle 'Contents/Resources' } else { $distribution }

try {
    foreach ($runtime in $runtimes) {
        $publish = Join-Path $stage $runtime
        dotnet publish (Join-Path $projectRoot 'src/Client.Launcher') -c Release -r $runtime --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $publish
        if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed: $runtime" }
        $executable = if ($Platform -eq 'Windows') { 'DungeonRunnersLauncher.exe' } else { 'DungeonRunnersLauncher' }
        $destination = if ($Platform -eq 'Windows') { $distribution } else { Join-Path $resourceRoot "bin/$runtime" }
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        $publishedFiles = @(Get-ChildItem -LiteralPath $publish -File)
        if (-not ($publishedFiles.Name -contains $executable)) { throw "Launcher executable missing: $runtime" }
        foreach ($file in $publishedFiles) {
            if ($file.Extension -eq '.pdb') { continue }
            if ($file.Name -ne $executable) { throw "Unexpected unbundled output: $($file.Name)" }
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
        if (-not $IsWindows -and $Platform -ne 'Windows') {
            & chmod 755 (Join-Path $destination $executable)
            if ($LASTEXITCODE -ne 0) { throw 'Executable permission failed.' }
        }
        if ($IsMacOS -and $Platform -eq 'Mac') {
            & codesign --force --sign - (Join-Path $destination $executable)
            if ($LASTEXITCODE -ne 0) { throw 'Local executable signing failed.' }
        }
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
                    Copy-Item -LiteralPath (Join-Path $directory $file) -Destination (Join-Path $licenses $name) -Force
                }
            }
        }
        $package = "Microsoft.NETCore.App.Runtime.$runtime"
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
        Copy-Item -LiteralPath (Join-Path $directory 'LICENSE.TXT') -Destination (Join-Path $licenses "dotnet-$runtime-LICENSE.txt")
        Copy-Item -LiteralPath (Join-Path $directory 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses "dotnet-$runtime-NOTICES.txt")
    }
    foreach ($name in @('LICENSE', 'NOTICE.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $distribution }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'licenses') -File) { Copy-Item -LiteralPath $file.FullName -Destination $licenses }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src/Client.Launcher/Assets/Fonts/LICENSE.md') -Destination (Join-Path $licenses 'SourceSerif4-OFL.txt')
    if ($Platform -eq 'Windows') {
        Copy-Item -LiteralPath (Join-Path $projectRoot 'Install.cmd') -Destination $distribution
    } else {
        Copy-Item -LiteralPath (Join-Path $projectRoot 'Launcher.sh') -Destination $resourceRoot
        if ($Platform -eq 'Linux') {
            Copy-Item -LiteralPath (Join-Path $projectRoot 'Install.sh') -Destination $distribution
        } else {
            if (-not $IsMacOS) { throw 'Build the Mac package on macOS to sign and verify the application bundle.' }
            Copy-Item -LiteralPath (Join-Path $projectRoot 'Install.command') -Destination $distribution
            $macos = Join-Path $bundle 'Contents/MacOS'
            [IO.Directory]::CreateDirectory($macos) | Out-Null
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'macOS/Launch.sh') -Destination (Join-Path $macos 'DungeonRunnersLauncher')
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'macOS/Info.plist') -Destination (Join-Path $bundle 'Contents/Info.plist')
            & chmod 755 (Join-Path $macos 'DungeonRunnersLauncher') (Join-Path $resourceRoot 'Launcher.sh') (Join-Path $distribution 'Install.command')
            if ($LASTEXITCODE -ne 0) { throw 'App bundle permissions failed.' }
            & codesign --force --deep --sign - $bundle
            if ($LASTEXITCODE -ne 0) { throw 'App bundle signing failed.' }
            & codesign --verify --deep --strict $bundle
            if ($LASTEXITCODE -ne 0) { throw 'App bundle verification failed.' }
        }
    }
    $archive = Join-Path $stage $archiveName
    [IO.Compression.ZipFile]::CreateFromDirectory($distribution, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    if ($Platform -ne 'Windows') {
        $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Update)
        try {
            foreach ($entry in $zip.Entries) {
                $mode = if ($entry.FullName -match '(\.sh|\.command|/DungeonRunnersLauncher)$') { 493 } else { 420 }
                $entry.ExternalAttributes = (($mode -bor 32768) -shl 16)
            }
        } finally { $zip.Dispose() }
    }
    $destination = Join-Path $outputRoot $archiveName
    Move-Item -LiteralPath $archive -Destination $destination -Force
    Get-FileHash -LiteralPath $destination -Algorithm SHA256
} finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $prefix = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedStage) -notmatch '^\.package-[0-9a-f]{32}$') { throw 'Invalid staging directory.' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
