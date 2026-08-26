[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5043",
    [string]$RequestId = "test-001",
    [string]$Purpose = "KhamBenh",
    [string]$FormCode = "P2026-0001",
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
    requestId = $RequestId
    purpose   = $Purpose
    metadata  = @{
        formCode = $FormCode
        source   = "Test-Capture.ps1"
    }
} | ConvertTo-Json -Depth 3

$capture = Invoke-SafeRestMethod -Uri "$BaseUrl/api/capture" -Method "POST" -Body $body
Write-Host "requestId      : $($capture.requestId)"
Write-Host "isSuccess      : $($capture.isSuccess)"
Write-Host "deviceId       : $($capture.deviceId)"
Write-Host "mimeType       : $($capture.mimeType)"
Write-Host "capturedAt     : $($capture.capturedAt)"
Write-Host "verificationData: $($capture.verificationData)"

if ($SaveImage -and $capture.imageBytes) {
    $imagePath = Join-Path $env:TEMP "fingerprint-capture.png"
    [System.IO.File]::WriteAllBytes($imagePath, [Convert]::FromBase64String($capture.imageBytes))
    Write-Host "`nImage saved to: $imagePath"
}
