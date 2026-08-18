<#
.SYNOPSIS
    Diagnostic tool to isolate ZK9500 capture failure root cause.

.DESCRIPTION
    Runs 4 isolated tests against the FingerprintAgent + ZKTeco ZK9500 stack:

    T1: /health endpoint            → confirms agent is reachable + SDK init state
    T2: /api/capture WITH finger    → tests if capture works when user has finger on scanner
    T3: /api/capture WITHOUT finger → measures SDK native timeout (expected ~5-10s)
    T4: Process/device sharing      → lists processes that may hold the USB sensor

    USE THIS TO IDENTIFY WHICH LAYER FAILS:
      - Layer A (driver/device):  T1 health=unhealthy, T2/T3 both fail at Initialize
      - Layer B (SDK timeout):    T2 succeeds, T3 fails with ERROR_CAPTURE after 5-10s
      - Layer C (state corrupt):  T2 fails with ERROR_INITLIB (after a previous failure)
      - Layer D (agent bug):      T2 fails but vendor ZKFinger demo succeeds

.PARAMETER BaseUrl
    Agent HTTP base URL. Default http://localhost:5043

.PARAMETER ThamChieuId
    Test reference ID sent to /api/capture

.PARAMETER MaPhieu
    Test order ID sent to /api/capture

.EXAMPLE
    .\scripts\diagnostic\Test-ZK9500.ps1
    # Run all 4 tests; user must place finger on scanner during T2

.NOTES
    Safe to run multiple times. Does not modify agent config or registry.
    Read-only diagnostic — no service install/uninstall.
#>

[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5043",
    [string]$ThamChieuId = "diag-001",
    [string]$MaPhieu = "DIAG-2026-0001",
    [switch]$SkipHealth,
    [switch]$SkipCaptureWithFinger,
    [switch]$SkipCaptureNoFinger,
    [switch]$SkipProcessScan
)

$ErrorActionPreference = "Continue"

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host ("=" * 70) -ForegroundColor Cyan
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host ("=" * 70) -ForegroundColor Cyan
}

function Write-SubSection {
    param([string]$Title)
    Write-Host ""
    Write-Host "--- $Title ---" -ForegroundColor Yellow
}

function Get-Health {
    try {
        $resp = Invoke-RestMethod -Uri "$BaseUrl/health" -Method GET -TimeoutSec 5 -ErrorAction Stop
        return $resp
    } catch {
        Write-Warning "Health endpoint unreachable: $($_.Exception.Message)"
        return $null
    }
}

function Invoke-CaptureWithTiming {
    [CmdletBinding()]
    param(
        [string]$Label,
        [string]$Url,
        [string]$Body
    )

    Write-Host ""
    Write-Host "[$Label] Sending POST /api/capture at $(Get-Date -Format 'HH:mm:ss.fff')" -ForegroundColor Green
    Write-Host "[$Label] $(if ($Label -match 'FINGER') { '>>> PLACE YOUR FINGER ON THE SCANNER NOW <<<' } else { '>>> DO NOT touch the scanner <<<' })" -ForegroundColor Magenta

    $start = Get-Date
    try {
        $resp = Invoke-RestMethod -Uri $Url -Method POST -ContentType "application/json" -Body $Body -TimeoutSec 30 -ErrorAction Stop
        $elapsed = (Get-Date) - $start
        Write-Host ("[{0}] Response in {1:N2}s" -f $Label, $elapsed.TotalSeconds) -ForegroundColor Green
        return @{ Success = $true; ElapsedSec = $elapsed.TotalSeconds; Response = $resp }
    } catch {
        $elapsed = (Get-Date) - $start
        $msg = $_.Exception.Message
        $statusCode = $null
        if ($msg -match '\((\d+)\)|status code (\d+)') {
            $statusCode = if ($matches[1]) { $matches[1] } else { $matches[2] }
        }
        Write-Host ("[{0}] Failed after {1:N2}s | status={2} | error={3}" -f $Label, $elapsed.TotalSeconds, $statusCode, $msg) -ForegroundColor Red
        return @{ Success = $false; ElapsedSec = $elapsed.TotalSeconds; StatusCode = $statusCode; Error = $msg }
    }
}

