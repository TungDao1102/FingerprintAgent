---
status: testing
phase: 03-resilience-runtime-reconfiguration
source: 03-01-SUMMARY.md, 03-02-SUMMARY.md, 03-03-SUMMARY.md
started: 2026-08-19T00:11:00Z
updated: 2026-08-19T00:17:00Z
---

## Current Test

number: 7
name: CaptureHandler returns 503 for SCANNER_NOT_CONNECTED
expected: |
  With mockMode=false and no real scanner connected:
  POST /api/capture returns HTTP 503 with JSON body:
    { errorCode: "SCANNER_NOT_CONNECTED", errorMessage: "...",
      vendorErrorCode: "...", timestamp: "ISO 8601" }
awaiting: user response

## Tests

### 1. Build verification — release build succeeds clean
expected: dotnet build -c Release produces 0 warnings, 0 errors
result: pass

### 2. /health JSON exposes inBackoff, backoffStep, status fields
expected: GET /health returns JSON with status, inBackoff (bool), backoffStep (int), deviceId, uptime fields
result: pass

### 3. /health returns 503 when scanner disconnected in max backoff
expected: With scanner disconnected and backoffStep=3, GET /health returns HTTP 503 with degraded status
result: pass

### 4. Config hot-reload via config.json edit (CORS)
expected: Edit config.json to change cors.mode from wildcard to allowlist; service continues running; subsequent OPTIONS request reflects new CORS policy (no service restart)
result: pass

### 5. Config hot-reload via config.json edit (Scanner priority)
expected: Edit config.json to reorder scanner.priority list; service continues; ScannerManager logs "priority updated" (D-09 active adapter preserved)
result: pass

### 6. Config reload with malformed JSON keeps old config
expected: Save invalid JSON to config.json; service logs error but does NOT crash; old config remains active; subsequent /health returns same deviceId
result: pass

### 7. CaptureHandler returns 503 for SCANNER_NOT_CONNECTED
expected: With mockMode=false and no real scanner, POST /api/capture returns 503 with errorCode=SCANNER_NOT_CONNECTED
result: [pending]

### 8. CaptureHandler returns 504 for CAPTURE_TIMEOUT
expected: With mockMode adapter that delays >10s, POST /api/capture returns 504 with errorCode=CAPTURE_TIMEOUT after ~10s
result: [pending]

### 9. CaptureHandler returns 500 for CAPTURE_FAILED
expected: With mockMode adapter that throws exception, POST /api/capture returns 500 with errorCode=CAPTURE_FAILED and errorMessage
result: [pending]

### 10. CaptureHandler returns 400 for INVALID_REQUEST
expected: POST /api/capture with missing required JSON fields returns 400 with errorCode=INVALID_REQUEST
result: [pending]

### 11. Error response JSON includes vendorErrorCode + ISO 8601 timestamp
expected: Failed capture response JSON contains data.vendorErrorCode (string) and data.timestamp (ISO 8601: YYYY-MM-DDTHH:MM:SS.fffffffZ)
result: [pending]

### 12. Exponential backoff sequence {10, 30, 60, 120}s
expected: After 3+ consecutive failures, backoffStep increments 1→2→3 (capped); BackoffDelaysSeconds = {10, 30, 60, 120}; verified via unit tests ScannerManagerTests.ExponentialBackoff.cs
result: [pending]

### 13. Backoff resets on successful capture
expected: After backoff engaged, a successful Scan() call resets backoffStep to 0; subsequent failure starts at step 1 again
result: [pending]

### 14. Health check timer fires every 30s and reads only IsConnected
expected: After ~30s of service running, log shows health-check warning (if disconnected); Timer does NOT call Initialize() or Scan() per D-17
result: [pending]

### 15. Real ZKTeco disconnect → backoff trigger (hardware)
expected: With real ZKTeco scanner connected, unplug USB during idle state; /health transitions to degraded; backoffStep increments to 1; next /api/capture returns 503
result: blocked
blocked_by: physical-device
reason: "Requires unplugging real ZKTeco mid-run; integration with hospital deployment"

### 16. Real backoff timer expiry (long-running observation)
expected: With scanner disconnected at step=3, observe backoffUntil timestamps: 10s, 30s, 60s, 120s intervals between retry attempts; total wait ~220s
result: blocked
blocked_by: time-bound
reason: "Requires sustained 4+ minute observation of backoff cycle with real scanner"

## Summary

total: 16
passed: 6
issues: 0
pending: 8
skipped: 0
blocked: 2

## Gaps

[none yet]
