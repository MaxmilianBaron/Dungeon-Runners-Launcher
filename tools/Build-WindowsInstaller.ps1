param(
    [Parameter(Mandatory)][string]$ModernExecutable,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stage = Join-Path $projectRoot 'Temp/windows-legacy'
$downloads = Join-Path $stage 'downloads'
[IO.Directory]::CreateDirectory($downloads) | Out-Null
function Get-Pinned([string]$Url, [string]$Name, [string]$Hash) {
    $path = Join-Path $downloads $Name
    if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $Hash) {
        Write-Output "Downloading $Name" | Out-Host
        & curl.exe --fail --location --silent --show-error --proto '=https' --proto-redir '=https' --connect-timeout 30 --max-time 180 --retry 2 --retry-delay 3 --output $path $Url
        if ($LASTEXITCODE -ne 0) { throw "Dependency download failed: $Name" }
    }
    if ((Get-FileHash -LiteralPath $path).Hash -ne $Hash) { throw "Dependency verification failed: $Name" }
    return $path
}
$mbed = Get-Pinned 'https://github.com/Mbed-TLS/mbedtls/releases/download/mbedtls-3.6.7/mbedtls-3.6.7.tar.bz2' 'mbedtls.tar.bz2' 'a7e8bcbec0e6f761b4af24f25677626b35f762f68eef79c08677a363212d11f6'
$miniz = Get-Pinned 'https://github.com/richgel999/miniz/releases/download/3.1.2/miniz-3.1.2.zip' 'miniz.zip' 'f0446d863f9c19926ad9483c523fdc42e42b8d4a6a431d27e09d49c79a140d9a'
$null = Get-Pinned 'https://github.com/nlohmann/json/releases/download/v3.12.0/json.hpp' 'json.hpp' 'aaf127c04cb31c406e5b04a63f1ae89369fccde6d8fa7cdda1ed4f32dfc5de63'
$roots = Join-Path $projectRoot 'src/Client.Windows/cacert.pem'
if ((Get-FileHash -LiteralPath $roots).Hash -ne 'a41b5d356aea97a529fe27e0f7316d2f9d946d75927476cf9cf1b90637d00505') { throw 'Bundled certificate verification failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $stage 'mbedtls-3.6.7'))) {
    Write-Output 'Extracting Mbed TLS.'
    & python -m tarfile -e $mbed $stage
    if ($LASTEXITCODE -ne 0) { throw 'TLS dependency extraction failed.' }
}
if (-not (Test-Path -LiteralPath (Join-Path $stage 'miniz'))) {
    Write-Output 'Extracting miniz.'
    [IO.Compression.ZipFile]::ExtractToDirectory($miniz, (Join-Path $stage 'miniz'))
}
Write-Output 'Configuring the native toolchain.'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.v141.x86.x64 -property installationPath
if (-not $installation) { throw 'Install the Visual Studio v141 x86/x64 and Windows XP support components.' }
$compilerRoot = Join-Path $installation 'VC/Tools/MSVC/14.16.27023'
if (-not (Test-Path -LiteralPath (Join-Path $compilerRoot 'bin/Hostx64/x86/cl.exe'))) { throw 'Install the Visual Studio v141 x86/x64 and Windows XP support components.' }
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdk = @(Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Lib') -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um/x86/kernel32.lib') } | Sort-Object Name -Descending)[0].Name
$crt = '10.0.10240.0'
if (-not (Test-Path -LiteralPath (Join-Path $sdkRoot "Lib/$crt/ucrt/x86/libucrt.lib"))) { throw 'The Windows 10 10240 static CRT is required for XP targeting.' }
$env:PATH = (Join-Path $compilerRoot 'bin/Hostx64/x86') + ';' + (Join-Path $compilerRoot 'bin/Hostx64/x64') + ';' + (Join-Path $sdkRoot "bin/$sdk/x64") + ';' + $env:PATH
$env:INCLUDE = @((Join-Path $compilerRoot 'include'), (Join-Path $sdkRoot "Include/$crt/ucrt"), (Join-Path $sdkRoot "Include/$sdk/shared"), (Join-Path $sdkRoot "Include/$sdk/um")) -join ';'
$env:LIB = @((Join-Path $compilerRoot 'lib/x86'), (Join-Path $sdkRoot "Lib/$crt/ucrt/x86"), (Join-Path $sdkRoot "Lib/$sdk/um/x86")) -join ';'
$env:VSLANG = '1033'
$resourceCompiler = (Join-Path $sdkRoot "bin/$sdk/x64/rc.exe").Replace('\','/')
$modern = [IO.Path]::GetFullPath($ModernExecutable)
$notices = Join-Path $stage 'notices.txt'
Write-Output 'Exporting bundled notices.'
$noticeStart = [Diagnostics.ProcessStartInfo]::new($modern)
$noticeStart.UseShellExecute = $false
$noticeStart.CreateNoWindow = $true
$noticeStart.ArgumentList.Add('--licenses'); $noticeStart.ArgumentList.Add($notices)
$noticeProcess = [Diagnostics.Process]::Start($noticeStart)
try { if (-not $noticeProcess.WaitForExit(30000) -or $noticeProcess.ExitCode -ne 0) { throw 'Could not export bundled notices.' } } finally { $noticeProcess.Dispose() }
foreach ($file in @('mbedtls-3.6.7/LICENSE','miniz/LICENSE')) { [IO.File]::AppendAllText($notices, "`r`n" + [IO.File]::ReadAllText((Join-Path $stage $file))) }
[IO.File]::AppendAllText($notices, "`r`nJSON for Modern C++ 3.12.0, Copyright (c) 2013-2025 Niels Lohmann. MIT License (text above).`r`nMozilla CA certificate bundle 2026-09-25. Mozilla Public License 2.0: https://www.mozilla.org/MPL/2.0/`r`n")
foreach ($file in @('JSON-LICENSE.txt','MPL-2.0.txt')) { [IO.File]::AppendAllText($notices, "`r`n" + [IO.File]::ReadAllText((Join-Path $projectRoot "licenses/windows/$file"))) }
$source = Join-Path $projectRoot 'src/Client.Windows'
$fixture = Join-Path $stage 'contract.zip'
$identity = Join-Path $stage 'identity.txt'
[IO.File]::WriteAllText($identity, 'Dungeon-Runners-Windows-Bundle:1')
$memory = [IO.MemoryStream]::new()
$zip = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Create, $true)
$entry = $zip.CreateEntry('fmodex.dll', [IO.Compression.CompressionLevel]::Optimal)
$stream = $entry.Open()
try { $stream.Write([Text.Encoding]::ASCII.GetBytes('replacement')) } finally { $stream.Dispose() }
$zip.Dispose()
[IO.File]::WriteAllBytes($fixture, $memory.ToArray())
$memory.Dispose()
function Resource-Line([int]$Id, [string]$Type, [string]$Path) { return "$Id $Type " + '"' + ([IO.Path]::GetFullPath($Path).Replace('\','/')) + '"' }
$resources = @(
    '#include <windows.h>'
    (Resource-Line 1 'ICON' (Join-Path $projectRoot 'src/Client.Launcher/Assets/DungeonRunners.ico'))
    (Resource-Line 1 '24' (Join-Path $source 'app.manifest'))
    (Resource-Line 100 'RCDATA' $modern)
    (Resource-Line 101 'RCDATA' $roots)
    (Resource-Line 102 'RCDATA' (Join-Path $projectRoot 'src/Client.Core/client-compatibility.json'))
    (Resource-Line 103 'RCDATA' $notices)
    (Resource-Line 104 'RCDATA' $fixture)
    (Resource-Line 105 'RCDATA' $identity)
    (Resource-Line 201 'RCDATA' (Join-Path $projectRoot 'src/Client.Launcher/Assets/AdBackground.png'))
    (Resource-Line 202 'RCDATA' (Join-Path $projectRoot 'src/Client.Launcher/Assets/Load_01.png'))
    (Resource-Line 203 'RCDATA' (Join-Path $projectRoot 'src/Client.Launcher/Assets/AdFrame_DRLogo.png'))
)
$resourceFile = Join-Path $stage 'installer.rc'
[IO.File]::WriteAllLines($resourceFile, $resources)
$build = Join-Path $stage 'build'
$compilerRoot = $compilerRoot.Replace('\','/')
$stage = $stage.Replace('\','/')
$resourceFile = $resourceFile.Replace('\','/')
& cmake --fresh -S $source -B $build -G Ninja '-DCMAKE_BUILD_TYPE=Release' '-DCMAKE_POLICY_VERSION_MINIMUM=3.5' "-DCMAKE_C_COMPILER=$compilerRoot/bin/Hostx64/x86/cl.exe" "-DCMAKE_CXX_COMPILER=$compilerRoot/bin/Hostx64/x86/cl.exe" "-DCMAKE_RC_COMPILER=$resourceCompiler" "-DDEPENDENCIES=$stage" "-DRESOURCES=$resourceFile"
if ($LASTEXITCODE -ne 0) { throw 'Windows installer configuration failed.' }
& cmake --build $build --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'Windows installer build failed.' }
$destination = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'DungeonRunnersWindows.exe') -Destination $destination -Force
& (Join-Path $PSScriptRoot 'Test-WindowsImports.ps1') -Executable $destination
Write-Output $destination
