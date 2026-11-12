<#
.SYNOPSIS
  Captures USB traffic while a manufacturer's software reads a device's battery, then searches the
  capture for the request/reply that carries the battery value.

.EXAMPLES
  # 1) capture (run PowerShell as Administrator), then it asks which % the official software showed
  powershell -ExecutionPolicy Bypass -File .\battery-capture.ps1 -Seconds 45 -VendorId 3537

  # 2) analyze an existing classic .pcap capture (USBPcap link type) without capturing again
  powershell -ExecutionPolicy Bypass -File .\battery-capture.ps1 -Analyze .\capture.pcap -Battery 85

REQUIREMENTS: USBPcap (installed with Wireshark's "USBPcap" option) + reboot. Capture needs Administrator.
#>
[CmdletBinding()]
param(
    [int]$Battery = 0,          # % shown by the official software (asked after the capture if omitted)
    [int]$Seconds = 45,         # capture duration
    [string]$VendorId,          # optional filter, hex, e.g. 3537 (GameSir) or 1D57 (Attack Shark dongle)
    [string]$ProductId,         # optional filter, hex
    [string]$Analyze,           # analyze this .pcap (or a folder of .pcap files) instead of capturing
    [int[]]$AlsoTry = @()       # extra byte values to look for (e.g. a raw value you suspect)
)
$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------- capture
function Find-UsbPcap {
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        $c = Join-Path $base 'USBPcap\USBPcapCMD.exe'
        if (Test-Path $c) { return $c }
    }
    throw "USBPcapCMD.exe not found. Install Wireshark with the USBPcap option (or USBPcap alone) and reboot."
}

