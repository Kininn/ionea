# Going online: serves http://localhost:47811 from a background job that
# writes down every request, runs o1-online.txt and then o1-online-restart.txt on the same data folder, and
# checks that only the allowed downloads reached the server. Nothing leaves this PC.
# Usage: o1-online.ps1 [-Exe <Launcher.exe>] [-Root <dir>]   (wipes <dir>)
param([string]$Exe = "$PSScriptRoot\..\bin\Release\net10.0-windows\Launcher.exe", [string]$Root = "$env:TEMP\Ionea-tests\o1-online")
$ErrorActionPreference = 'Stop'
$port = 47811
if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
$T = "$Root\T"; $data = "$Root\data"; $served = "$Root\served.txt"; $ready = "$Root\ready.txt"
New-Item -ItemType Directory -Force $T, $data | Out-Null
if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { "port $port is in use"; exit 1 }

# The test server: /cover.png, /core.dll, /moved (sent on to /cover.png), /away (sent on to another site).
$job = Start-Job -ArgumentList $port, $served, $ready -ScriptBlock {
    param($port, $served, $ready)
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://localhost:$port/")
    $l.Start()
    Set-Content $ready 'ready'
    while ($l.IsListening) {
        $c = $l.GetContext()
        $p = $c.Request.Url.AbsolutePath
        Add-Content $served $p
        $r = $c.Response
        switch ($p) {
            '/moved' { $r.StatusCode = 302; $r.RedirectLocation = "http://localhost:$port/cover.png"; $b = @() }
            '/away'  { $r.StatusCode = 302; $r.RedirectLocation = 'https://example.com/away.png'; $b = @() }
            '/cover.png' { $b = [Text.Encoding]::ASCII.GetBytes('IONEA-COVER') }
            '/core.dll'  { $b = [Text.Encoding]::ASCII.GetBytes('IONEA-CORE') }
            default { $r.StatusCode = 404; $b = @() }
        }
        if ($b.Length) { $r.OutputStream.Write($b, 0, $b.Length) }
        $r.Close()
    }
}
$fails = 0
function Check($ok, $what) { if ($ok) { "ok   $what" } else { "FAIL $what"; $script:fails++ } }
try {
    $until = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $ready) -and (Get-Date) -lt $until -and $job.State -eq 'Running') { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $ready)) { "server did not start: $($job.State)"; Receive-Job $job -ErrorAction SilentlyContinue | Out-Host; exit 1 }

    $env:IONEA_TESTROOT = $T
    $common = "--data `"$data`" --no-import --dummies 6 --windowed --mute --size 1040x620 --pos -30000,-30000 --shots `"$data\shots`" --online-prompt --online-test-host localhost:$port"
    foreach ($run in @(@{ Name = 'o1-online'; Extra = ' --setup' }, @{ Name = 'o1-online-restart'; Extra = '' })) {
        $before = if (Test-Path "$data\launcher.log") { (Get-Content "$data\launcher.log").Count } else { 0 }
        $p = Start-Process $Exe -ArgumentList ($common + $run.Extra + " --script `"$PSScriptRoot\$($run.Name).txt`"") -PassThru
        if (-not $p.WaitForExit(180000)) { "launcher still running; stopping it"; $p.Kill(); $fails++ }
        $log = Get-Content "$data\launcher.log" | Select-Object -Skip $before
        $log | Where-Object { $_ -match 'SCRIPT (FAIL|done)|online:' } | Out-Host
        Check (($log -match 'SCRIPT done, 0 failed').Count -eq 1) "$($run.Name): script 0 failed"
    }

    # Only the allowed downloads reached the server: nothing while off, nothing for a cancelled question.
    $got = @(if (Test-Path $served) { Get-Content $served })
    "served: $($got -join ', ')"
    Check (($got -join ',') -eq '/cover.png,/moved,/cover.png,/away,/core.dll') 'server saw exactly the 5 allowed requests'
    $entries = (Get-Content "$data\online-log.json" -Raw | ConvertFrom-Json).Entries
    Check ($entries.Count -eq 14) "log lists every download ($($entries.Count))"
    Check (@($entries | Where-Object { -not $_.When -or -not $_.Item -or -not $_.Url -or -not $_.Result }).Count -eq 0) 'each entry has what, from where, when, result'
    Check (@($entries | Where-Object Result -eq 'done').Count -eq 3) '3 done'
    $s = Get-Content "$data\settings.json" -Raw | ConvertFrom-Json
    Check ($s.OnlineUse.On.covers -eq $true -and $s.OnlineUse.On.emulators -eq $false -and $s.OnlineUse.On.definitions -eq $false) 'switches saved in settings.json'
}
finally {
    Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue
    $env:IONEA_TESTROOT = $null
}
"o1: $fails failed"
