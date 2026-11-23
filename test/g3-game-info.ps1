# Game info: makes the test folder, runs g3-game-info.txt and checks the
# log and the files. Fake emulators are copies of the harmless TestGame.exe under the shipped definitions'
# program names (shadPS4, Eden, yuzu, Cemu, mGBA); the user's own definition changes only shadPS4's package
# tool to a fake .cmd. Every game, package, key and .nsp here is made up: synthetic bytes, nothing real.
# The .nsp files are built below with this script's own AES-XTS / AES-CTR (on .NET's AES), so the launcher's
# reader is checked against an encoder written apart from it.
# Usage: g3-game-info.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\g3-game-info")
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$T = "$Root\T"; $data = "$Root\data"
foreach ($d in 'emus\shadPS4', 'emus\Eden\user\keys', 'emus\Eden\user\config', 'emus\yuzu', 'emus\Cemu', 'emus\mGBA', 'games\CUSA00900', 'games\Switch', 'games\WiiU\code', 'games\WiiU\meta', 'pkg', 'nsp', 'wiiu-dlc\meta', 'wiiu-dlc\content', 'recycle') {
    New-Item -ItemType Directory -Force (Join-Path $T $d) | Out-Null
}
New-Item -ItemType Directory -Force "$data\definitions" | Out-Null
Copy-Item $game "$T\emus\shadPS4\shadPS4.exe"
Copy-Item $game "$T\emus\Eden\eden.exe"
Copy-Item $game "$T\emus\yuzu\yuzu.exe"
Copy-Item $game "$T\emus\Cemu\Cemu.exe"
Copy-Item $game "$T\emus\mGBA\mGBA.exe"
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Put($p, $text) { New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; [IO.File]::WriteAllText($p, $text, $utf8) }

# ---- PS4: a param.sfo (utf-8 strings), a game with its update beside it and three add-ons ----
function Sfo($p, [hashtable]$kv) {
    $keys = @($kv.Keys | Sort-Object)
    $kt = New-Object IO.MemoryStream; $dt = New-Object IO.MemoryStream; $idx = New-Object IO.MemoryStream
    foreach ($k in $keys) {
        $v = $utf8.GetBytes([string]$kv[$k] + "`0"); $max = [Math]::Max(4, [int]([Math]::Ceiling($v.Length / 4) * 4))
        $e = [byte[]]::new(16)
        [BitConverter]::GetBytes([uint16]$kt.Length).CopyTo($e, 0); [BitConverter]::GetBytes([uint16]0x0204).CopyTo($e, 2)
        [BitConverter]::GetBytes([uint32]$v.Length).CopyTo($e, 4); [BitConverter]::GetBytes([uint32]$max).CopyTo($e, 8)
        [BitConverter]::GetBytes([uint32]$dt.Length).CopyTo($e, 12)
        $idx.Write($e, 0, 16)
        $kb = [Text.Encoding]::ASCII.GetBytes("$k`0"); $kt.Write($kb, 0, $kb.Length)
        $pad = [byte[]]::new($max); $v.CopyTo($pad, 0); $dt.Write($pad, 0, $max)
    }
    while ($kt.Length % 4) { $kt.WriteByte(0) }
    $h = [byte[]]::new(20)
    [byte[]](0, 0x50, 0x53, 0x46, 1, 1, 0, 0) | ForEach-Object -Begin { $i = 0 } -Process { $h[$i++] = $_ }
    [BitConverter]::GetBytes([uint32](20 + $idx.Length)).CopyTo($h, 8)
    [BitConverter]::GetBytes([uint32](20 + $idx.Length + $kt.Length)).CopyTo($h, 12)
    [BitConverter]::GetBytes([uint32]$keys.Count).CopyTo($h, 16)
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    [IO.File]::WriteAllBytes($p, [byte[]]($h + $idx.ToArray() + $kt.ToArray() + $dt.ToArray()))
}
$cid = 'UP9000-CUSA00900_00-BLOODBORNE0000NA'
[IO.File]::WriteAllBytes("$T\games\CUSA00900\eboot.bin", [byte[]]::new(4096))
Sfo "$T\games\CUSA00900\sce_sys\param.sfo" @{ TITLE = 'Bloodborne'; TITLE_ID = 'CUSA00900'; APP_VER = '01.00'; CONTENT_ID = $cid; CATEGORY = 'gd' }
Sfo "$T\games\CUSA00900-patch\sce_sys\param.sfo" @{ TITLE = 'Bloodborne'; TITLE_ID = 'CUSA00900'; APP_VER = '01.09'; CONTENT_ID = $cid; CATEGORY = 'gp' }
$ac = "$T\emus\shadPS4\user\addcont\CUSA00900"
Sfo "$ac\BBOLDHUNTERS0001\sce_sys\param.sfo" @{ TITLE = 'The Old Hunters'; TITLE_ID = 'CUSA00900'; CATEGORY = 'ac' }
Sfo "$ac\BBSOUNDTRACK0002\sce_sys\param.sfo" @{ TITLE = 'Soundtrack'; TITLE_ID = 'CUSA00900'; CATEGORY = 'ac' }
Sfo "$ac\BBTHEMEDYNAMIC03\sce_sys\param.sfo" @{ TITLE = 'Dynamic Theme'; TITLE_ID = 'CUSA00900'; CATEGORY = 'ac' }

