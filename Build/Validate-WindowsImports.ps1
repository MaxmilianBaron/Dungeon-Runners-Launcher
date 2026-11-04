param([Parameter(Mandatory)][string]$Executable)
$ErrorActionPreference = 'Stop'
$contract = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'windows-xp-imports.json') -Raw | ConvertFrom-Json -AsHashtable
$stream = [IO.File]::OpenRead([IO.Path]::GetFullPath($Executable))
$reader = [IO.BinaryReader]::new($stream)
function At([long]$Offset, [int]$Length) {
    if ($Offset -lt 0 -or $Offset + $Length -gt $stream.Length) { throw 'Invalid PE bounds.' }
    $stream.Position = $Offset
}
function U16([long]$Offset) { At $Offset 2; return $reader.ReadUInt16() }
function U32([long]$Offset) { At $Offset 4; return $reader.ReadUInt32() }
function Rva([uint32]$Value) {
    if ($Value -lt $headerSize) { return [long]$Value }
    foreach ($section in $sections) {
        if ($Value -ge $section.Address -and [long]$Value -lt [long]$section.Address + $section.Size) {
            return [long]$section.Offset + $Value - $section.Address
        }
    }
    throw 'PE address is outside its file sections.'
}
function TextAt([long]$Offset) {
    At $Offset 1
    $value = [Text.StringBuilder]::new()
    for ($i = 0; $i -lt 512; $i++) {
        $character = $reader.ReadByte()
        if ($character -eq 0) { return $value.ToString() }
        if ($character -lt 32 -or $character -gt 126) { throw 'Invalid PE import name.' }
        [void]$value.Append([char]$character)
    }
    throw 'PE import name is too long.'
}
try {
    if ((U16 0) -ne 0x5a4d) { throw 'Missing DOS header.' }
    $pe = U32 0x3c
    if ((U32 $pe) -ne 0x4550 -or (U16 ($pe + 4)) -ne 0x14c) { throw 'The Windows entry point must be an x86 PE.' }
    $optional = $pe + 24
    if ((U16 $optional) -ne 0x10b -or (U16 ($optional + 68)) -ne 2) { throw 'The Windows entry point must be a native PE32 GUI.' }
    foreach ($field in @(40, 48)) {
        if ((U16 ($optional + $field)) -ne 5 -or (U16 ($optional + $field + 2)) -gt 1) { throw 'The executable no longer targets Windows XP.' }
    }
    if ((U32 ($optional + 200)) -ne 0 -or (U32 ($optional + 208)) -ne 0) { throw 'Unexpected delay-loaded or managed entry-point dependency.' }
    $headerSize = U32 ($optional + 60)
    $sectionCount = U16 ($pe + 6)
    if ($sectionCount -lt 1 -or $sectionCount -gt 96) { throw 'Invalid PE section count.' }
    $table = $optional + (U16 ($pe + 20))
    $sections = @(for ($i = 0; $i -lt $sectionCount; $i++) {
        $entry = $table + $i * 40
        @{ Address = U32 ($entry + 12); Size = U32 ($entry + 16); Offset = U32 ($entry + 20) }
    })
    $imports = Rva (U32 ($optional + 104))
    $count = 0
    $modules = @()
    $ended = $false
    for ($i = 0; $i -lt 128; $i++) {
        $entry = $imports + $i * 20
        $name = U32 ($entry + 12)
        if ($name -eq 0) { $ended = $true; break }
        $moduleName = (TextAt (Rva $name)).ToLowerInvariant()
        if (-not $contract.imports.ContainsKey($moduleName)) { throw "Unreviewed Windows dependency: $moduleName" }
        $modules += $moduleName
        $lookup = U32 $entry
        if ($lookup -eq 0) { $lookup = U32 ($entry + 16) }
        $thunks = Rva $lookup
        $terminated = $false
        for ($j = 0; $j -lt 4096; $j++) {
            $thunk = U32 ($thunks + $j * 4)
            if ($thunk -eq 0) { $terminated = $true; break }
            $symbol = if ([long]$thunk -band 2147483648) { '#' + ($thunk -band 65535) } else { TextAt ((Rva $thunk) + 2) }
            if ($contract.imports[$moduleName] -cnotcontains $symbol) { throw "Import needs XP compatibility review: $moduleName!$symbol" }
            $count++
        }
        if (-not $terminated) { throw 'Unterminated PE import table.' }
    }
    if (-not $ended -or $count -eq 0) { throw 'Invalid PE import directory.' }
    Write-Output "PASS native x86 Windows 5.1 target; $count imports across $($modules.Count) system libraries match the XP contract."
} finally { $reader.Dispose(); $stream.Dispose() }
