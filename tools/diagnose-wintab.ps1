# One-shot Wintab diagnosis.
#
#   1. Reads the shared-memory ring the OpenTabletDriver output mode writes,
#      so we can tell whether the daemon is feeding samples at all.
#   2. Summarises every per-process diagnostics log, which tells us which
#      applications actually loaded the shim and with which packet mask.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File diagnose-wintab.ps1

$mappingName = "Local\OpenTabletDriverWintab"
$logDir = Join-Path $env:LOCALAPPDATA "OTDWintab"

Write-Host "== shared buffer =="
try {
    $mmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($mappingName)
    $view = $mmf.CreateViewAccessor(0, 32)

    $magic = $view.ReadUInt32(0)
    $version = $view.ReadUInt32(4)
    $enabled = $view.ReadUInt32(8)
    $width = $view.ReadInt32(16)
    $height = $view.ReadInt32(20)
    $maxPressure = $view.ReadUInt32(24)

    $seq1 = $view.ReadUInt32(28)
    Start-Sleep -Milliseconds 1000
    $seq2 = $view.ReadUInt32(28)
    $heartbeat = $view.ReadUInt32(12)
    $age = [uint32](([Environment]::TickCount - [int]$heartbeat) -band 0xFFFFFFFF)

    Write-Host ("  magic=0x{0:X} version={1} enabled={2}" -f $magic, $version, $enabled)
    Write-Host ("  digitizer={0}x{1} maxPressure={2}" -f $width, $height, $maxPressure)
    Write-Host ("  samples: {0} -> {1}  ({2} in 1s)" -f $seq1, $seq2, ($seq2 - $seq1))
    Write-Host ("  heartbeat age: {0} ms" -f $age)

    if ($seq1 -eq 0 -and $seq2 -eq 0) {
        Write-Host "  => daemon is NOT publishing. Start OpenTabletDriver and touch the pen."
    }
    elseif ($seq2 -eq $seq1) {
        Write-Host "  => buffer exists but is frozen: no pen movement, or the Wintab output mode is not selected."
    }
    else {
        Write-Host "  => feeding normally."
    }

    $view.Dispose()
    $mmf.Dispose()
}
catch {
    Write-Host "  not available: $($_.Exception.Message)"
    Write-Host "  => the mapping does not exist. OpenTabletDriver is not running,"
    Write-Host "     or the OpenTabletDriver.Wintab output mode is not active."
}

Write-Host ""
Write-Host "== per-process logs: $logDir =="
if (-not (Test-Path -LiteralPath $logDir)) {
    Write-Host "  no process has ever loaded the shim (no log directory)."
    exit 0
}

$logs = Get-ChildItem -LiteralPath $logDir -Filter "wintab-*.log" -File | Sort-Object LastWriteTime
if ($logs.Count -eq 0) {
    Write-Host "  log directory exists but is empty: no application loaded the shim."
    exit 0
}

foreach ($file in $logs) {
    $lines = Get-Content -LiteralPath $file.FullName -Encoding utf8
    $header = if ($lines.Count -gt 0) { $lines[0] } else { "(empty)" }
    $opens = @($lines | Select-String -Pattern "WTOpen" -SimpleMatch)
    $infos = @($lines | Select-String -Pattern "WTInfoW DVC" -SimpleMatch)
    $contexts = @($lines | Select-String -Pattern "WTInfoW context" -SimpleMatch)

    Write-Host ""
    Write-Host ("  {0}" -f $file.Name)
    Write-Host ("    {0}" -f $header)
    Write-Host ("    opened contexts: {0}" -f $opens.Count)

    foreach ($open in ($opens | Select-Object -Last 4)) {
        Write-Host ("      {0}" -f ($open.Line -replace ".*\s{2,}", ""))
    }
    if ($infos.Count -gt 0) {
        Write-Host ("    DVC queries: {0}  (last: {1})" -f $infos.Count,
            ($infos[-1].Line -replace ".*\s{2,}", ""))
    }
    Write-Host ("    context queries: {0}" -f $contexts.Count)

    # Packet path: what we actually handed the application and what it asked
    # for. This is where a "pressure reads as zero" turns into a real number.
    $pkts = @($lines | Select-String -Pattern " PKT " -SimpleMatch)
    $peaks = @($lines | Select-String -Pattern " PEAK " -SimpleMatch)
    $packs = @($lines | Select-String -Pattern " PACK serial=" -SimpleMatch)
    $calls = @($lines | Select-String -Pattern " CALL " -SimpleMatch)
    $gets = @($lines | Select-String -Pattern "GET count=" -SimpleMatch)
    $gets0 = @($lines | Select-String -Pattern "GET0 " -SimpleMatch)
    $misses = @($lines | Select-String -Pattern "MISS serial=" -SimpleMatch)
    $msgs = @($lines | Select-String -Pattern "MSG id=" -SimpleMatch)

    Write-Host ("    packets sampled: {0}   WTPacketsGet: {1}   WTPacket misses: {2}   messages posted: {3}" -f
        $pkts.Count, $gets.Count, $misses.Count, $msgs.Count)

    foreach ($pkt in ($pkts | Select-Object -Last 5)) {
        Write-Host ("      {0}" -f ($pkt.Line -replace ".*\s{2,}", ""))
    }
    foreach ($peak in ($peaks | Select-Object -Last 4)) {
        Write-Host ("      {0}" -f ($peak.Line -replace ".*\s{2,}", ""))
    }
    foreach ($pack in ($packs | Select-Object -Last 5)) {
        Write-Host ("      {0}" -f ($pack.Line -replace ".*\s{2,}", ""))
    }
    $infos = @($calls | Select-String -Pattern "WTInfo" -SimpleMatch)
    Write-Host ("    WTInfo declaration queries: {0}   total calls traced: {1}" -f
        $infos.Count, $calls.Count)
    foreach ($info in ($infos | Select-Object -Last 80)) {
        Write-Host ("      {0}" -f ($info.Line -replace ".*\s{2,}", ""))
    }
    foreach ($call in ($calls | Select-Object -Last 12)) {
        if ($call.Line -match "WTInfo") { continue }
        Write-Host ("      {0}" -f ($call.Line -replace ".*\s{2,}", ""))
    }
    if ($gets0.Count -gt 0) {
        Write-Host ("      empty WTPacketsGet calls: {0}  (last: {1})" -f $gets0.Count,
            ($gets0[-1].Line -replace ".*\s{2,}", ""))
    }
    if ($gets.Count -gt 0) {
        Write-Host ("      last GET: {0}" -f ($gets[-1].Line -replace ".*\s{2,}", ""))
    }
    if ($misses.Count -gt 0) {
        Write-Host ("      last MISS: {0}" -f ($misses[-1].Line -replace ".*\s{2,}", ""))
    }
    foreach ($msg in ($msgs | Select-Object -Last 3)) {
        Write-Host ("      {0}" -f ($msg.Line -replace ".*\s{2,}", ""))
    }
}

Write-Host ""
Write-Host "How to read this:"
Write-Host "  = a log named wintab-Photoshop-*.log        => Photoshop uses WinTab (our shim)"
Write-Host "  = no Photoshop log at all                   => Photoshop uses Windows Ink; the shim is irrelevant to it"
Write-Host "  = wintab-sai*.log with opened contexts > 0  => SAI loaded the shim"
Write-Host "  = mask values in WTOpen lines               => the packet layout each app expects"