# Fake packages: the PKG magic and a content id; <pkg>.kind is the fake extractor's answer to --check-type
# (101 game, 102 update, 103 add-on) and <pkg>.src is what it "unpacks".
function Pkg($name, $contentId, $kind) {
    $b = [byte[]]::new(0x1000); [byte[]](0x7F, 0x43, 0x4E, 0x54) | ForEach-Object -Begin { $i = 0 } -Process { $b[$i++] = $_ }
    [Text.Encoding]::ASCII.GetBytes($contentId).CopyTo($b, 0x40)
    [IO.File]::WriteAllBytes("$T\pkg\$name", $b); Put "$T\pkg\$name.kind" "$kind"
}
Pkg 'upd-0105.pkg' 'UP9000-CUSA00900_00-BLOODBORNE0000NA' 102
Sfo "$T\pkg\upd-0105.pkg.src\Image0\sce_sys\param.sfo" @{ TITLE = 'Bloodborne'; TITLE_ID = 'CUSA00900'; APP_VER = '01.05'; CATEGORY = 'gp' }
Pkg 'upd-0110.pkg' 'UP9000-CUSA00900_00-BLOODBORNE0000NA' 102
Sfo "$T\pkg\upd-0110.pkg.src\Image0\sce_sys\param.sfo" @{ TITLE = 'Bloodborne'; TITLE_ID = 'CUSA00900'; APP_VER = '01.10'; CATEGORY = 'gp' }
Put "$T\pkg\upd-0110.pkg.src\Image0\eboot.bin" 'patched'
Pkg 'dlc-chalice.pkg' 'UP9000-CUSA00900_00-BBCHALICEDUNGEON' 103
Sfo "$T\pkg\dlc-chalice.pkg.src\Image0\sce_sys\param.sfo" @{ TITLE = 'Chalice Set'; TITLE_ID = 'CUSA00900'; CATEGORY = 'ac' }
Pkg 'base.pkg' 'UP9000-CUSA00900_00-BLOODBORNE0000NA' 101
Pkg 'other.pkg' 'UP9000-CUSA00003_00-SOMETHINGELSE001' 102
[IO.File]::WriteAllBytes("$T\pkg\notapkg.pkg", [byte[]]::new(0x1000))
Put "$T\pkgtool.cmd" @"
@echo off
if "%~2"=="--check-type" (
  set /p K=<"%~1.kind"
  goto :kind
)
xcopy /e /i /q /y "%~1.src" "%~2" >nul
exit /b 0
:kind
exit /b %K%
"@
$def = [ordered]@{ id = 'shadps4'; gameData = @{ install = @{ tool = "$T\pkgtool.cmd" } } }
Put "$data\definitions\shadps4.json" ($def | ConvertTo-Json -Depth 6)

