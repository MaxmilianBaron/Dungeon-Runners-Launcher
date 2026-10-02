param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts')
)

$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$names = @('Dungeon-Runners-Launcher-Linux.AppImage', 'Dungeon-Runners-Launcher-Linux-arm64.AppImage')
$tokens = @('X64', 'ARM64')
$template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'linux/Installer.run.in')).Replace("`r`n", "`n").TrimEnd("`n") + "`n"
for ($index = 0; $index -lt $names.Count; $index++) {
    $path = Join-Path $outputRoot $names[$index]
    $file = Get-Item -LiteralPath $path
    if ($file.Length -lt 1024 -or $file.Length -gt 512MB) { throw "Unexpected AppImage size: $($file.Name)" }
    $template = $template.Replace("@$($tokens[$index])_SIZE@", $file.Length.ToString([Globalization.CultureInfo]::InvariantCulture))
    $template = $template.Replace("@$($tokens[$index])_SHA256@", (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())
}
$payloadLine = ($template.ToCharArray() | Where-Object { $_ -eq "`n" } | Measure-Object).Count + 1
$template = $template.Replace('@PAYLOAD_LINE@', $payloadLine.ToString([Globalization.CultureInfo]::InvariantCulture))
if ($template -match '@[A-Z0-9_]+@') { throw 'Unresolved installer template token.' }
$destination = Join-Path $outputRoot 'Dungeon-Runners-Launcher-Linux.run'
$temporary = Join-Path $outputRoot ('.linux-installer-' + [Guid]::NewGuid().ToString('N'))
try {
    $stream = [IO.File]::Create($temporary)
    try {
        $header = [Text.UTF8Encoding]::new($false).GetBytes($template)
        $stream.Write($header, 0, $header.Length)
        $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionLevel]::Optimal, $true)
        try {
            $tar = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Ustar, $true)
            try {
                foreach ($name in $names) {
                    $entry = [System.Formats.Tar.UstarTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $name)
                    $entry.ModificationTime = [DateTimeOffset]::UnixEpoch
                    $entry.Mode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute
                    $inputStream = [IO.File]::OpenRead((Join-Path $outputRoot $name))
                    try { $entry.DataStream = $inputStream; $tar.WriteEntry($entry) } finally { $inputStream.Dispose() }
                }
            } finally { $tar.Dispose() }
        } finally { $gzip.Dispose() }
    } finally { $stream.Dispose() }
    if ($IsLinux) {
        & chmod 755 $temporary
        if ($LASTEXITCODE -ne 0) { throw 'Installer permissions failed.' }
    }
    Move-Item -LiteralPath $temporary -Destination $destination -Force
    Get-FileHash -LiteralPath $destination -Algorithm SHA256
} finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
}
