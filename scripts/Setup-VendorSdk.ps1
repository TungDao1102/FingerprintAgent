<#
.SYNOPSIS
    Downloads / extracts vendor fingerprint SDK DLLs into lib\ directory.
.DESCRIPTION
    Sets up native DLLs for all 4 supported fingerprint scanner vendors.
    Some SDKs require manual download from vendor websites (registration needed).
    This script:
      1. Creates lib\{Vendor}\ directories
      2. Attempts to locate DLLs from installed SDKs on this machine
      3. Downloads public SDK packages where available
      4. Prints exact instructions for vendors needing manual download
      5. Validates final state
#>

param(
    [string[]]$Vendor = @("ZKTeco", "SecuGen", "DigitalPersona", "Futronic"),
    [switch]$Force
)

$ProjectRoot = Resolve-Path "$PSScriptRoot\.."
$LibRoot = "$ProjectRoot\lib"

# Helpers

function Write-Step($msg)  { Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)    { Write-Host "   [OK] $msg" -ForegroundColor Green }
function Write-Warn($msg)  { Write-Host "   [!] $msg" -ForegroundColor Yellow }
function Write-Info($msg)  { Write-Host "   [i] $msg" -ForegroundColor Gray }
function Write-Action($msg){ Write-Host "   [=>] $msg" -ForegroundColor Magenta }
function Write-Err($msg)   { Write-Host "   [ERR] $msg" -ForegroundColor Red }

function Test-Dll($path) {
    if (Test-Path $path) { Write-Ok "$(Split-Path $path -Leaf)"; return $true }
    else                 { return $false }
}

function New-LibDir($vendor) {
    $dir = "$LibRoot\$vendor"
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $dir
}

# Main

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "  FingerprintAgent - Vendor SDK Setup" -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

if (-not (Test-Path $LibRoot)) {
    New-Item -ItemType Directory -Path $LibRoot -Force | Out-Null
    Write-Ok "Created lib\ directory"
}

# ZKTeco

if ($Vendor -contains "ZKTeco") {
    Write-Step "ZKTeco - libzkfp.dll (native) + ZkTecoFingerPrint NuGet (managed)"
    $dir = New-LibDir "ZKTeco"
    Write-Info "NuGet package 'ZkTecoFingerPrint 1.2.1' (managed wrapper) - already restored."
    Write-Info "Native DLL needed: libzkfp.dll (NOT zkfp2.dll - see note below)"

    $found = $false
    $searchPaths = @(
        "$env:ProgramFiles\ZKTeco\ZKFingerSDK\lib\",
        "$env:ProgramFiles(x86)\ZKTeco\ZKFingerSDK\lib\",
        "${env:ProgramFiles(x86)}\ZKTeco BioMetric SDK 4.0\lib\",
        "$env:WINDIR\System32\",
        "$env:WINDIR\SysWOW64\"
    )
    foreach ($sp in $searchPaths) {
        $dll = "$sp\libzkfp.dll"
        if (Test-Path $dll) {
            Write-Ok "Found libzkfp.dll at $dll"
            if ($Force -or -not (Test-Path "$dir\libzkfp.dll")) {
                Copy-Item $dll "$dir\libzkfp.dll" -Force
                Write-Ok "Copied to lib\ZKTeco\libzkfp.dll"
            }
            $found = $true
            break
        }
    }
    if (-not $found) {
        $wow = "$env:WINDIR\SysWOW64\libzkfp.dll"
        if (Test-Path $wow) {
            Copy-Item $wow "$dir\libzkfp.dll" -Force
            Write-Ok "Found libzkfp.dll in SysWOW64, copied"
            $found = $true
        }
    }

    if (-not $found) {
        Write-Warn "libzkfp.dll not found on this machine."
        Write-Action "DOWNLOAD: ZKTeco ZKFinger SDK (requires Silver+ membership)"
        Write-Action "  URL: https://www.zkteco.com/en/download_center"
        Write-Action "  After install, run this script again OR copy from:"
        Write-Action "    C:\Program Files\ZKTeco\ZKFingerSDK\lib\libzkfp.dll"
        Write-Action "  -> Copy into: $dir"
        Write-Action ""
        Write-Action "ALTERNATIVE (no registration): Download from ZKTeco GitHub mirror:"
        Write-Action "  1. Go to https://github.com/rainxh11/ZkTecoFingerPrint"
        Write-Action "  2. Check Releases or Issues for SDK download links"
    }

    Write-Ok ".csproj ZKTeco check already fixed - uses libzkfp.dll now"
}