# ---- Switch: made-up keys and .nsp files (PFS0 with a meta NCA and a control NCA) ----
$aes = [Security.Cryptography.Aes]::Create()
$aes.Mode = [Security.Cryptography.CipherMode]::ECB; $aes.Padding = [Security.Cryptography.PaddingMode]::None
function EcbEnc([byte[]]$key, [byte[]]$d) { $e = $aes.CreateEncryptor($key, [byte[]]::new(16)); $o = $e.TransformFinalBlock($d, 0, $d.Length); $e.Dispose(); , $o }
function Hex([byte[]]$b) { ($b | ForEach-Object { $_.ToString('x2') }) -join '' }
function XtsEnc([byte[]]$d, [byte[]]$key) {
    $k1 = [byte[]]$key[0..15]; $k2 = [byte[]]$key[16..31]; $o = [byte[]]::new($d.Length)
    for ($sec = 0; $sec -lt $d.Length / 0x200; $sec++) {
        $tw = [byte[]]::new(16); for ($i = 0; $i -lt 8; $i++) { $tw[15 - $i] = [byte]((([long]$sec) -shr (8 * $i)) -band 0xFF) }
        $tw = EcbEnc $k2 $tw
        for ($blk = 0; $blk -lt 0x200; $blk += 16) {
            $at = $sec * 0x200 + $blk; $x = [byte[]]::new(16)
            for ($i = 0; $i -lt 16; $i++) { $x[$i] = $d[$at + $i] -bxor $tw[$i] }
            $c = EcbEnc $k1 $x
            for ($i = 0; $i -lt 16; $i++) { $o[$at + $i] = $c[$i] -bxor $tw[$i] }
            $carry = 0
            for ($i = 0; $i -lt 16; $i++) { $n = $tw[$i] -shr 7; $tw[$i] = [byte]((($tw[$i] -shl 1) -band 0xFF) -bor $carry); $carry = $n }
            if ($carry) { $tw[0] = $tw[0] -bxor 0x87 }
        }
    }
    , $o
}
function CtrEnc([byte[]]$d, [byte[]]$key, [byte[]]$upper, [long]$ncaOff) {
    $blocks = [int][Math]::Ceiling($d.Length / 16); $ctr = [byte[]]::new($blocks * 16)
    for ($b = 0; $b -lt $blocks; $b++) {
        [Array]::Copy($upper, 0, $ctr, $b * 16, 8); $n = [uint64]($ncaOff / 16 + $b)
        for ($j = 0; $j -lt 8; $j++) { $ctr[$b * 16 + 15 - $j] = [byte](($n -shr (8 * $j)) -band 0xFF) }
    }
    $ks = EcbEnc $key $ctr; $o = [byte[]]::new($d.Length)
    for ($i = 0; $i -lt $d.Length; $i++) { $o[$i] = $d[$i] -bxor $ks[$i] }
    , $o
}
function Pad([byte[]]$b, $to) { $n = [int]([Math]::Ceiling($b.Length / $to) * $to); if ($n -eq 0) { $n = $to }; $o = [byte[]]::new($n); $b.CopyTo($o, 0); , $o }
function Pfs0($files) {   # ordered name -> bytes
    $names = @($files.Keys); $st = New-Object IO.MemoryStream; $soff = @()
    foreach ($n in $names) { $soff += $st.Length; $nb = [Text.Encoding]::ASCII.GetBytes("$n`0"); $st.Write($nb, 0, $nb.Length) }
    while ($st.Length % 0x10) { $st.WriteByte(0) }
    $h = New-Object IO.MemoryStream
    $hd = [byte[]]::new(16); [Text.Encoding]::ASCII.GetBytes('PFS0').CopyTo($hd, 0)
    [BitConverter]::GetBytes([int]$names.Count).CopyTo($hd, 4); [BitConverter]::GetBytes([int]$st.Length).CopyTo($hd, 8); $h.Write($hd, 0, 16)
    $off = 0L
    for ($i = 0; $i -lt $names.Count; $i++) {
        $e = [byte[]]::new(0x18); $len = $files[$names[$i]].Length
        [BitConverter]::GetBytes([uint64]$off).CopyTo($e, 0); [BitConverter]::GetBytes([uint64]$len).CopyTo($e, 8); [BitConverter]::GetBytes([uint32]$soff[$i]).CopyTo($e, 0x10)
        $h.Write($e, 0, 0x18); $off += $len
    }
    $sb = $st.ToArray(); $h.Write($sb, 0, $sb.Length)
    foreach ($n in $names) { $fb = $files[$n]; $h.Write($fb, 0, $fb.Length) }
    , $h.ToArray()
}
$hk = [byte[]](1..32); $kak = [byte[]](0x40..0x4F); $ctrKey = [byte[]](0x70..0x7F)
# One NCA: header (content type, section 0 at 0xC00, CTR, key area with the CTR key third), section bytes.
function Nca([int]$contentType, [byte[]]$section, [byte[]]$fsExtra) {
    $sec = Pad $section 0x200
    $h = [byte[]]::new(0xC00)
    [Text.Encoding]::ASCII.GetBytes('NCA3').CopyTo($h, 0x200); $h[0x205] = $contentType
    [BitConverter]::GetBytes([uint64](0xC00 + $sec.Length)).CopyTo($h, 0x208)
    [BitConverter]::GetBytes([uint32](0xC00 / 0x200)).CopyTo($h, 0x240); [BitConverter]::GetBytes([uint32]((0xC00 + $sec.Length) / 0x200)).CopyTo($h, 0x244)
    $area = [byte[]]::new(0x40); $ctrKey.CopyTo($area, 0x20); (EcbEnc $kak $area).CopyTo($h, 0x300)
    $fs = [byte[]]::new(0x200); $fs[0] = 2; $fs[4] = 3
    $fsExtra.CopyTo($fs, 0)
    $fs[4] = 3
    $up = [byte[]](0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88); for ($j = 0; $j -lt 8; $j++) { $fs[0x147 - $j] = $up[$j] }
    $fs.CopyTo($h, 0x400)
    $enc = CtrEnc $sec $ctrKey $up 0xC00
    , [byte[]]((XtsEnc $h $hk) + $enc)
}
function MetaNca([uint64]$id, [uint32]$ver, [byte]$type, [uint64]$app, [byte[]]$controlId) {
    $c = [byte[]]::new(0x20 + 0x10 + 0x38)
    [BitConverter]::GetBytes($id).CopyTo($c, 0); [BitConverter]::GetBytes($ver).CopyTo($c, 8); $c[0xC] = $type
    [BitConverter]::GetBytes([uint16]0x10).CopyTo($c, 0xE); [BitConverter]::GetBytes([uint16]$(if ($controlId) { 1 } else { 0 })).CopyTo($c, 0x10)
    [BitConverter]::GetBytes($app).CopyTo($c, 0x20)
    if ($controlId) { $controlId.CopyTo($c, 0x30 + 0x20); $c[0x30 + 0x36] = 3 }
    $pfs = Pfs0 ([ordered]@{ ('{0}_{1:x16}.cnmt' -f $(switch ($type) { 0x80 { 'Application' } 0x81 { 'Patch' } default { 'AddOnContent' } }), $id) = $c })
    $fs = [byte[]]::new(0x200); [BitConverter]::GetBytes([uint64]0x200).CopyTo($fs, 0x38)   # the PFS0 after a 0x200 "hash table"
    , (Nca 1 ([byte[]]([byte[]]::new(0x200) + $pfs)) $fs)
}
function ControlNca([string]$display, [string]$name) {
    $nacp = [byte[]]::new(0x4000); $utf8.GetBytes($name).CopyTo($nacp, 0); $utf8.GetBytes($display).CopyTo($nacp, 0x3060)
    $rom = [byte[]]::new(0x200 + 0x4000)
    [BitConverter]::GetBytes([uint64]0x50).CopyTo($rom, 0x38); [BitConverter]::GetBytes([uint64]0x30).CopyTo($rom, 0x40); [BitConverter]::GetBytes([uint64]0x200).CopyTo($rom, 0x48)
    [BitConverter]::GetBytes([uint32]12).CopyTo($rom, 0x50 + 0x1C); [Text.Encoding]::ASCII.GetBytes('control.nacp').CopyTo($rom, 0x50 + 0x20)
    $nacp.CopyTo($rom, 0x200)
    $fs = [byte[]]::new(0x200); [Text.Encoding]::ASCII.GetBytes('IVFC').CopyTo($fs, 8); [BitConverter]::GetBytes([uint64]0x400).CopyTo($fs, 0x88)
    , (Nca 2 ([byte[]]([byte[]]::new(0x400) + $rom)) $fs)
}
function Nsp($path, [uint64]$id, [uint32]$ver, [byte]$type, [uint64]$app, $display, $name) {
    $files = [ordered]@{}
    $cidBytes = $null
    if ($display) { $cidBytes = [byte[]](0..15 | ForEach-Object { [byte](($_ * 7 + $type) -band 0xFF) }); $files[(Hex $cidBytes) + '.nca'] = ControlNca $display $name }
    $files[('{0:x32}.cnmt.nca' -f $id)] = MetaNca $id $ver $type $app $cidBytes
    [IO.File]::WriteAllBytes($path, (Pfs0 $files))
}
$gid = [Convert]::ToUInt64('0100ABCD00010000', 16)
Nsp "$T\games\Switch\Test Quest [0100ABCD00010000][v0].nsp" $gid 0 0x80 ([Convert]::ToUInt64('0100ABCD00010800', 16)) '1.0.0' 'Test Quest'
Nsp "$T\nsp\Test Quest [0100ABCD00010800][v196608].nsp" ([Convert]::ToUInt64('0100ABCD00010800', 16)) 196608 0x81 $gid '1.0.3' 'Test Quest'
Nsp "$T\nsp\Test Quest [0100ABCD00010800][v65536].nsp" ([Convert]::ToUInt64('0100ABCD00010800', 16)) 65536 0x81 $gid '1.0.1' 'Test Quest'
Nsp "$T\nsp\Test Quest - Bonus Pack [0100ABCD00011001][v0].nsp" ([Convert]::ToUInt64('0100ABCD00011001', 16)) 0 0x82 $gid $null $null
Nsp "$T\nsp\Other Game [0100FFFF00010800][v65536].nsp" ([Convert]::ToUInt64('0100FFFF00010800', 16)) 65536 0x81 ([Convert]::ToUInt64('0100FFFF00010000', 16)) '2.0.0' 'Other'
Copy-Item -LiteralPath "$T\games\Switch\Test Quest [0100ABCD00010000][v0].nsp" "$T\nsp\Test Quest base copy.nsp"
Put "$T\emus\Eden\user\keys\prod.keys" "header_key = $(Hex $hk)`nkey_area_key_application_00 = $(Hex $kak)`n"
# Eden's own settings with three game folders, as the real file has them; other sections stay as they are.
$qt = @"
[Controls]
player_0_button_a="engine:keyboard,code:67,toggle:0"

