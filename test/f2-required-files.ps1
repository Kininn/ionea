# Required files: makes the test folder, runs f2-required-files.txt and
# checks the log and the files. Fake emulators are copies of the harmless TestGame.exe ("Pup Emu" is a copy of
# cmd.exe, so its "install" really runs and copies the fake .PUP into place). Every key, firmware and BIOS file
# here is made up: synthetic text and bytes with hashes worked out below; nothing real is used or read.
# Usage: f2-required-files.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\f2-required-files")
$ErrorActionPreference = 'Stop'
$game = Join-Path $PSScriptRoot 'bin\TestGame.exe'
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$T = "$Root\T"; $data = "$Root\data"
foreach ($d in 'emus\SwitA', 'emus\SwitB', 'emus\Own\user\keys', 'emus\Psx', 'emus\Pup', 'emus\Cores', 'bad', 'good\inner', 'good2', 'fw', 'psxdir\sub') {
    New-Item -ItemType Directory -Force (Join-Path $T $d) | Out-Null
}
New-Item -ItemType Directory -Force "$data\definitions" | Out-Null
Copy-Item $game "$T\emus\SwitA\swita.exe"
Copy-Item $game "$T\emus\SwitB\switb.exe"
Copy-Item $game "$T\emus\Own\own.exe"
Copy-Item $game "$T\emus\Psx\psx.exe"
Copy-Item "$env:SystemRoot\System32\cmd.exe" "$T\emus\Pup\pup.exe"
Set-Content -LiteralPath "$T\emus\Cores\melondsds_libretro.dll" 'not a real core'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Put($p, $text) { [IO.File]::WriteAllText($p, $text, $utf8) }
function Md5($p) { (Get-FileHash -Algorithm MD5 -LiteralPath $p).Hash.ToLowerInvariant() }

# Made-up files: right ones and wrong ones.
$hex = 'ab' * 16
Put "$T\good\inner\prod.keys" "header_key = $($hex * 2)`nmaster_key_00 = $hex`nkey_area_key_application_00 = $hex`n"
Put "$T\good2\prod.keys" "header_key = $('cd' * 32)`nmaster_key_00 = $('cd' * 16)`n"
Put "$T\good\boot.bin" ('IONEA GOOD BOOT ' * 64)
Put "$T\bad\prod.keys" "hello, these are not keys`n"
Put "$T\bad\boot.bin" ('IONEA BAD BOOT ' * 64)
for ($i = 1; $i -le 25; $i++) { Put ("$T\fw\{0:x2}.nca" -f $i) "fake nca $i" }
Put "$T\psxdir\sub\scph-test.bin" ('IONEA PSX BIOS ' * 64)
$pup = [byte[]]::new(64); [Text.Encoding]::ASCII.GetBytes('SCEUF').CopyTo($pup, 0); [IO.File]::WriteAllBytes("$T\PS3UPDAT.PUP", $pup)
Put "$T\emus\Own\user\keys\prod.keys" "hello, the user's own file`n"   # in place already: Ready without a check
$boot = Md5 "$T\good\boot.bin"; $psx = Md5 "$T\psxdir\sub\scph-test.bin"
$tar = "$env:SystemRoot\System32\tar.exe"
Push-Location "$T\good"; & $tar -a -cf "$T\good.zip" boot.bin inner; Pop-Location

