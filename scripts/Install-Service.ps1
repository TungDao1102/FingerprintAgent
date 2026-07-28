#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$BinPath = $null,
    [string]$LogDir = "C:\ProgramData\FingerprintAgent\Logs",
    [string]$ServiceName = "FingerprintAgent"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($BinPath)) {
    $base = if ($PSScriptRoot) { $PSScriptRoot } else { $PWD.Path }
    $BinPath = Join-Path $base "..\src\FingerprintAgent\bin\Release\net48\FingerprintAgent.exe"
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Error "This script must be run as Administrator."
    exit 1
}

if (-not (Test-Path -Path $BinPath)) {
    Write-Error "Binary not found: $BinPath. Build the solution in Release configuration first (dotnet build FingerprintAgent.sln -c Release)."
    exit 1
}

$resolvedBinPath = (Resolve-Path $BinPath).Path

# Remove existing service if present
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service $ServiceName already exists. Stopping and removing..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $scOutput = sc.exe delete $ServiceName
    if ($LASTEXITCODE -ne 0) {
        Write-Error "sc.exe delete failed: $scOutput"
    }
    Start-Sleep -Seconds 2
}

Write-Host "Creating service $ServiceName..."
$binaryPathName = '"{0}" --service' -f $resolvedBinPath
New-Service -Name $ServiceName `
    -BinaryPathName $binaryPathName `
    -DisplayName "Fingerprint Agent" `
    -Description "Local fingerprint capture service providing HTTP API for web applications" `
    -StartupType Automatic | Out-Null

Write-Host "Configuring service recovery..."
$recoveryOutput = sc.exe failure $ServiceName reset=86400 actions=restart/5000/restart/10000/restart/30000
if ($LASTEXITCODE -ne 0) {
    Write-Warning "sc.exe failure configuration returned non-zero: $recoveryOutput"
}

Write-Host "Creating log directory $LogDir..."
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

Write-Host "Registering EventLog source $ServiceName..."
if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    [System.Diagnostics.EventLog]::CreateEventSource($ServiceName, "Application")
}

Write-Host "Service $ServiceName installed successfully."
Write-Host "Start it with: Start-Service $ServiceName"