[UI]
check_for_updates\default=false
check_for_updates=false
Paths\gamedirs\1\deep_scan\default=true
Paths\gamedirs\1\deep_scan=false
Paths\gamedirs\1\expanded\default=true
Paths\gamedirs\1\expanded=true
Paths\gamedirs\1\path=SDMC
Paths\gamedirs\2\deep_scan\default=true
Paths\gamedirs\2\deep_scan=false
Paths\gamedirs\2\expanded\default=true
Paths\gamedirs\2\expanded=true
Paths\gamedirs\2\path=UserNAND
Paths\gamedirs\3\deep_scan\default=true
Paths\gamedirs\3\deep_scan=false
Paths\gamedirs\3\expanded\default=true
Paths\gamedirs\3\expanded=true
Paths\gamedirs\3\path=SysNAND
Paths\gamedirs\size=3
confirmStop\default=false

[Renderer]
backend=1

[DisabledAddOns]
size=2
1\title_id\default=false
1\title_id=$([Convert]::ToUInt64('0100FFFF00010000', 16))
1\disabled\1\d=Old Mod
1\disabled\size=1
2\title_id\default=false
2\title_id=$([Convert]::ToUInt64('0100ABCD00010000', 16))
2\disabled\1\d=Old Mod
2\disabled\size=1
"@
Put "$T\emus\Eden\user\config\qt-config.ini" ($qt -replace "`r`n", "`n")
# Eden's mods for Test Quest (one folder each, Old Mod turned off above) and one for another game
Put "$T\emus\Eden\user\load\0100ABCD00010000\60 FPS\romfs\dfps\60fps.ini" "[dFPS]`nMaxFramerate = 60`n"
Put "$T\emus\Eden\user\load\0100ABCD00010000\Old Mod\exefs\main.npdm" 'made-up'
Put "$T\emus\Eden\user\load\0100FFFF00010000\Other Mod\exefs\main.npdm" 'made-up'

