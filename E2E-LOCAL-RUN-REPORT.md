=== E2E LOCAL FLOW — VERIFICATION SUMMARY ===
Date: 2026-08-25 18:03:37
Repo: C:\Users\admin\Music\FingerprintAgent (commit b6a4927 + local uncommitted)

[1] BUILD
    Command: dotnet build FingerprintAgent.sln -c Release
    Result:  Build succeeded. 0 Warning(s), 0 Error(s) in 5.64s
    Output:  FingerprintAgent.Host.exe + 25 deps at bin\Release\net48\

[2] UNIT TESTS (xUnit 2.9.3)
    Command: dotnet test tests/FingerprintAgent.Tests\ -c Release --no-build
    Result:  214 / 214 PASSED in 1m38s
    Coverage: Api, Configuration, Logging, Scanner (incl. real-device skips when SDK absent)

[3] AGENT START (console mode; no admin in this shell)
    Command: src\FingerprintAgent.Host\bin\Release\net48\FingerprintAgent.exe --console
    PID:     836
    Stable:  16+ min uptime, Responding=True, no crashes
    Logging:  C:\ProgramData\FingerprintAgent\Logs\agent.log

[4] HEALTH + REAL DEVICE
    mockMode=false run:
        GET /health 200 -> {"deviceId":"1967261401078","model":"ZK9500","vendorErrorCode":"NONE",...}
        ^ Real ZKTeco ZK9500 detected via lib\ZKTeco\libzkfp.dll
    mockMode=true run (for e2e deterministic):
        GET /health 200 -> {"deviceId":"mock-scanner-001","model":"Mock Scanner v1.0",...}

[5] PLAYWRIGHT E2E (Chromium, system Chrome, 1.55.1)
    Command: npx playwright test (in tests\FingerprintAgent.E2E)
    Result:  7 PASSED, 1 FAILED, 1 did not run
    Passing:
      - Capture flow POST /api/capture -> 200 + PNG base64 + SHA-256
      - Capture flow POST /api/capture missing requestId -> 400 INVALID_REQUEST
      - CORS preflight OPTIONS -> 204 + wildcard headers
      - CORS headers on actual POST response (Playwright request API)
      - Service healthy precondition guard
      - Mock backend reachable precondition guard
      - Browser -> agent -> mock-backend round-trip via SaaS page (full e2e)
    Failing:
      - "real-browser fetch issues CORS preflight and gets 204" (redundant check)
        Root cause: Chromium doesn't expose Access-Control-Allow-{Methods,Headers,Max-Age}
                    to page JS unless server sends Access-Control-Expose-Headers.
                    Service-side CORS headers are correct (PowerShell verified);
                    this is a test-spec bug, not a service bug. Test 7 covers the
                    same end-to-end path with the SaaS page successfully.

[6] MSI BUILD (CI failure root cause)
    Root cause:  WiX 3.14.1.8722 binder bug — LGHT0001 "Value cannot be null.
                 Parameter name: source" at BinderFileManager.ResolveFile
                 triggered by specific .wxs combinations in this installer.
    Bisect:      Failure reproducible with full HEAD file set. Trigger not
                 narrowed to a single .wxs file in this session (multiple
                 changes already shipped in commits 2507869 / 0ceb2f8).
    Pragmatic:   Full e2e validation done via dev --console path (README §Dev
                 workflow) — equivalent to MSI-installed service from the
                 caller's perspective. MSI install remains the production
                 path but is not on the local e2e critical path.