# The user's own definitions.
function Def($name, $o) { Put "$data\definitions\$name.json" ($o | ConvertTo-Json -Depth 8) }
Def swita ([ordered]@{ id = 'swita'; name = 'Swit A'; detect = @{ programs = @('swita.exe') }; start = @{ args = '"{rom}"' }; systems = @('switch')
    requiredFiles = @(
        [ordered]@{ name = 'prod.keys'; path = 'user\keys\prod.keys'; check = 'switch-keys'; share = 'switch-keys' },
        [ordered]@{ name = 'title.keys'; path = 'user\keys\title.keys'; check = 'switch-titlekeys'; share = 'switch-titlekeys'; optional = $true },
        [ordered]@{ name = 'Firmware'; path = 'user\nand\system\Contents\registered\'; check = 'switch-firmware'; share = 'switch-firmware' },
        [ordered]@{ name = 'boot.bin'; path = 'sys\boot.bin'; md5 = $boot }) })
Def switb ([ordered]@{ id = 'switb'; name = 'Swit B'; family = 'swita'; detect = @{ programs = @('switb.exe') } })   # its files come from the family
Def own ([ordered]@{ id = 'own'; name = 'Own Emu'; detect = @{ programs = @('own.exe') }; start = @{ args = '"{rom}"' }; systems = @('switch')
    requiredFiles = @([ordered]@{ name = 'prod.keys'; path = 'user\keys\prod.keys'; check = 'switch-keys'; share = 'switch-keys' },
        [ordered]@{ name = 'Escape'; path = '..\..\escape.bin' },            # outside its folder: skipped
        [ordered]@{ name = 'Odd'; path = 'odd.bin'; check = 'no-such-check' }) })   # a check Ionea doesn't have: skipped
Def psx ([ordered]@{ id = 'psx'; name = 'Psx Emu'; detect = @{ programs = @('psx.exe') }; start = @{ args = '"{rom}"' }; systems = @('psx')
    requiredFiles = @([ordered]@{ name = 'PS1 BIOS'; path = 'bios\'; md5s = @('00000000000000000000000000000000', $psx.ToUpperInvariant()) }) })
Def pup ([ordered]@{ id = 'pup'; name = 'Pup Emu'; detect = @{ programs = @('pup.exe') }; start = @{ args = '"{rom}"' }; systems = @('ps3')
    requiredFiles = @([ordered]@{ name = 'Firmware (.PUP)'; ready = 'dev_flash\vsh\module\vsh.self'; check = 'ps3-pup'; share = 'ps3-firmware'
        install = '/c mkdir dev_flash\vsh\module & copy /y "{file}" dev_flash\vsh\module\vsh.self' }) })

# Run (nothing takes the foreground; the file dialogs are never opened: reqpick stands for a pick).
$env:IONEA_TESTROOT = $T; $env:IONEA_TESTGAME_SECS = $null
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
$a = "--data `"$data`" --no-import --emulators `"$T\emus`" --windowed --mute --files-prompt --size 1040x620 --pos -30000,-30000 --shots `"$data\shots`" --script `"$PSScriptRoot\f2-required-files.txt`""
$p = Start-Process $Exe -ArgumentList $a -PassThru
if (-not $p.WaitForExit(180000)) { "launcher still running; stopping it"; $p.Kill(); $fails++ }
$log = Get-Content "$data\launcher.log"
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|required files:' } | Out-Host
Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) 'script 0 failed'
Check (($log -match 'required files: refused .*bad\\prod\.keys for Swit A').Count -eq 1) 'wrong keys file refused (logged)'
Check (($log -match 'required files: refused .*bad\\boot\.bin for Swit A: This boot\.bin is not a known good copy').Count -eq 1) 'wrong hash refused (logged)'
Check (($log -match 'required files: prod\.keys copied to Swit B').Count -eq 1) 'keys supplied to the second emulator'
Check (($log -match 'required files: own: \S*escape\.bin is outside the emulator''s folder').Count -eq 1) 'path outside the folder skipped (logged once)'
Check (($log -match 'required files: own: unknown check no-such-check').Count -eq 1) 'unknown check skipped (logged once)'
Check (-not (Test-Path "$Root\escape.bin") -and -not (Test-Path "$T\escape.bin")) 'nothing written outside'
Check (($log -match 'copied to Own Emu').Count -eq 0) 'nothing copied over the user''s own file'
Check ((Get-Content "$T\emus\Own\user\keys\prod.keys") -eq "hello, the user's own file") 'own file unchanged'
Check ((Get-Content -Raw "$T\emus\SwitA\user\keys\prod.keys") -eq (Get-Content -Raw "$T\good2\prod.keys")) 'row pick replaced prod.keys'
Check (@(Get-ChildItem "$T\emus\SwitA\user\keys" -Filter 'prod.keys.bak-ionea-*').Count -eq 1) 'replaced copy kept as .bak-ionea'
Check ((Md5 "$data\files\md5-$boot\boot.bin") -eq $boot) 'boot.bin kept once in the store'
Check (@(Get-ChildItem "$data\files\switch-firmware" -Filter *.nca).Count -eq 25) 'firmware kept once in the store'
Check (@(Get-ChildItem "$T\emus\SwitB\user\nand\system\Contents\registered" -Filter *.nca).Count -eq 25) 'firmware supplied to Swit B'
Check (Test-Path "$T\emus\Psx\bios\scph-test.bin") 'PS1 BIOS copied under its own name'
Check (Test-Path "$T\emus\Pup\dev_flash\vsh\module\vsh.self") 'Pup Emu installed its firmware'
Check (@(Get-ChildItem $env:TEMP -Directory -Filter 'ionea-files-*' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-5) }).Count -eq 0) 'zip stage removed'
Check ((Get-Content -Raw "$data\settings.json") -match '"FilesAsked":\s*true') 'first-start screen remembered'

# Second run on the same data: the first-start screen is not shown again, a fresh emulator gets the store's
# files at startup, and a System.dat in the data's definitions adds hashes.
New-Item -ItemType Directory -Force "$T\emus\SwitC" | Out-Null
Copy-Item $game "$T\emus\SwitC\switc.exe"
Def switc ([ordered]@{ id = 'switc'; name = 'Swit C'; family = 'swita'; detect = @{ programs = @('switc.exe') } })
# ... and the user's own System.dat (made up here): its hashes count, its systems and names are checked.
New-Item -ItemType Directory -Force "$data\definitions\more" | Out-Null
foreach ($d in 'emus\DatPsx', 'emus\DatPs2', 'emus\DatRom', 'dat\deep', 'dat2') { New-Item -ItemType Directory -Force "$T\$d" | Out-Null }
Copy-Item $game "$T\emus\DatPsx\datpsx.exe"; Copy-Item $game "$T\emus\DatPs2\datps2.exe"; Copy-Item $game "$T\emus\DatRom\datrom.exe"
Put "$T\dat\deep\scph-dat.bin" ('IONEA DAT PSX ' * 64)
Put "$T\dat2\scph-other.bin" ('IONEA NOT IN DAT ' * 64)
function Ps2($p, [byte]$last) { $b = [byte[]]::new(4MB); [Text.Encoding]::ASCII.GetBytes("RESET`0").CopyTo($b, 0); [Text.Encoding]::ASCII.GetBytes('ROMDIR').CopyTo($b, 16); $b[$b.Length - 1] = $last; [IO.File]::WriteAllBytes($p, $b) }
Ps2 "$T\dat\ps2-good.bin" 1; Ps2 "$T\dat2\ps2-other.bin" 2
Put "$T\dat\rom.bin" ('IONEA DAT ROM ' * 64)
Put "$T\dat2\named.bin" ('IONEA WRONG NAMED ' * 64)
$m1 = Md5 "$T\dat\deep\scph-dat.bin"; $m2 = Md5 "$T\dat\ps2-good.bin"; $m3 = Md5 "$T\dat\rom.bin"
Put "$data\definitions\more\System.dat" @"
clrmamepro (
`tname "System"
`tdescription "Ionea test, made up"
)

game (
`tname "Ionea Test - Disc"
`trom ( name "disc/scph-dat.bin" size 896 crc 00000000 md5 $($m1.ToUpperInvariant()) sha1 0000 )
)

game (
`tname "Ionea Test - Two"
`trom ( name ps2-good.bin size 4194304 md5 $m2 )
)

game (
`tname "Ionea Test - Misc"
`trom ( name rom.bin md5 $m3 )
`trom ( name named.bin md5 0123456789abcdef0123456789abcdef )
)
"@
Def datpsx ([ordered]@{ id = 'datpsx'; name = 'Dat Psx'; detect = @{ programs = @('datpsx.exe') }; start = @{ args = '"{rom}"' }; systems = @('psx')
    requiredFiles = @([ordered]@{ name = 'BIOS'; path = 'bios\'; md5s = @('00000000000000000000000000000000'); dat = 'Ionea Test - Disc' }) })
Def datps2 ([ordered]@{ id = 'datps2'; name = 'Dat Ps2'; detect = @{ programs = @('datps2.exe') }; start = @{ args = '"{rom}"' }; systems = @('ps2')
    requiredFiles = @([ordered]@{ name = 'PS2 BIOS'; path = 'bios\'; check = 'ps2-bios'; dat = 'Ionea Test - Two' }) })
Def datrom ([ordered]@{ id = 'datrom'; name = 'Dat Rom'; detect = @{ programs = @('datrom.exe') }; start = @{ args = '"{rom}"' }; systems = @('psx')
    requiredFiles = @([ordered]@{ name = 'rom.bin'; path = 'rom.bin'; md5 = '00000000000000000000000000000000' },
        [ordered]@{ name = 'named.bin'; path = 'named.bin' }) })
$n1 = @(Get-Content "$data\launcher.log").Count
$p = Start-Process $Exe -ArgumentList ($a -replace 'f2-required-files\.txt', 'f2-required-files-again.txt') -PassThru
if (-not $p.WaitForExit(120000)) { "launcher still running; stopping it"; $p.Kill(); $fails++ }
$log = @(Get-Content "$data\launcher.log" | Select-Object -Skip $n1)
$log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|required files:' } | Out-Host
Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) 'second run: script 0 failed'
Check (($log -match 'required files: (prod\.keys|Firmware|boot\.bin) copied to Swit C').Count -eq 3) 'second run: Swit C supplied from the store at startup'
Check (($log -match 'required files: System\.dat .*more\\System\.dat: 4 files for 3 systems').Count -eq 1) 'System.dat read (logged)'
Check (($log -match 'refused .*dat2 for Dat Psx: That is not BIOS').Count -eq 1) 'file not in System.dat refused'
Check (($log -match 'refused .*ps2-other\.bin for Dat Ps2').Count -eq 1) 'valid-looking PS2 BIOS not in System.dat refused'
Check (($log -match 'refused .*named\.bin for Dat Rom').Count -eq 1) 'listed name with another hash refused'
Check (Test-Path "$T\emus\DatPsx\bios\scph-dat.bin") 'System.dat file found deep in the folder and copied'
Check (Test-Path "$T\emus\DatPs2\bios\ps2-good.bin") 'PS2 BIOS from System.dat copied'
Check (Test-Path "$T\emus\DatRom\rom.bin") 'rom.bin accepted by its System.dat hash'
Check (-not (Test-Path "$T\emus\DatRom\named.bin")) 'named.bin not copied'
"f2: $fails failed"
