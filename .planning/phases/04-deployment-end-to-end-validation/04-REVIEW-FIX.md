---
phase: 04
iteration: 3
fixes_applied: 1
fixes_skipped: 1
status: clean
---

# Phase 04 Review Fix Report — Final (Iteration 3)

**Fixed at:** 2026-08-20
**Source review:** [04-REVIEW.md](04-REVIEW.md) (iteration 2)
**Iteration:** 3 of 3 (FINAL)

**Summary:**
- Findings in scope: 2 (1 Warning, 1 Info — per `fix_scope: critical_warning`, only CR-*/BL-*/WR-* count; IN-01 was excluded by default but explicitly addressed per task brief)
- Fixed: 1 (IN-01 — concurrency guard tests added)
- Skipped: 1 (WR-01 — operator-action only, not code-fixable by agent)
- Atomic commit: 1 (`531d100`)

## Iteration Trajectory

| Iteration | Critical | Warning | Info | Status |
|-----------|----------|---------|------|--------|
| 1 (initial) | 7 → 0 fixed | 11 → 0 fixed | (skipped) | ✓ all_fixed |
| 2 (re-review) | 0 | 1 carried-over (WARN-05 → WR-01) | 1 (concurrency tests missing) | has_findings |
| 3 (this) | 0 | 1 (deferred to operator) | 1 fixed | **clean** |

## Iter 3 Changes

### IN-01 — No dedicated unit tests for CR-03/05/06 concurrency guards → **FIXED**

**Commit:** `531d100`
**Files:**
- `tests/FingerprintAgent.Tests/Update/UpdateCheckServiceTests.cs` (+180 LOC, 4 new tests)
- `tests/FingerprintAgent.Tests/Update/MockHttpMessageHandler.cs` (+28 LOC, new `QueueResponseTask` overload)
- `src/FingerprintAgent/Update/UpdateCheckService.cs` (+11 LOC, new `SetStateForTest` test seam)

**Tests added:**

| Test | Guards | What it asserts |
|---|---|---|
| `TriggerImmediateCheck_WhenAlreadyChecking_SkipsSecondHttpCall` | CR-03 | Timer + 3× TriggerImmediateCheck while HTTP is blocked → only 1 HTTP call; CR-03 in-flight skip works |
| `DownloadAndInstallForTest_WhenInstallFails_FinalStateIsStopped` | CR-05 | Install fails → state ends `Stopped` (not stale `Running`); `update.enabled=false` written to config.json |
| `ApplyConfig_DuringDownload_DoesNotStopTimer` | CR-06 | `ApplyConfig(false)` while `Downloading`/`Installing` → state preserved, no `Stop()` call |
| `ApplyConfig_DuringChecking_CallsStop` | CR-06 boundary | `ApplyConfig(false)` while `Checking` (not in-flight) → `Stop()` IS called (operator intent applies promptly) |

**Test seams added (minimal, opt-in):**

1. `MockHttpMessageHandler.QueueResponseTask(matcher, task)` — Refactored internal response queue from `(matcher, HttpResponseMessage)` to `(matcher, Func<Task<HttpResponseMessage>>)`. Backward-compatible: existing `QueueResponse(...)` calls wrap `HttpResponseMessage` in `() => Task.FromResult(response)`. New overload accepts a `Task<HttpResponseMessage>` directly, letting tests keep HTTP in flight until a `TaskCompletionSource` is manually released.

2. `UpdateCheckService.SetStateForTest(state)` — Single-line internal method that sets `_state` under `_lock`. Follows existing test-seam pattern (`InstallInstallerOverride`, `SetProgramDataConfigPathForTest`). Lets CR-06 tests assert the in-flight deferral boundary without paying the 10-second `PreInstallDelay`.

Both seams are additive — production behavior is unchanged. They only activate when tests explicitly call them.

**Coverage delta:** UpdateCheckServiceTests went from 13 tests → 17 tests (+4 concurrency guards). Full suite: 168 pass → 172 pass (+4 new).

## Skipped Items

### WR-01 — WiX 3.14.1 SHA256 placeholder — **RESOLVED 2026-08-20 (operator action)**

**File:** `.github/workflows/release.yml:78` and `.github/workflows/e2e.yml:65`
**Severity:** Warning (carry-over from WARN-05; flagged as WR-01 in iteration 2)
**Resolution:** Operator (this session) downloaded `wix314-binaries.zip` from https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/ and computed the SHA256 hash via `(Get-FileHash -Algorithm SHA256 wix314-binaries.zip).Hash`. Pinned value:

```
6ac824e1642d6f7277d0ed7ea09411a508f6116ba6fae0aa5f2c7daa2ff43d31
```

**Commit:** (this commit — `fix(04): wire real wix3141rtm SHA256 hash in release.yml + e2e.yml`)
**Files changed:**
- `.github/workflows/release.yml` — placeholder → real hash (+ provenance comment)
- `.github/workflows/e2e.yml` — placeholder → real hash (+ provenance comment)

**Effect:** SHA256 verification is now ACTIVE. Any future WiX 3.x patch release that changes the binary hash will fail the build (`Write-Error` exits 1 at line 85/72). Supply-chain attack via DNS hijack or compromised GitHub release is now detectable and blocked.

**Verification command (for future upgrades):**
```powershell
Invoke-WebRequest https://github.com/wixtoolset/wix3/releases/download/<NEW_TAG>/wix314-binaries.zip -OutFile wix.zip
(Get-FileHash -Algorithm SHA256 wix.zip).Hash
```

## Final Verification

- **`dotnet build -c Release`** — **0 warnings / 0 errors** (all 4 projects compile clean)
- **`dotnet test -c Release --no-build`** — **172 passed, 6 failed** (6 pre-existing ZK9500 hardware-dependent: `ScannerManagerProbeIntegrationTests` × 5 + `ZkSdkProbe_Run` × 1, identical to iteration 1 baseline; no regressions introduced by iter-3 commit)
- **Total atomic commits in Phase 4:** 11 (10 from iteration 1 + 1 from iteration 3)

## Conclusion

**PASS — Phase 4 ready for v1.0 tag.** All Critical (7) and Warning (11) findings resolved across iterations 1–3, plus the single operator-action item (WR-01 WiX SHA256 pin) wired with the verified hash. Zero remaining blockers.

**Status:** `clean`

---

_Fixed: 2026-08-20_
_Fixer: the agent (gsd-code-fixer) + operator (WR-01 hash pin)_
_Iterations: 1–3 (final)_
_Commits: `2530d3d`, `970ffbd`, `a4fa170`, `733d5b0`, `e873ab8`, `37e7c4a`, `89e9264`, `b139486`, `ae7ab28`, `9400cdc`, `531d100` + operator commit for WR-01_
