<#
.SYNOPSIS
    Precise timing test for ZK9500 SDK capture timeout.

.DESCRIPTION
    Runs 3 timed tests to determine whether root cause is:
      (A) SDK native timeout too short — need UX fix
      (B) Sensor not detecting finger — need sensor/driver fix

    Must be run AFTER service is freshly restarted (backoff step=0).

.PARAMETER BaseUrl
    Agent URL (default http://localhost:5043)

.EXAMPLE
    # Place finger on scanner, then:
    .\scripts\diagnostic\Test-ZK9500-Timing.ps1
#>

[CmdletBinding()]
param([string]$BaseUrl = "http://localhost:5043")

$ErrorActionPreference = "Stop"
$endpoint = "$BaseUrl/api/capture"

function Send-Capture($label, $fingerPlaced) {
    $body = @{
        thamChieuId = "timing-$label"
        maPhieu     = "TIMING-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
    } | ConvertTo-Json -Depth 3

    Write-Host ""
    Write-Host "[$label] $(if ($fingerPlaced) { '>>> FINGER SHOULD BE ON SCANNER <<<' } else { '>>> NO FINGER <<<' })" -ForegroundColor Magenta
    Write-Host "[$label] Sending request at $(Get-Date -Format 'HH:mm:ss.fff')"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $resp = Invoke-RestMethod -Uri $endpoint -Method POST -ContentType "application/json" -Body $body -TimeoutSec 30
        $sw.Stop()
        Write-Host ("[{0}] SUCCESS in {1:N2}s | deviceId={2}" -f $label, $sw.Elapsed.TotalSeconds, $resp.deviceId) -ForegroundColor Green
        return @{ Success = $true; ElapsedSec = $sw.Elapsed.TotalSeconds; Response = $resp }
    } catch {
        $sw.Stop()
        $msg = $_.Exception.Message
        Write-Host ("[{0}] FAIL after {1:N2}s | {2}" -f $label, $sw.Elapsed.TotalSeconds, $msg) -ForegroundColor Red
        # Try to extract HTTP body from error
        $body = $_.Exception.Response
        if ($body) {
            try {
                $stream = $body.GetResponseStream()
                $reader = New-Object System.IO.StreamReader($stream)
                $bodyText = $reader.ReadToEnd()
                Write-Host ("[{0}] Body: {1}" -f $label, $bodyText) -ForegroundColor DarkYellow
            } catch {}
        }
        return @{ Success = $false; ElapsedSec = $sw.Elapsed.TotalSeconds; Error = $msg }
    }
}

Write-Host "============================================================"
Write-Host "  ZK9500 Timing Test — confirm SDK vs sensor root cause"
Write-Host "============================================================"

# Pre-flight
$health = try { Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 5 } catch { @{ status = 'unreachable' } }
Write-Host ""
Write-Host "Health: $($health | ConvertTo-Json -Compress)"
Write-Host ""

if ($health.status -eq 'unreachable') {
    Write-Error "Agent unreachable. Start service first: .\scripts\Service.ps1 start"
    exit 1
}

# Test 1: No finger (baseline - should hit SDK timeout)
Write-Host ""
Write-Host "TEST 1: No finger — measures native SDK timeout"
Write-Host "  Expected: 5-10s with ERROR_CAPTURE (SDK native timeout)"
Write-Host "  If <4s: Initialize() failed (not SDK timeout)"
Write-Host "  If >10s: ScannerManager 10s timeout fired first"
Read-Host "  Press ENTER when ready"
$t1 = Send-Capture "NO_FINGER" $false

# Test 2: Finger placed BEFORE request
Write-Host ""
Write-Host "TEST 2: Place finger on scanner BEFORE pressing ENTER"
Write-Host "  Keep finger on scanner steady. Expected: SUCCESS in 1-3s"
Write-Host "  If 5-10s with ERROR_CAPTURE: SDK bug — does not detect pre-placed finger"
Write-Host "  If >10s: ScannerManager timeout fired (SDK hung)"
Read-Host "  Press ENTER after finger is firmly on scanner"
$t2 = Send-Capture "FINGER_PRE" $true