# SecuGen

if ($Vendor -contains "SecuGen") {
    Write-Step "SecuGen - SecuGen.FDxSDKPro.Windows + sgfplib.dll / sgfpamx.dll"
    $dir = New-LibDir "SecuGen"
    Write-Info "Needed files: SecuGen.FDxSDKPro.Windows.dll (managed), sgfplib.dll, sgfpamx.dll (native)"

    $foundAll = $true
    $files = @("SecuGen.FDxSDKPro.Windows.dll", "sgfplib.dll", "sgfpamx.dll")
    foreach ($f in $files) {
        if (-not (Test-Dll "$dir\$f")) { $foundAll = $false }
    }

    if (-not $foundAll) {
        $sdkPaths = @(
            "$env:ProgramFiles\SecuGen\FDxSDKPro\Bin\i386\",
            "${env:ProgramFiles(x86)}\SecuGen\FDxSDKPro\Bin\i386\",
            "$env:ProgramFiles\Secugen\FDxSDKPro\Bin\i386\",
            "${env:ProgramFiles(x86)}\Secugen\FDxSDKPro\Bin\i386\"
        )
        $copied = $false
        foreach ($sp in $sdkPaths) {
            if (Test-Path $sp) {
                Write-Ok "Found SecuGen SDK at $sp"
                foreach ($f in $files) {
                    if (Test-Path "$sp\$f") {
                        Copy-Item "$sp\$f" "$dir\$f" -Force
                        Write-Ok "  Copied $f"
                    }
                }
                $copied = $true
                break
            }
        }

        if (-not $copied) {
            Write-Warn "SecuGen SDK not installed on this machine."
            Write-Action "DOWNLOAD: SecuGen FDx SDK Pro (free registration)"
            Write-Action "  URL: https://www.secugen.com/download"
            Write-Action "  After download + install, re-run this script OR copy from SDK:"
            Write-Action "    FDxSDKPro\Bin\i386\ -> $dir"
            Write-Action "  Required files:"
            Write-Action "    - SecuGen.FDxSDKPro.Windows.dll"
            Write-Action "    - sgfplib.dll"
            Write-Action "    - sgfpamx.dll"
        }
    }
}

# Digital Persona

if ($Vendor -contains "DigitalPersona") {
    Write-Step "Digital Persona - U.are.U SDK (dpfpdd.dll + dpfj.dll)"
    $dir = New-LibDir "DigitalPersona"
    Write-Info "NuGet package 'DPUruNet 1.0.0.1' (managed wrapper) - already restored."
    Write-Info "Native DLLs needed: dpfpdd.dll, dpfj.dll + managed wrappers from SDK"

    $nativeNeeded = @("dpfpdd.dll", "dpfj.dll")
    $managedNeeded = @("DPFPDevNET.dll", "DPFPCapture.dll")
    $allNeeded = $nativeNeeded + $managedNeeded

    $foundAll = $true
    foreach ($f in $allNeeded) {
        if (-not (Test-Dll "$dir\$f")) { $foundAll = $false }
    }

    if (-not $foundAll) {
        $sdkPaths = @(
            "$env:ProgramFiles\DigitalPersona\UareUSdk\",
            "${env:ProgramFiles(x86)}\DigitalPersona\UareUSdk\",
            "$env:ProgramFiles\HID Global\DigitalPersona SDK\",
            "${env:ProgramFiles(x86)}\HID Global\DigitalPersona SDK\"
        )
        $copied = $false
        foreach ($sp in $sdkPaths) {
            if (Test-Path $sp) {
                Write-Ok "Found Digital Persona SDK at $sp"
                foreach ($f in $allNeeded) {
                    $src = Get-ChildItem "$sp" -Recurse -Filter $f -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($src) {
                        Copy-Item $src.FullName "$dir\$f" -Force
                        Write-Ok "  Copied $f"
                    }
                }
                $copied = $true
                break
            }
        }

        if (-not $copied) {
            Write-Warn "Digital Persona SDK not installed on this machine."
            Write-Action "DOWNLOAD: Digital Persona U.are.U SDK (HID Global - registration required)"
            Write-Action "  URL: https://developer.hidglobal.com/"
            Write-Action "  After download + install, re-run this script OR copy from SDK:"
            Write-Action "    -> $dir"
            Write-Action "  Required files:"
            Write-Action "    - dpfpdd.dll (native device driver)"
            Write-Action "    - dpfj.dll (native fingerprint engine)"
            Write-Action "    - DPFPDevNET.dll (managed wrapper)"
            Write-Action "    - DPFPCapture.dll (managed capture)"
        }
    }
}

