[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5043",
    [string]$ThamChieuId = "test-001",
    [string]$MaPhieu = "P2026-0001",
    [string]$LoaiPhieu = "KhamBenh",
    [string]$VaiKyId = "vai-001",
    [string]$NhanLucId = "user-001",
    [switch]$SaveImage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-SafeRestMethod {
    param([string]$Uri, [string]$Method, [string]$Body)
    try {
        $result = Invoke-RestMethod -Uri $Uri -Method $Method -Body $Body -ContentType "application/json" -NoProxy -ErrorAction Stop
        return $result
    }
    catch {
        # Extract status code from exception message (works on both PS5 and PS7):
        # PS5: "...The remote server returned an unexpected response: (400) Bad Request"
        # PS7: "...Response status code does not indicate success: 400 (Bad Request)"
        $statusCode = $null
        if ($_.Exception.Message -match '\((\d+)\)|status code (\d+)') {
            $statusCode = if ($matches[1]) { $matches[1] } else { $matches[2] }
        }
        $msg = $_.Exception.Message
        if ($statusCode) {
            Write-Error "HTTP $statusCode : $msg"
        } else {
            Write-Error $msg
        }
    }
}

Write-Host "=== Health Check ==="
$health = Invoke-SafeRestMethod -Uri "$BaseUrl/health" -Method "GET"
$health | ConvertTo-Json -Depth 3 | Write-Host

Write-Host "`n=== Capture Request ==="
$body = @{
    thamChieuId = $ThamChieuId
    maPhieu = $MaPhieu
    loaiPhieu = $LoaiPhieu
    vaiKyId = $VaiKyId
    nhanLucId = $NhanLucId
    metadata = @{
        source = "Test-Capture.ps1"
    }
} | ConvertTo-Json -Depth 3

$capture = Invoke-SafeRestMethod -Uri "$BaseUrl/api/capture" -Method "POST" -Body $body
Write-Host "isSuccess     : $($capture.isSuccess)"
Write-Host "deviceId      : $($capture.deviceId)"
Write-Host "mimeType      : $($capture.mimeType)"
Write-Host "capturedAt    : $($capture.capturedAt)"
Write-Host "verificationData: $($capture.verificationData)"

if ($SaveImage -and $capture.imageBytes) {
    $imagePath = Join-Path $env:TEMP "fingerprint-capture.png"
    [Convert]::FromBase64String($capture.imageBytes) | Set-Content -Path $imagePath -Encoding Byte
    Write-Host "`nImage saved to: $imagePath"
}