# Test 3: Finger placed DURING request
Write-Host ""
Write-Host "TEST 3: Place finger DURING request — realistic UX"
Write-Host "  Press ENTER immediately AFTER request fires, then place finger"
Write-Host "  Tests if SDK has rolling timeout or single-shot"
$body = @{
    thamChieuId = "timing-FINGER_DURING"
    maPhieu     = "TIMING-DURING-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
} | ConvertTo-Json -Depth 3
Write-Host ""
Write-Host "[FINGER_DURING] Sending request NOW at $(Get-Date -Format 'HH:mm:ss.fff') — place finger!"
Write-Host "[FINGER_DURING] >>> PLACE FINGER ON SCANNER NOW <<<" -ForegroundColor Magenta
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $resp = Invoke-RestMethod -Uri $endpoint -Method POST -ContentType "application/json" -Body $body -TimeoutSec 30
    $sw.Stop()
    Write-Host ("[FINGER_DURING] SUCCESS in {0:N2}s" -f $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    $t3 = @{ Success = $true; ElapsedSec = $sw.Elapsed.TotalSeconds }
} catch {
    $sw.Stop()
    Write-Host ("[FINGER_DURING] FAIL after {0:N2}s | {1}" -f $sw.Elapsed.TotalSeconds, $_.Exception.Message) -ForegroundColor Red
    $t3 = @{ Success = $false; ElapsedSec = $sw.Elapsed.TotalSeconds }
}

# Verdict
Write-Host ""
Write-Host "============================================================"
Write-Host "  VERDICT"
Write-Host "============================================================"

# Adapter has an 8s rolling-capture budget (per fix(03)). Total ScannerManager budget is 10s.
# Observed server-side latency is reported by the script; HTTP overhead adds ~1-2s.

$verdict = ""

# T1: No finger — expected to FAIL by exhausting the 8s adapter retry budget
if ($t1.Success) {
    # SDK captured something with no finger — likely sensor residue/phantom. Investigate cleanliness.
    $verdict += "[!] T1 captured in ~$([Math]::Round($t1.ElapsedSec,1))s WITH NO FINGER — sensor residue or spurious detection (clean sensor and retry)`n"
} elseif ($t1.ElapsedSec -ge 7 -and $t1.ElapsedSec -le 12) {
    $verdict += "[OK] T1 fail at ~$([Math]::Round($t1.ElapsedSec,1))s = adapter 8s budget exhausted (expected, no finger)`n"
} elseif ($t1.ElapsedSec -gt 12) {
    $verdict += "[!] T1 fail >12s — ScannerManager 10s timeout fired (adapter hung beyond budget, possible SDK regression)`n"
} else {
    $verdict += "[!] T1 fail in <7s — adapter budget not exhausted, possible Initialize() or early-exit regression`n"
}

# T2: Finger pre-placed — expected SUCCESS quickly (SDK detects on first poll)
if ($t2.Success) {
    $t2Elapsed = [Math]::Round($t2.ElapsedSec, 1)
    if ($t2Elapsed -le 5) {
        $verdict += "[OK] T2 succeeded in ${t2Elapsed}s with finger pre-placed — SDK detects immediately`n"
    } else {
        $verdict += "[OK] T2 succeeded in ${t2Elapsed}s (slow but within 8s budget — SDK caught finger mid-retry)`n"
    }
} elseif ($t2.ElapsedSec -ge 4 -and $t2.ElapsedSec -le 12) {
    $verdict += "[ROOT CAUSE = Sensor/SDK] T2 fail with finger pre-placed in ~$([Math]::Round($t2.ElapsedSec,1))s — sensor not detecting finger (driver/calibration issue)`n"
} elseif ($t2.ElapsedSec -gt 12) {
    $verdict += "[ROOT CAUSE = SDK HANG] T2 timeout at ScannerManager — SDK hung beyond 10s`n"
}

# T3: Finger during request — expected SUCCESS within 8s rolling window
if ($t3.Success -and -not $t2.Success) {
    $verdict += "[INSIGHT] T3 succeeded but T2 failed — SDK has 'first-time' detection issue with pre-placed finger`n"
}

Write-Host $verdict -ForegroundColor Cyan
Write-Host ""
Write-Host "Report this verdict back to developer for next step."
