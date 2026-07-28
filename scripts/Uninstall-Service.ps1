#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$ServiceName = "FingerprintAgent"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Error "This script must be run as Administrator."
    exit 1
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Host "Service $ServiceName not found."
    exit 0
}

Write-Host "Stopping service $ServiceName..."
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Host "Removing service $ServiceName..."
$scOutput = sc.exe delete $ServiceName
if ($LASTEXITCODE -ne 0) {
    Write-Error "sc.exe delete failed: $scOutput"
}

Write-Host "Service $ServiceName removed successfully."
