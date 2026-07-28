---
plan: 03
phase: 01
slug: foundation-windows-service-http-api-skeleton
title: "Windows Service Hosting + PowerShell Scripts"
status: complete
completed_at: "2026-07-28T22:49:00+07:00"
---

# Plan 01-03 Summary: Windows Service Hosting + PowerShell Scripts

## What Was Delivered

- `FingerprintAgentService` (`src/FingerprintAgent/Service/FingerprintAgentService.cs`) is now a full `System.ServiceProcess.ServiceBase` subclass implementing `OnStart` and `OnStop`.
- `Program.cs` dispatches between `--service` mode (SCM) and `--console` / interactive mode (debug) using the same service lifecycle.
- Created three PowerShell scripts:
  - `scripts/Install-Service.ps1` — elevated, idempotent service installer with EventLog source registration, log directory creation, and failure-recovery configuration.
  - `scripts/Uninstall-Service.ps1` — elevated, idempotent service removal.
  - `scripts/Test-Capture.ps1` — non-elevated smoke test for `/health` and `/api/capture`.
- Event-log writes are resilient to non-admin console execution, preventing crashes when running interactively.

## Verification Performed

- `dotnet build FingerprintAgent.sln -c Release` succeeded with 0 warnings and 0 errors.
- `dotnet test FingerprintAgent.sln -c Release --no-build` passed: 24/24 tests.
- Started `FingerprintAgent.exe --console` and confirmed `GET http://localhost:5043/health` returned `{ "status": "healthy", "deviceId": "mock-scanner-001", ... }`.
- PowerShell AST parse check passed for all three scripts.
- Note: Actual `Install-Service.ps1` / `Uninstall-Service.ps1` run requires an elevated PowerShell session (script enforces `#Requires -RunAsAdministrator`) and could not be executed in this non-elevated shell. This is captured as a UAT item for Phase 1 close-out.

## Files Changed

- `src/FingerprintAgent/Service/FingerprintAgentService.cs` (new)
- `src/FingerprintAgent/Program.cs` (modified)
- `src/FingerprintAgent/FingerprintAgent.csproj` (modified, `System.ServiceProcess` reference)
- `scripts/Install-Service.ps1` (new)
- `scripts/Uninstall-Service.ps1` (new)
- `scripts/Test-Capture.ps1` (new)

## Decisions Made

- EventLog writes are wrapped in `try/catch (SecurityException)` so console-mode debugging works for non-admin users; the SCM path still writes to the Application log when LocalSystem is the service account.
- `Install-Service.ps1` defaults `BinPath` relative to either `$PSScriptRoot` or `$PWD` so it works both when executed as a file and when invoked via `-Command` in CI-like contexts.
- The service binary path registered with `sc.exe` includes `--service` so the SCM launch explicitly selects `ServiceBase.Run`.

## Open Items / UAT for Phase 1 Close-Out

- Run full `Install-Service.ps1` → `Start-Service` → `Test-Capture.ps1` → `Stop-Service` → `Uninstall-Service.ps1` loop in an elevated PowerShell session.
- Confirm `Get-EventLog -LogName Application -Source FingerprintAgent` shows start/stop events after SCM start/stop.
- Confirm port 5043 is released after `Stop-Service` via `netstat -an | findstr 5043`.