function Get-LikelyFingerprintProcesses {
    Write-Host ""
    Write-Host "--- Processes that commonly hold fingerprint sensors ---" -ForegroundColor Yellow

    $patterns = @(
        'zkfp', 'libzkfp', 'zkfinger', 'ZKFinger', 'ZKTeco', 'fpengin',
        'WbioSrvc', 'WBFScardAuth', 'BioExService'  # Windows Biometric Service components
    )

    $found = @()
    foreach ($pattern in $patterns) {
        try {
            $procs = Get-Process -ErrorAction SilentlyContinue |
                Where-Object { $_.ProcessName -match $pattern }
            if ($procs) { $found += $procs }
        } catch { }
    }

    if ($found.Count -eq 0) {
        Write-Host "  (none of the known fingerprint-related processes are running)" -ForegroundColor DarkGray
    } else {
        foreach ($p in ($found | Select-Object -Unique)) {
            $svcInfo = ""
            try {
                $svc = Get-Service -Name $p.ServiceName -ErrorAction SilentlyContinue
                if ($svc) { $svcInfo = " | service: $($svc.Name) ($($svc.Status))" }
            } catch { }
            Write-Host ("  PID={0,-6} Name={1,-30} Path={2}{3}" -f $p.Id, $p.ProcessName, $p.Path, $svcInfo) -ForegroundColor Gray
        }
    }
    return $found
}

function Get-UsbFingerprintDevices {
    Write-Host ""
    Write-Host "--- USB devices matching fingerprint/ZK (PnP enumeration) ---" -ForegroundColor Yellow
    try {
        $usbDevices = Get-PnpDevice -Class Biometric -ErrorAction SilentlyContinue
        if (-not $usbDevices) { $usbDevices = @() }
        $zkDevices = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
            ($_.FriendlyName -match 'ZK|ZKteco|fingerprint|ZK9500') -and
            ($_.Status -eq 'OK' -or $_.Status -eq 'Error')
        }
        $combined = @($usbDevices) + @($zkDevices) | Where-Object { $_ } | Select-Object -Unique
        if ($combined.Count -eq 0) {
            Write-Host "  (no fingerprint-class or ZK-named PnP devices found)" -ForegroundColor DarkGray
        } else {
            foreach ($d in $combined) {
                $color = if ($d.Status -eq 'OK') { 'Green' } else { 'Red' }
                Write-Host ("  [{0}] {1,-40} | InstanceId={2}" -f $d.Status, $d.FriendlyName, $d.InstanceId) -ForegroundColor $color
            }
        }
        return $combined
    } catch {
        Write-Warning "PnP enumeration failed: $($_.Exception.Message)"
        return @()
    }
}

# =================== MAIN ===================

Write-Section "ZK9500 Capture Diagnostic — FingerprintAgent"
Write-Host "Agent URL: $BaseUrl"
Write-Host "Test ID:   $ThamChieuId"
Write-Host "Time:      $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

$results = [ordered]@{}

# T1: Health
if (-not $SkipHealth) {
    Write-Section "T1: Health endpoint check"
    $health = Get-Health
    if ($health) {
        $statusColor = if ($health.status -eq 'healthy') { 'Green' } else { 'Yellow' }
        Write-Host ("  status      = {0}" -f $health.status) -ForegroundColor $statusColor
        Write-Host ("  deviceId    = {0}" -f $health.deviceId)
        Write-Host ("  uptime      = {0}" -f $health.uptime)
        Write-Host ("  inBackoff   = {0}" -f $health.inBackoff)
        Write-Host ("  backoffStep = {0}" -f $health.backoffStep)
        $results['T1_health'] = $health
    } else {
        $results['T1_health'] = $null
    }
}

# T2: Capture WITH finger on scanner
if (-not $SkipCaptureWithFinger) {
    Write-Section "T2: Capture WITH finger on scanner (test hypothesis: timeout #1)"
    Write-Host "Get ready — when you see 'PLACE YOUR FINGER NOW' you have ~10s window."

    for ($i = 3; $i -ge 1; $i--) {
        Write-Host -NoNewline ("  Starting in {0}... " -f $i)
        Start-Sleep -Seconds 1
    }
    Write-Host ""

    $body = @{
        thamChieuId = $ThamChieuId
        maPhieu     = $MaPhieu
    } | ConvertTo-Json -Depth 3

    $results['T2_captureWithFinger'] = Invoke-CaptureWithTiming -Label "FINGER" -Url "$BaseUrl/api/capture" -Body $body
}