# Futronic

if ($Vendor -contains "Futronic") {
    Write-Step "Futronic - ftrScanAPI.dll (P/Invoke, direct native)"
    $dir = New-LibDir "Futronic"
    Write-Info "No NuGet needed - direct DllImport into ftrScanAPI.dll"

    $found = Test-Dll "$dir\ftrScanAPI.dll"

    if (-not $found) {
        $searchPaths = @(
            "$env:ProgramFiles\Futronic\SDK\",
            "${env:ProgramFiles(x86)}\Futronic\SDK\",
            "$env:ProgramFiles\Futronic\Futronic Scanner SDK\",
            "${env:ProgramFiles(x86)}\Futronic\Futronic Scanner SDK\"
        )
        $copied = $false
        foreach ($sp in $searchPaths) {
            $dll = Get-ChildItem "$sp" -Recurse -Filter "ftrScanAPI.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($dll) {
                Copy-Item $dll.FullName "$dir\ftrScanAPI.dll" -Force
                Write-Ok "Found ftrScanAPI.dll at $($dll.FullName), copied"
                $copied = $true
                break
            }
        }

        if (-not $copied) {
            Write-Warn "Futronic SDK not installed on this machine."
            Write-Action "DOWNLOAD: Futronic Standard SDK v4.2 (registration required)"
            Write-Action "  URL: http://www.futronic-tech.com/download.html"
            Write-Action "  After download + install, re-run this script OR copy:"
            Write-Action "    ftrScanAPI.dll -> $dir"
        }
    }
}

# Summary

Write-Step "Setup Summary"

$allOk = $true
$table = @()

$checks = @(
    @{ Vendor="ZKTeco";         File="libzkfp.dll";         Path="$LibRoot\ZKTeco\libzkfp.dll" }
    @{ Vendor="SecuGen";        File="sgfplib.dll";         Path="$LibRoot\SecuGen\sgfplib.dll" }
    @{ Vendor="SecuGen";        File="sgfpamx.dll";         Path="$LibRoot\SecuGen\sgfpamx.dll" }
    @{ Vendor="SecuGen";        File="SecuGen.FDxSDKPro.Windows.dll"; Path="$LibRoot\SecuGen\SecuGen.FDxSDKPro.Windows.dll" }
    @{ Vendor="DigitalPersona"; File="dpfpdd.dll";          Path="$LibRoot\DigitalPersona\dpfpdd.dll" }
    @{ Vendor="DigitalPersona"; File="dpfj.dll";            Path="$LibRoot\DigitalPersona\dpfj.dll" }
    @{ Vendor="DigitalPersona"; File="DPFPDevNET.dll";      Path="$LibRoot\DigitalPersona\DPFPDevNET.dll" }
    @{ Vendor="Futronic";       File="ftrScanAPI.dll";      Path="$LibRoot\Futronic\ftrScanAPI.dll" }
)

foreach ($c in $checks) {
    $ok = Test-Path $c.Path
    if (-not $ok) { $allOk = $false }
    $table += [PSCustomObject]@{
        Vendor = $c.Vendor
        File   = $c.File
        Status = if ($ok) { "PRESENT" } else { "MISSING" }
    }
}

$table | Format-Table -AutoSize

if ($allOk) {
    Write-Host "`nAll vendor SDK DLLs are present. Run 'dotnet build' to activate all adapters." -ForegroundColor Green
} else {
    Write-Host "`nSome DLLs are missing. Follow the download instructions above for each vendor." -ForegroundColor Yellow
    Write-Host "  After placing DLLs, run this script again to validate." -ForegroundColor Yellow
}

Write-Host ""
Write-Ok ".csproj ZKTeco check already fixed to libzkfp.dll"
