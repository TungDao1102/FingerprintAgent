# Quick service start/stop for FingerprintAgent
param(
    [Parameter(Position=0)]
    [ValidateSet('start','stop','restart','status')]
    [string]$Action = 'status'
)

switch ($Action) {
    'start'   { Start-Service FingerprintAgent; Get-Service FingerprintAgent | Format-List Status }
    'stop'    { Stop-Service FingerprintAgent -Force; Get-Service FingerprintAgent | Format-List Status }
    'restart' { Restart-Service FingerprintAgent -Force; Get-Service FingerprintAgent | Format-List Status }
    'status'  { Get-Service FingerprintAgent | Format-List Name,Status,StartType }
}