# T3: Capture WITHOUT finger
if (-not $SkipCaptureNoFinger) {
    Write-Section "T3: Capture WITHOUT finger (measure SDK timeout)"
    Write-Host "DO NOT place finger on scanner. This measures native SDK timeout."

    $body = @{
        thamChieuId = "$ThamChieuId-noFinger"
        maPhieu     = "$MaPhieu-NF"
    } | ConvertTo-Json -Depth 3

    $results['T3_captureNoFinger'] = Invoke-CaptureWithTiming -Label "NO FINGER" -Url "$BaseUrl/api/capture" -Body $body
}

# T4: Process / device sharing
if (-not $SkipProcessScan) {
    Write-Section "T4: Process + USB enumeration (test hypothesis: device busy #4)"
    $results['T4_processes'] = Get-LikelyFingerprintProcesses
    $results['T4_usbDevices'] = Get-UsbFingerprintDevices
}

# =================== ANALYSIS ===================

Write-Section "ANALYSIS"

$t1 = $results['T1_health']
$t2 = $results['T2_captureWithFinger']
$t3 = $results['T3_captureNoFinger']
$t4 = $results['T4_processes']

$verdict = ""

if ($t1 -and $t1.status -ne 'healthy') {
    $verdict += "[Layer A] Agent reports unhealthy. DeviceId is empty or backoff active. Check vendor DLL paths and Device Manager.`n"
}

if ($t2 -and $t2.Success) {
    $verdict += "[OK] T2 capture succeeded WITH finger. SDK + driver + agent are working.`n"
} elseif ($t2 -and -not $t2.Success) {
    if ($t2.ElapsedSec -lt 4) {
        $verdict += "[Layer A or D] T2 failed in <4s with finger present. Likely Initialize fail (driver/device/agent code).`n"
    } elseif ($t2.ElapsedSec -lt 12) {
        $verdict += "[Layer B?] T2 failed in 4-12s even with finger. Either SDK did not detect finger OR agent timeout (ScannerManager per-adapter budget=3s + total=10s).`n"
    } else {
        $verdict += "[Layer B or D] T2 timed out at total budget 10s. Likely SDK AcquireFingerprint blocked + ScannerManager fired its own timeout.`n"
    }
}

if ($t3) {
    if ($t3.Success) {
        $verdict += "[!] T3 succeeded WITHOUT finger — agent returns dummy image. SDK never reached AcquireFingerprint. Re-check agent wiring.`n"
    } elseif ($t3.ElapsedSec -ge 4 -and $t3.ElapsedSec -le 12) {
        $verdict += "[OK / expected] T3 failed in 4-12s without finger — this is SDK native timeout (ZKFP_ERR_CAPTURE).`n"
    } elseif ($t3.ElapsedSec -lt 4) {
        $verdict += "[Layer A] T3 failed in <4s without finger — Initialize failed (device not open). SDK or driver issue.`n"
    } elseif ($t3.ElapsedSec -gt 12) {
        $verdict += "[Layer D] T3 timed out at 10s — ScannerManager total timeout fired. SDK blocked beyond 10s (cascading timeout).`n"
    }
}

if ($t4 -and $t4.Count -gt 0) {
    $verdict += "[Layer D] $($t4.Count) process(es) may be holding the device. Kill vendor/Windows Biometric services if applicable.`n"
}

Write-Host $verdict -ForegroundColor Cyan

Write-Section "NEXT STEPS"
Write-Host "1. If T2 (with finger) succeeded → root cause was user timing (hypothesis #1)."
Write-Host "   Apply UX fix: pre-capture prompt so user knows to place finger."
Write-Host ""
Write-Host "2. If T2 succeeded but T3 hangs >10s → SDK timeout too long."
Write-Host "   Apply ScannerManager-level tightening OR configure SDK timeout."
Write-Host ""
Write-Host "3. If both T2 and T3 fail at Initialize with ERROR_INITLIB → state corruption."
Write-Host "   ZKTecoAdapter.EnsureHostInitialized() recovery incomplete. Fix needed."
Write-Host ""
Write-Host "4. If T2 fails but vendor ZKFinger Demo works → bug in agent code."
Write-Host "   Add diagnostic logging inside ZKTecoAdapter.Scan() to narrow down."
Write-Host ""
Write-Host "5. If T2 fails AND vendor demo fails → driver/device issue."
Write-Host "   Reinstall ZK9500 driver; check USB port power management."

Write-Host ""
Write-Host "Diagnostic complete." -ForegroundColor Green