# ---- Wii U: a game folder (meta.xml), an update in Cemu's title folder, an add-on folder to add ----
$meta = { param($id, $v, $name) "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<menu type=`"complex`" access=`"777`">`n  <title_id type=`"hexBinary`" length=`"8`">$id</title_id>`n  <title_version type=`"unsignedInt`" length=`"4`">$v</title_version>`n  <region type=`"hexBinary`" length=`"4`">00000002</region>`n  <longname_en type=`"string`" length=`"512`">$name</longname_en>`n</menu>`n" }
Put "$T\games\WiiU\meta\meta.xml" (& $meta '00050000101C9500' 0 'Test Kart')
[IO.File]::WriteAllBytes("$T\games\WiiU\code\game.rpx", [byte[]]::new(2048))
Put "$T\emus\Cemu\mlc01\usr\title\0005000e\101c9500\meta\meta.xml" (& $meta '0005000E101C9500' 208 'Test Kart')
Put "$T\wiiu-dlc\meta\meta.xml" (& $meta '0005000C101C9500' 112 'Test Kart')
Put "$T\wiiu-dlc\content\0010\dlc.bin" 'made-up add-on'

# ---- GBA: a header with the fixed byte, a game code and revision 0 ----
$gba = [byte[]]::new(0x200); [Text.Encoding]::ASCII.GetBytes('TESTADVENTUR').CopyTo($gba, 0xA0); [Text.Encoding]::ASCII.GetBytes('BTQE').CopyTo($gba, 0xAC); $gba[0xB2] = 0x96
[IO.File]::WriteAllBytes("$T\games\Test Adventure.gba", $gba)

# ---- a cover image ----
Add-Type -AssemblyName System.Drawing
$bmp = New-Object Drawing.Bitmap 320, 240; $gr = [Drawing.Graphics]::FromImage($bmp); $gr.Clear([Drawing.Color]::FromArgb(255, 40, 120, 200)); $gr.Dispose()
$bmp.Save("$T\cover.png", [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()

# Run (nothing takes the foreground; file pickers are never opened: addonpick / coverpick stand for a pick).
$env:IONEA_TESTROOT = $T; $env:IONEA_TEST_RECYCLE = "$T\recycle"; $env:IONEA_TESTGAME_SECS = $null
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
$a = "--data `"$data`" --no-import --emulators `"$T\emus`" --windowed --mute --size 1040x620 --pos -30000,-30000 --shots `"$data\shots`" --script `"$PSScriptRoot\g3-game-info.txt`""
$p = Start-Process $Exe -ArgumentList $a -PassThru
if (-not $p.WaitForExit(240000)) { "launcher still running; stopping it"; $p.Kill(); $fails++ }
$env:IONEA_TEST_RECYCLE = $null
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|game info:' } | Out-Host
$content = "$data\game-content\switch\0100ABCD00010000"
$ini = Get-Content -Raw "$T\emus\Eden\user\config\qt-config.ini"
Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) 'script 0 failed'
Check ((Test-Path "$T\games\CUSA00900-patch\eboot.bin") -and (Get-Content "$T\games\CUSA00900-patch\eboot.bin") -eq 'patched') 'PS4 update 01.10 in the -patch folder'
Check (Test-Path "$T\recycle\CUSA00900-patch\sce_sys\param.sfo") 'old PS4 update to the Recycle Bin'
Check (Test-Path "$ac\BBCHALICEDUNGEON\sce_sys\param.sfo") 'PS4 add-on in addcont under its label'
Check ((Test-Path "$T\pkg\upd-0110.pkg") -and (Test-Path "$T\pkg\dlc-chalice.pkg")) 'the user''s packages kept'
Check (@(Get-ChildItem "$data\work" -Directory -ErrorAction SilentlyContinue).Count -eq 0) 'unpack folders removed'
Check (Test-Path -LiteralPath "$content\Test Quest [0100ABCD00010800][v196608].nsp") 'Switch update kept in Ionea''s folder for the game'
Check (Test-Path -LiteralPath "$content\Test Quest - Bonus Pack [0100ABCD00011001][v0].nsp") 'Switch add-on kept'
Check (@(Get-ChildItem $content -Filter *.nsp -ErrorAction SilentlyContinue).Count -eq 2) 'only those two (refused ones not copied)'
Check (Test-Path -LiteralPath "$T\nsp\Test Quest [0100ABCD00010800][v196608].nsp") 'the user''s .nsp kept'
$want = 'Paths\gamedirs\4\path=' + ($content -replace '\\', '/')
Check ($ini.Contains($want + "`n")) 'Eden: game folder 4 is Ionea''s folder'
Check ($ini.Contains("Paths\gamedirs\size=4`n") -and -not $ini.Contains('size=3')) 'Eden: game folder count 4'
Check (([regex]::Matches($ini, [regex]::Escape($want))).Count -eq 1) 'Eden: added once'
Check ($ini.Contains("[Controls]`nplayer_0_button_a=") -and $ini.Contains("[Renderer]`nbackend=1")) 'Eden: other settings untouched'
Check (@(Get-ChildItem "$T\emus\Eden\user\config" -Filter 'qt-config.ini.bak-ionea-*').Count -eq 1) 'Eden: settings backed up once'
Check (-not (Test-Path "$T\emus\Eden\user\nand")) 'Eden: NAND not touched'
Check (Test-Path "$T\emus\Cemu\mlc01\usr\title\0005000c\101c9500\content\0010\dlc.bin") 'Wii U add-on copied to Cemu''s title folder'
Check (@(Get-ChildItem "$data\covers" -Filter '*.png' -ErrorAction SilentlyContinue).Count -ge 1 -and ((Get-Content -Raw "$data\library.json") -match '"Cover":\s*"[^"]+-\d{14}(\d{3})?\.png"')) 'cover saved and set'
"g3: $fails failed"
