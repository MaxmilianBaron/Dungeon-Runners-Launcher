param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/display-check'))
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$fixture = Join-Path $output $(if ($IsWindows) { 'fixture.exe' } else { 'fixture' })
$source = Join-Path $PSScriptRoot 'Client.DisplayCheck/window.c'
if ($IsWindows) {
    $vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.v141.x86.x64 -property installationPath
    if (-not $installation) { throw 'The native Windows toolchain is required.' }
    $compiler = Join-Path $installation 'VC/Tools/MSVC/14.16.27023'
    $env:PATH = (Join-Path $compiler 'bin/Hostx64/x86') + ';' + (Join-Path $compiler 'bin/Hostx64/x64') + ';' + $env:PATH
    $sdkRoot = "${env:ProgramFiles(x86)}/Windows Kits/10"
    $sdk = @(Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Lib') -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um/x86/kernel32.lib') } | Sort-Object Name -Descending)[0].Name
    $env:INCLUDE = @((Join-Path $compiler 'include'),(Join-Path $sdkRoot 'Include/10.0.10240.0/ucrt'),(Join-Path $sdkRoot "Include/$sdk/shared"),(Join-Path $sdkRoot "Include/$sdk/um")) -join ';'
    $env:LIB = @((Join-Path $compiler 'lib/x86'),(Join-Path $sdkRoot 'Lib/10.0.10240.0/ucrt/x86'),(Join-Path $sdkRoot "Lib/$sdk/um/x86")) -join ';'
    & (Join-Path $compiler 'bin/Hostx64/x86/cl.exe') /nologo /MT /utf-8 /D_CRT_SECURE_NO_WARNINGS $source "/Fo$output/fixture.obj" "/Fe$fixture" user32.lib
} else {
    & gcc $source -lX11 -o $fixture
}
if ($LASTEXITCODE -ne 0) { throw 'The window fixture did not build.' }
& dotnet run --project (Join-Path $PSScriptRoot 'Client.DisplayCheck') -c Release -- $fixture
if ($LASTEXITCODE -ne 0) { throw 'Game monitor placement failed.' }