function Start-UsbCapture([string]$cmd, [string]$dir, [int]$seconds) {
    $hubs = @(& $cmd --extcap-interfaces | ForEach-Object { if ($_ -match '\{value=([^}]+)\}') { $Matches[1] } })
    if ($hubs.Count -eq 0) { throw "No USBPcap root hub found (did you reboot after installing USBPcap?)." }

    $procs = @(); $files = @(); $n = 0
    foreach ($hub in $hubs) {
        $n++
        $file = Join-Path $dir ("hub{0}.pcap" -f $n)
        $argList = @('-d', "`"$hub`"", '-o', "`"$file`"", '-A', '--inject-descriptors')
        $p = Start-Process -FilePath $cmd -ArgumentList $argList -PassThru -WindowStyle Hidden
        Start-Sleep -Milliseconds 1500
        if ($p.HasExited) {   # older USBPcap without --inject-descriptors: retry without it
            $argList = @('-d', "`"$hub`"", '-o', "`"$file`"", '-A')
            $p = Start-Process -FilePath $cmd -ArgumentList $argList -PassThru -WindowStyle Hidden
            Start-Sleep -Milliseconds 1500
        }
        $procs += $p; $files += $file
    }

    Write-Host ""
    Write-Host ">>> CAPTURE RUNNING ($seconds s). NOW open the official software and wait until it shows the battery." -ForegroundColor Yellow
    Write-Host "    (If it was already open, close it and reopen it now, so it asks the device again.)"
    for ($s = $seconds; $s -gt 0; $s--) {
        Write-Progress -Activity "Capturing USB traffic" -Status "$s s left" -PercentComplete (100 * ($seconds - $s) / $seconds)
        Start-Sleep -Seconds 1
    }
    Write-Progress -Activity "Capturing USB traffic" -Completed
    foreach ($p in $procs) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
    Start-Sleep -Milliseconds 500
    return $files
}

# ----------------------------------------------------------------- pcap parsing (USBPcap, link type 249)
function Read-UsbPcap([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    $events = New-Object System.Collections.Generic.List[object]
    $ids = @{}
    if ($b.Length -lt 24) { return @{ Events = $events; Ids = $ids } }
    if ($b[0] -ne 0xD4 -or $b[1] -ne 0xC3 -or $b[2] -ne 0xB2 -or $b[3] -ne 0xA1) { throw "$path is not a classic little-endian .pcap (pcapng is not supported; save as pcap)." }
    if ([BitConverter]::ToUInt32($b, 20) -ne 249) { throw "$path is not a USBPcap capture (link type $([BitConverter]::ToUInt32($b, 20)))." }

    $pos = 24
    $lastSetup = @{}
    while ($pos + 16 -le $b.Length) {
        $t = [BitConverter]::ToUInt32($b, $pos) + [BitConverter]::ToUInt32($b, $pos + 4) / 1e6
        $incl = [int][BitConverter]::ToUInt32($b, $pos + 8)
        $pos += 16
        if ($pos + $incl -gt $b.Length) { break }      # truncated tail (capture was stopped abruptly)
        $p = $pos; $pos += $incl
        if ($incl -lt 27) { continue }

        $hdr  = [int][BitConverter]::ToUInt16($b, $p)
        $info = $b[$p + 16]
        $bus  = [BitConverter]::ToUInt16($b, $p + 17)
        $dev  = [BitConverter]::ToUInt16($b, $p + 19)
        $ep   = $b[$p + 21]
        $xfer = $b[$p + 22]
        $dlen = [int][BitConverter]::ToUInt32($b, $p + 23)
        $n = [Math]::Max([Math]::Min($dlen, $incl - $hdr), 0)
        $data = New-Object byte[] $n
        if ($n -gt 0) { [Array]::Copy($b, $p + $hdr, $data, 0, $n) }

        $key = "$bus`:$dev"
        $toHost = (($info -band 1) -eq 1)     # completion = data coming back from the device
        $dir = $null; $setup = $null

        if ($xfer -eq 2) {                    # control transfer
            $stage = if ($hdr -ge 28) { $b[$p + 27] } else { -1 }
            if ($stage -eq 0 -and $n -ge 8) { $lastSetup[$key] = $data; continue }
            if ($stage -eq 1 -and $n -gt 0) {
                $setup = $lastSetup[$key]
                $isIn = ($null -ne $setup) -and (($setup[0] -band 0x80) -ne 0)
                if ($isIn -and $toHost) { $dir = 'IN' } elseif (-not $isIn -and -not $toHost) { $dir = 'OUT' }
                # remember VID/PID from GET_DESCRIPTOR(device)
                if ($dir -eq 'IN' -and $setup[1] -eq 6 -and $setup[3] -eq 1 -and $n -ge 18) {
                    $ids[$key] = @{ Vid = ($data[8] + 256 * $data[9]); Pid = ($data[10] + 256 * $data[11]) }
                }
            }
        }
        elseif ($n -gt 0) {                   # interrupt / bulk
            $isIn = (($ep -band 0x80) -ne 0)
            if ($isIn -and $toHost) { $dir = 'IN' } elseif (-not $isIn -and -not $toHost) { $dir = 'OUT' }
        }
        if ($dir) {
            [void]$events.Add([pscustomobject]@{ T = $t; Key = $key; Ep = $ep; Xfer = $xfer; Dir = $dir; Data = $data; Setup = $setup })
        }
    }
    return @{ Events = $events; Ids = $ids }
}

function Hex($bytes, [int]$max = 48) {
    if ($null -eq $bytes -or $bytes.Length -eq 0) { return '' }
    $s = ($bytes | Select-Object -First $max | ForEach-Object { $_.ToString('X2') }) -join ' '
    if ($bytes.Length -gt $max) { $s += ' ...' }
    return $s
}

# ----------------------------------------------------------------- analysis
function Invoke-Analysis([string[]]$files, [int]$battery, [string]$report) {
    $out = New-Object System.Collections.Generic.List[string]
    function L([string]$s) { $out.Add($s); Write-Host $s }

    $all = New-Object System.Collections.Generic.List[object]
    $ids = @{}
    foreach ($f in $files) {
        if (-not (Test-Path $f) -or (Get-Item $f).Length -lt 24) { continue }
        $r = Read-UsbPcap $f
        $all.AddRange($r.Events)
        foreach ($k in $r.Ids.Keys) { $ids[$k] = $r.Ids[$k] }
    }
    $events = @($all | Sort-Object T)
    L "Analyzed $($events.Count) data packets from $($files.Count) file(s)."

    function Label([string]$key) {
        if ($ids.ContainsKey($key)) { return ("{0} (VID_{1:X4} PID_{2:X4})" -f $key, $ids[$key].Vid, $ids[$key].Pid) }
        return "$key (VID/PID unknown)"
    }
    function Wanted([string]$key) {
        if (-not $VendorId -and -not $ProductId) { return $true }
        if (-not $ids.ContainsKey($key)) { return $true }
        if ($VendorId  -and ('{0:X4}' -f $ids[$key].Vid) -ne $VendorId.ToUpper().PadLeft(4, '0'))  { return $false }
        if ($ProductId -and ('{0:X4}' -f $ids[$key].Pid) -ne $ProductId.ToUpper().PadLeft(4, '0')) { return $false }
        return $true
    }

    $events = @($events | Where-Object { Wanted $_.Key })

    L ""
    L "=== Devices with traffic ==="
    $events | Group-Object Key | ForEach-Object {
        $in = @($_.Group | Where-Object Dir -eq 'IN').Count; $o = @($_.Group | Where-Object Dir -eq 'OUT').Count
        L ("  {0}: {1} IN, {2} OUT" -f (Label $_.Name), $in, $o)
    }

    L ""
    L "=== Commands the host sent to the devices (most frequent) ==="
    $events | Where-Object Dir -eq 'OUT' | Group-Object { "{0}|ep{1:X2}|{2}|{3}" -f $_.Key, $_.Ep, (Hex $_.Setup 8), (Hex $_.Data 16) } |
        Sort-Object Count -Descending | Select-Object -First 15 | ForEach-Object {
            $e = $_.Group[0]
            $line = "  x{0} {1} ep{2:X2} " -f $_.Count, (Label $e.Key), $e.Ep
            if ($e.Setup) { $line += "setup[" + (Hex $e.Setup 8) + "] " }
            L ($line + "data[" + (Hex $e.Data 24) + "]")
        }

    if ($battery -le 0) { L ""; L "No -Battery value given: skipped the value search."; Set-Content $report $out; return }

    $targets = New-Object 'System.Collections.Generic.HashSet[int]'
    [void]$targets.Add($battery); [void]$targets.Add([int][Math]::Round($battery * 2.55))
    foreach ($x in $AlsoTry) { [void]$targets.Add($x) }

    $groups = @{}
    $outsByKey = @{}
    foreach ($e in $events) {
        if ($e.Dir -eq 'OUT') {
            if (-not $outsByKey.ContainsKey($e.Key)) { $outsByKey[$e.Key] = New-Object System.Collections.Generic.List[object] }
            $outsByKey[$e.Key].Add($e)
            continue
        }
        $k = "{0}|ep{1:X2}|len{2}|id{3:X2}" -f $e.Key, $e.Ep, $e.Data.Length, $e.Data[0]
        if (-not $groups.ContainsKey($k)) {
            $groups[$k] = [pscustomobject]@{ Key = $e.Key; Ep = $e.Ep; Len = $e.Data.Length; Id = $e.Data[0]; Count = 0; Hits = [int[]]::new($e.Data.Length); First = @{} }
        }
        $g = $groups[$k]; $g.Count++
        for ($i = 0; $i -lt $e.Data.Length; $i++) {
            if ($targets.Contains([int]$e.Data[$i])) {
                $g.Hits[$i]++
                if (-not $g.First.ContainsKey($i)) { $g.First[$i] = $e }
            }
        }
    }

    $cands = @(foreach ($g in $groups.Values) {
        $best = 0; $bi = -1
        for ($i = 0; $i -lt $g.Len; $i++) { if ($g.Hits[$i] -gt $best) { $best = $g.Hits[$i]; $bi = $i } }
        if ($best -gt 0) { [pscustomobject]@{ G = $g; Offset = $bi; Hits = $best; Ratio = [Math]::Round($best / $g.Count, 2) } }
    }) | Sort-Object @{ Expression = 'Ratio'; Descending = $true }, @{ Expression = 'Hits'; Descending = $true }

    L ""
    L ("=== Replies containing the value(s) {0} ===" -f (($targets | Sort-Object) -join ', '))
    if ($cands.Count -eq 0) {
        L "  Nothing found. The software may not have asked the device during the capture, or the value is encoded"
        L "  differently (raw voltage, 0-255 scale, 4 levels...). Try -AlsoTry with other values, or look at the"
        L "  'Commands the host sent' list above and the packets that follow them."
    }
    foreach ($c in ($cands | Select-Object -First 10)) {
        $g = $c.G; $first = $g.First[$c.Offset]
        $verdict = if ($g.Count -gt 200) { "probably noise (high-rate stream, e.g. mouse movement)" }
                   elseif ($c.Ratio -ge 0.9) { "STRONG candidate" } else { "weak candidate" }
        L ""
        L ("  [{0}] {1} ep{2:X2} len {3} first-byte {8:X2}: byte offset {4} matched in {5}/{6} packets (ratio {7})" -f $verdict, (Label $g.Key), $g.Ep, $g.Len, $c.Offset, $c.Hits, $g.Count, $c.Ratio, $g.Id)
        L ("     reply  : " + (Hex $first.Data 64))
        $req = $null
        if ($outsByKey.ContainsKey($g.Key)) {
            foreach ($o in $outsByKey[$g.Key]) { if ($o.T -le $first.T -and ($first.T - $o.T) -lt 1.0) { $req = $o } }
        }
        if ($req) {
            $su = if ($req.Setup) { "setup[" + (Hex $req.Setup 8) + "] " } else { "" }
            L ("     request: ep{0:X2} {1}data[{2}]  ({3:0} ms earlier)" -f $req.Ep, $su, (Hex $req.Data 64), (($first.T - $req.T) * 1000))
        } else {
            L "     request: none within 1 s before it (the device may send this on its own, periodically)"
        }
    }
    Set-Content -Path $report -Value $out
    L ""
    L "Report saved: $report"
}

# ----------------------------------------------------------------- main
if ($Analyze) {
    $files = if (Test-Path $Analyze -PathType Container) { @(Get-ChildItem $Analyze -Filter *.pcap | ForEach-Object FullName) } else { @($Analyze) }
    $dir = if (Test-Path $Analyze -PathType Container) { $Analyze } else { Split-Path -Parent (Resolve-Path $Analyze) }
    Invoke-Analysis $files $Battery (Join-Path $dir 'battery-analysis.txt')
    return
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Write-Warning "Not running as Administrator: USBPcap may refuse to capture. If it fails, reopen PowerShell as Administrator." }

$usbpcap = Find-UsbPcap
$dir = Join-Path ([Environment]::GetFolderPath('Desktop')) ("bt-capture-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $dir | Out-Null
Write-Host "Captures will be saved in: $dir"
Write-Host "Tip: close the official software BEFORE starting, open it when the capture is running."
[void](Read-Host "Press Enter to start the capture")

$files = Start-UsbCapture $usbpcap $dir $Seconds
if ($Battery -le 0) { $Battery = [int](Read-Host "Which battery % did the official software show") }
Invoke-Analysis $files $Battery (Join-Path $dir 'battery-analysis.txt')
