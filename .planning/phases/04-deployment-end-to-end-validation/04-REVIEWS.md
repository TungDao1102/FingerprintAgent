# Phase 4 Cross-AI Review — Cycle 1

**Reviewer:** Independent cross-AI reviewer (MiniMax-M3, fresh context)
**Cycle:** 1 of max 3
**Date:** 2026-08-19

## Summary

**Not converged.** Four material HIGHs and several MEDIUMs found that will either break at execution or compromise Phase 4 acceptance. The two most damaging are (a) `Id="*"` auto-generated ProductCode combined with the documented MajorUpgrade in-place flow — these are mutually exclusive, so either the smoke test passes and the upgrade is silently a fresh install, or the upgrade is genuinely smooth and the ProductCode handling is wrong; and (b) the merge logic is duplicated between `ConfigLoader` (Plan 04-01) and `CustomAction SeedProgramDataConfig` (Plan 04-02) — the source-link decision forces an inevitable drift on the first ConfigMerger evolution. ROADMAP Phase 4 SC #2 also contradicts D-09 and Plan 04-02 (silent VC++ install vs detect-only-with-error-dialog), so Phase 4 cannot be marked complete against the current ROADMAP as written.

## Findings

### HIGH severity (must fix before execution)

**H1. ProductCode `Id="*"` is incompatible with the smooth in-place upgrade the plan validates**
- **Plan/task:** 04-02 task 8 (`<Product Id="*" ...>`) + task 11 step 16 (upgrade smoke test) + D-03 / D-40.
- **Rationale:** WiX `Id="*"` generates a fresh ProductCode GUID on every build. `<MajorUpgrade AllowSameVersionUpgrades="yes">` only recognizes an "upgrade" by matching UpgradeCode + detecting a previous install of the same product lineage. With a per-build ProductCode, a second build installed over the first is treated as a **fresh install** (different ProductCode ⇒ new product), so the upgrade smoke test either (i) silently re-installs over itself, losing ProgramData config and registry state — contradicting "smooth in-place upgrade" — or (ii) fails as "another version already installed" because MajorUpgrade compares ProductCode+Version. The plan does not specify how the CI pipeline pins a stable ProductCode for a given Version (only `Version=$(Version)` is defined; ProductCode handling is absent). Either the `<Product>` element needs a fixed GUID per release line (e.g., generated once and committed, or derived from `${GITHUB_REF_NAME}`), or `MajorUpgrade` must be paired with `UpgradeCode` only and the smoke-test expectation must be relaxed.
- **Suggested fix:** In `installer/FingerprintAgent.Installer.wxs`, replace `<Product Id="*" ...>` with `<Product Id="$(var.ProductCode)" ... UpgradeCode="{FIXED-UPGRADE-GUID}" ...>` and define `<ProductCode>` from `${GITHUB_REF_NAME}` in the `.wixproj` (or in `release.yml` as `-p:ProductCode=...`). Confirm in task 11 step 16 that the upgrade smoke test uses a true ProductVersion bump (e.g., 1.0.0 → 1.0.1) with the SAME ProductCode and UpgradeCode — not a random rebuild.

**H2. CI cleanup uses `FastPackageReference` which is not the MSI ProductCode**
- **Plan/task:** 04-04 task 7 step 13–14 (cleanup with `Get-Package -Name 'Fingerprint Agent' | Select-Object -First 1).FastPackageReference`).
- **Rationale:** Verified against PowerShell `PackageProvider` semantics: `(Get-Package).FastPackageReference` returns the package provider's internal reference (registry key path for ProgramsAndFeatures provider), **not** the MSI `{GUID}` ProductCode. `msiexec /x` requires the actual MSI ProductCode GUID (the `{XXXXXXXX-XXXX-...}` form stored under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`). The current line will either pass an empty string to `msiexec /x` (silent no-op, MSI stays installed) or throw. This was the very reason the previous version of this step used `Get-WmiObject Win32_Product` (correct, but slow) — replacing it with the wrong field silently breaks cleanup. If cleanup fails, the MSI remains installed on the runner and **subsequent E2E workflow runs fail** (service already registered, port already in use), turning a 10–15 min workflow into a bricked runner.
- **Suggested fix:** Use `Get-WmiObject` for the cleanup step despite the speed cost (it runs once at teardown, not in the hot path). Pattern: `$productCode = (Get-WmiObject -Class Win32_Product -Filter "Name='Fingerprint Agent'" -ErrorAction SilentlyContinue).IdentifyingNumber`. If that fails, fall back to: `reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" /s /f "Fingerprint Agent" | Select-String -Pattern '\{[A-F0-9-]{36}\}'`. Wrap in `try/catch`; on hard failure, do not `throw` (workflow step still records the failure but does not wedge the runner).

**H3. CustomAction merge logic duplicates ConfigLoader merge logic — drift is guaranteed**
- **Plan/task:** 04-02 task 4 (CustomAction links `ConfigMerger.cs` source AND duplicates the ProgramData-vs-template + merge.log write logic) vs. 04-01 task 3 (ConfigLoader does the same merge + merge.log write inline).
- **Rationale:** Plan 04-02 task 4's `<Compile Include="..\FingerprintAgent\Configuration\ConfigMerger.cs" Link="..." />` compiles `ConfigMerger.cs` into the CustomAction DLL, but the wrapping logic — read template JSON, parse JObject, call ConfigMerger.Merge, write back, write merge.log if addedKeys.Count > 0 — is **written a second time inside the CustomAction**. Plan 04-01 task 3 has the same five-step sequence written a third time inside `ConfigLoader.Load()`. The ConfigMerger class is shared; the wrapper logic around it is not. The first time ConfigMerger grows (e.g., to handle a new section type or to log via AgentLogger), the agent's ConfigLoader picks it up immediately, but the CustomAction only picks it up on the next MSI build — and the next MSI build might forget to re-implement the wrapping changes. There is no test that catches this drift because the CustomAction merge path has no unit tests (Plan 04-02 has no test task; see M5).
- **Suggested fix:** Extract the wrapping logic into a static helper in `ConfigMerger.cs` itself (e.g., `ConfigMerger.LoadAndMerge(string programDataPath, string templatePath) : (AgentConfig config, IReadOnlyList<string> addedKeys)`), then `ConfigLoader.Load()` and `SeedProgramDataConfig` both call this helper. Add `ConfigMerger.LoadAndMergeTests` covering: first install seed, upgrade with merge, merge failure recovery, merge.log content. With the helper shared, a single source of truth covers both call sites.

**H4. ROADMAP Phase 4 SC #2 is unreachable as written — silent VC++ install contradicts D-09**
- **Plan/task:** 04-04 task 11 acceptance_criteria ("Final Phase 4 status: all 4 plans executed, all success criteria from ROADMAP.md met") vs. ROADMAP.md §Phase 4 SC #2 ("MSI cài đặt service, thư mục log, và VC++ redist x86 silently nếu thiếu") vs. 04-CONTEXT.md D-09 ("Detect only — error message if missing (no bundling)") vs. 04-02 task 2 (returns Failure → rollback, no install).
- **Rationale:** SC #2 demands **silent install of VC++ if missing**. D-09 (and Plan 04-02 task 2 implementation, and the `Anti-Patterns` row "Do NOT use a Burn bootstrapper — D-09 reject bundling") deliberately do **not** bundle/install VC++. The two cannot both be true. Phase 4 cannot be marked "complete against ROADMAP success criteria" without either (a) relaxing SC #2 to "MSI detects missing VC++ and shows a clear Vietnamese error; operator installs redist manually" — which matches D-09 + Plan 04-02, or (b) adding VC++ redistribution back, which contradicts D-09. Either ROADMAP or D-09 must change; the executor will not know which.
- **Suggested fix:** Update `ROADMAP.md` §Phase 4 SC #2 to match D-09: e.g., "MSI cài đặt service, thư mục log, và **phát hiện** VC++ redist x86 — nếu thiếu, hiển thị lỗi tiếng Việt với link tải". This is the correct behavior per the locked decisions and matches Plan 04-02 task 2 exactly. No code changes needed.

### MEDIUM severity (actionable — must be incorporated into PLAN.md or explicitly deferred)

**M1. Plan 04-03 task 5 leaves the runtime enable/disable path underspecified**
- **Plan/task:** 04-03 task 5 (acceptance_criteria: "if new `update.Enabled == true` AND service was running with update disabled, start the Timer; if new `update.Enabled == false` AND Timer was running, stop it") + task 6 (described as "Most likely no code change needed — Plan 04-01 already covers it. Mark complete after verification").
- **Rationale:** `AgentConfig.Update.Enabled` defaults to `false` (D-14). If the operator flips it via `ConfigFileWatcher` reload, `OnConfigReloaded` (FingerprintAgentService.cs:228) does NOT touch `UpdateCheckService` — it only updates CORS and Scanner priority. To make the "operator opt-in via config edit" path work, the plan needs to either (a) keep `UpdateCheckService` instantiated when `Enabled=false` and have the Timer Start/Stop based on the flag, OR (b) instantiate/destroy `UpdateCheckService` on each config reload (wasteful, fragile). The plan describes (a) in the task 5 description but acceptance_criteria only says "Add field, in OnStart try-catch, in OnStop dispose" — the reload-driven Start/Stop is NOT an acceptance criterion. The plan also says "task 6 is verification" but task 6 is gated on "Most likely no code change needed" — there is no concrete acceptance criterion that confirms propagation works. An executor following the acceptance_criteria literally will skip the runtime propagation path.
- **Suggested fix (PLAN.md location: 04-03 task 5 acceptance_criteria):** Add explicit acceptance: "OnConfigReloaded handler MUST check newConfig.Update.Enabled vs. current state; if transitioning false→true, call `_updateCheckService?.Start()`; if true→false, call `_updateCheckService?.Stop()` while keeping the instance alive for future Start calls. Add a unit test `UpdateCheckService_StartStop_TogglesBasedOnEnabledFlag`." Drop task 6's "verification only" framing or convert it to a "write a propagation test" task.

**M2. Plan 04-01 task 3 merge-failure recovery is vague and contradicts the existing `LoadFromDirectory` contract**
- **Plan/task:** 04-01 task 3 acceptance_criteria step 4 + description ("On merge failure, load the existing ProgramData config without modification") vs. the existing `LoadFromDirectory` (ConfigLoader.cs:42-60) which **throws** FormatException on bad JSON.
- **Rationale:** The new `Load()` is supposed to "preserve Phase 3 D-08 semantics: bad config keeps old config, logs error, does NOT throw" per the acceptance_criteria. But test 9-5 (`Load_BadProgramDataJson_ThrowsFormatException`) expects `FormatException` to propagate. If the merge step throws AND the subsequent `LoadFromFile(programDataConfigPath)` ALSO throws, the service crashes — contradicting D-08. The plan needs explicit ordering: which failure wins? Does merge failure swallow, or does ProgramData-read failure swallow? The current text is silent.
- **Suggested fix (PLAN.md location: 04-01 task 3 acceptance_criteria):** Spell out the precedence: "Merge failure (template unreadable / JObject.Parse on template fails) → log Warn, SKIP merge, fall through to `LoadFromFile(programDataConfigPath)`. If ProgramData config is ALSO bad, THEN propagate FormatException (existing `LoadFromDirectory` behavior). Update test 9-5 to assert this exact sequence (merge failure → ProgramData read failure → FormatException propagates; merge failure alone → success with unmolested ProgramData)."

**M3. Plan 04-02 task 11 smoke test does not show how the uninstaller gets the ProductCode**
- **Plan/task:** 04-02 task 11 step 8 (`msiexec /x {ProductCode-from-step-1} /qn /l*v uninstall.log`).
- **Rationale:** Step 1 uses `msiexec /i ... /qn` which does not print the ProductCode. The plan assumes the operator can recover it from "step 1" but doesn't show how. For an automated smoke test, the executor needs a deterministic command (e.g., `msiexec /x $(grep ProductCode build.log) /qn` won't work). This is the same defect as H2 in a different context — the ProductCode recovery pattern needs to be specified.
- **Suggested fix (PLAN.md location: 04-02 task 11 step 8):** Replace with explicit: `$productCode = (Get-WmiObject -Class Win32_Product -Filter "Name='Fingerprint Agent'" -ErrorAction SilentlyContinue).IdentifyingNumber; msiexec /x $productCode /qn /l*v uninstall.log`. Or use `msiexec /x FingerprintAgent-Setup.msi` (uninstall-by-source-msi, accepts both `*.msi` and `{GUID}`).

**M4. Plan 04-02 task 11 step 16 upgrade smoke test will fail unless H1 is fixed**
- **Plan/task:** 04-02 task 11 step 16 ("Bump version in csproj, rebuild MSI, install → MajorUpgrade smooth-in-place upgrade").
- **Rationale:** Direct consequence of H1. Even if H1 is fixed, the plan should add explicit ProductVersion bumping (1.0.0 → 1.0.1) AND a note that ProductCode must NOT change. Also: between step 1 (fresh install) and step 16 (upgrade), the operator must wait for the agent to fully stop (msiexec has finished), which takes ~5s for graceful service stop. Add a `Start-Sleep 5` (or equivalent) before the second `msiexec /i`.
- **Suggested fix (PLAN.md location: 04-02 task 11 step 16):** "Bump ONLY `<Version>` in `src/FingerprintAgent.Host/FingerprintAgent.Host.csproj` (e.g., 1.0.0 → 1.0.1). Rebuild MSI. Verify ProductCode is preserved across rebuilds (CI passes `-p:ProductCode=` from `${GITHUB_REF_NAME#v}`). Wait 5s for previous MSI's service to stop. Run `msiexec /i installer\bin\Release\FingerprintAgent-Setup.msi /qn`. Expect: `EventLog` shows exactly ONE stop+start cycle, `C:\ProgramData\FingerprintAgent\config.json` is preserved with user-edited values intact, `merge.log` shows the diff."

**M5. Plan 04-02 has zero test coverage for CustomAction code**
- **Plan/task:** 04-02 (no test task; task 11 is a manual smoke test only) vs. Plan 04-01 (16 tests), Plan 04-03 (12 tests), Plan 04-04 (3+ E2E specs).
- **Rationale:** The CustomAction DLL contains four non-trivial functions: `CheckVcRedist` (registry probing with fail-open), `ProbeHealthAfterInstall` (HTTP probe with 503-vs-failure routing), `SeedProgramDataConfig` (ConfigMerger wrapper), `DetectInstallType` (WiX property inspector). Each has paths that will only fire under specific conditions (VC++ missing, scanner disconnected, malformed ProgramData JSON). Without unit tests, regressions in these functions only surface at install time on a target machine — too late for CI. The ConfigMerger share via H3 partly addresses `SeedProgramDataConfig` but not the other three.
- **Suggested fix (PLAN.md location: 04-02 new task 04-02-12):** Add `tests/FingerprintAgent.Tests/Installer/CustomActionUnitTests.cs` (or similar) with at minimum: `CheckVcRedist_WhenRegistryHasInstalled1_ReturnsSuccess` (mocked `Registry.LocalMachine` via InternalsVisibleTo + testable wrapper), `CheckVcRedist_WhenNoKey_ReturnsFailure`, `CheckVcRedist_WhenRegistryThrows_ReturnsSuccess` (fail-open), `ProbeHealth_WhenHttp200_ReturnsSuccess`, `ProbeHealth_WhenHttp503_ReturnsSuccess` (treated as scanner-not-detected per D-38), `ProbeHealth_WhenTimeout_ReturnsFailure`, `DetectInstallType_WhenFreshInstall_SetsFreshProperty`, `DetectInstallType_WhenUpgrade_SetsUpgradeProperty`. Each test runs under xUnit in milliseconds.

**M6. Plan 04-02 task 7 Designer.cs generation is fragile on SDK-style projects**
- **Plan/task:** 04-02 task 7 acceptance_criteria ("Designer file `Properties/VietnameseStrings.Designer.cs` is auto-generated by adding `<PackageReference Include="Microsoft.Extensions.ResX.SourceGenerator" Version="..." />`").
- **Rationale:** `Microsoft.Extensions.ResX.SourceGenerator` requires the .resx to be declared with `<EmbeddedResource Update="Properties\VietnameseStrings.resx" GenerateSource="Properties\VietnameseStrings.Designer.cs" />` AND for the project to be SDK-style. Plan 04-02 task 1 already specifies SDK-style. But the generator emits warnings (`RSG010`/`RSG020`) on non-conventional .resx structures and emits the strongly-typed accessor class only if `internal`/`public` access modifier matches the project. There is no test that the accessor `VietnameseStrings.VcRedistMissingTitle` resolves at build time — if the generator silently fails, the build fails late at CA DLL compile time with `CS0103: VietnameseStrings does not exist`.
- **Suggested fix (PLAN.md location: 04-02 task 7 acceptance_criteria):** Add explicit MSBuild wiring: `<ItemGroup><EmbeddedResource Update="Properties\VietnameseStrings.resx"><Generator>MSBuild:_GenerateVSToolsResxSource</Generator></EmbeddedResource></ItemGroup>` and pin the source-generator package to a specific version (e.g., `8.0.0` to match other `Microsoft.Extensions.*` pins in STACK.md). Add an acceptance criterion: "Build must emit zero `RSG*` warnings about `VietnameseStrings.resx`; if warnings appear, fix resx structure (Root element `<root>` not `<resources>`)."

**M7. Plan 04-03 task 1 wording allows socket-exhaustion regression**
- **Plan/task:** 04-03 task 1 acceptance_criteria ("`new HttpClient()` internally for each call").
- **Rationale:** The phrase "for each call" is ambiguous. If the implementer reads this as "for each HTTP request to api.github.com", the service will leak sockets under sustained load (GitHub calls happen every 6h, but a chatty implementation would also leak after each fire). Per CONVENTIONS.md "Static singleton teardown" pattern and Phase 3 health-check Timer pattern, the intended behavior is ONE HttpClient per UpdateCheckService instance. The wording must be unambiguous.
- **Suggested fix (PLAN.md location: 04-03 task 1 acceptance_criteria):** Change to: "Constructs `new HttpClient()` ONCE in the constructor and reuses it for the lifetime of the service (release in Dispose). NO per-call allocation — HttpClient is intended for reuse; per-call `new HttpClient()` causes socket exhaustion under sustained load."

**M8. Plan 04-03 task 7 introduces two testability seams for one class — over-engineered vs. AGENTS.md "no DI" rule**
- **Plan/task:** 04-03 task 7 acceptance_criteria ("MockHttpMessageHandler for canned HTTP responses" + description ("inject `IUpdateInstaller` interface")).
- **Rationale:** Two seams: HttpMessageHandler (for HTTP responses) and IUpdateInstaller (for msiexec). The IUpdateInstaller seam is justified for msiexec injection, but the entire class can be tested with one seam — `internal UpdateCheckService(AgentConfig config, AgentLogger logger, HttpMessageHandler handler, IUpdateInstaller installer)` — or even simpler, by making `DownloadAndInstallAsync` virtual and overriding in tests (consistent with `MockScannerAdapterWithSettableProperties` pattern used throughout the codebase). AGENTS.md says "No DI container" but allows direct `new` — the seam count should match the testing need, not invent a separate interface per dependency.
- **Suggested fix (PLAN.md location: 04-03 task 7 acceptance_criteria):** Consolidate: "Constructor overload `internal UpdateCheckService(AgentConfig config, AgentLogger logger, HttpMessageHandler handler, string programDataConfigPathForTest)` — handler is reused for both GitHub API and asset download; programDataConfigPath parameterizes the config.json path for tests. IUpdateInstaller interface is NOT introduced — the seam is the HttpMessageHandler + a `protected virtual` method on DownloadAndInstallAsync for the msiexec invocation, override in `MockableUpdateCheckService : UpdateCheckService` for tests that need to assert msiexec exit code behavior."

**M9. Plan 04-03 task 3 partial-JSON rewrite disables update on success path too**
- **Plan/task:** 04-03 task 3 acceptance_criteria ("On failure: disable update.enabled in config.json") — note the test 10 verifies config.json update on msiexec non-zero.
- **Rationale:** Per acceptance_criteria, ONLY msiexec exit code != 0 OR download exception triggers the disable. Test 10 verifies the disable on exit code 1603. But there's no test for the case where msiexec returns 0 (success) — the implementation should NOT disable update. The plan does not explicitly state "success path does NOT touch config.json", leaving it implicit. An executor could legitimately add "always write update.enabled = true on success to confirm install" — wrong but not contradicted.
- **Suggested fix (PLAN.md location: 04-03 task 3 acceptance_criteria):** Add explicit: "Success path (msiexec exit code 0): DO NOT touch config.json. The `Environment.Exit(0)` happens AFTER msiexec exits — config is left in its current state (operator's `update.enabled` value preserved). Test: `DownloadAndInstallAsync_MsiexecExit0_LeavesConfigUnchanged`."

**M10. Plan 04-04 task 7 step 11 hardcodes port 8080 for mock backend — CI flakiness risk**
- **Plan/task:** 04-04 task 2 (`baseURL: 'http://127.0.0.1:8080'`) + task 4 (`startMockBackend(port: number = 8080)`) + task 7 (CI workflow runs both agent and mock backend).
- **Rationale:** Port 8080 is a well-known dev port. On `windows-latest` GitHub-hosted runners, port 8080 may be in use by another concurrent workflow (e.g., IIS, Visual Studio, another test job). Playwright's `webServer` config retries on the same port by default and will eventually fail. The agent uses 5043 (also somewhat common), and Playwright tests against both.
- **Suggested fix (PLAN.md location: 04-04 task 2 acceptance_criteria):** Use a dynamic port: mock backend listens on port 0 (let OS assign), write the assigned port to a file (`./test-runtime/mock-port.txt`), Playwright config reads it via `process.env.MOCK_BACKEND_PORT || 8080`. CI sets `MOCK_BACKEND_PORT` from the file in the test step. This matches the existing C# test pattern (`TcpListener` to find free port — see TESTING.md "Random Free Port Discovery").

**M11. Plan 04-02 task 8 EventLog source uninstall not explicitly specified**
- **Plan/task:** 04-02 task 8 description mentions `<util:EventSource Name="FingerprintAgent" />` for install + "Removal during uninstall" referenced in must_haves #12, but no `<RemoveRegistryKey>` or `<util:EventSource>` removal directive in acceptance_criteria.
- **Rationale:** D-31 requires "Delete EventLog source FingerprintAgent on uninstall". The `<util:EventSource>` element creates the source but does not remove it on uninstall — explicit `<RemoveRegistryKey>` (or `<util:EventSource RemoveOnUninstall="yes">`) is required. The acceptance_criteria lists components but doesn't enumerate uninstall-time registry cleanup, and D-31 will silently fail (EventLog source persists across uninstalls).
- **Suggested fix (PLAN.md location: 04-02 task 8 acceptance_criteria + must_haves):** Add explicit: "`installer/Components/UninstallBehavior.wxs` includes `<util:EventSource Name=\"FingerprintAgent\" RemoveOnUninstall=\"yes\" />` (or equivalent `<RemoveRegistryKey>` chain) to satisfy D-31. After uninstall, `reg query \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\EventLog\\Application\\FingerprintAgent\"` returns ERROR (key not found)."

### LOW severity (actionable — same as MEDIUM but lower urgency)

**L1. Plan 04-02 task 10 — optional `softprops/action-gh-release@v2` left as commented-out**
- **Plan/task:** 04-02 task 10 acceptance_criteria step 8.
- **Rationale:** Default behavior (artifact-only, no auto-release attach) means operator MUST manually attach the MSI to each GitHub Release. That's an extra step for each release. Either accept the friction or uncomment. Not a blocker.
- **Suggested fix:** Decide once: enable auto-attach by default OR document in DEPLOYMENT.md "after CI green, attach MSI manually". No code change to PLAN.md needed if the choice is documented.

**L2. Plan 04-03 task 1 — GitHub API version header `2026-03-10` is a future date**
- **Plan/task:** 04-03 task 1 acceptance_criteria (`X-GitHub-Api-Version: 2026-03-10`).
- **Rationale:** GitHub API version header is `YYYY-MM-DD`. Current API versions are around `2022-11-28`. Using a future date may be accepted by GitHub (they likely treat unknown versions as "newest stable") but is unusual. Use the documented stable version.
- **Suggested fix (PLAN.md location: 04-03 task 1 acceptance_criteria):** Replace `X-GitHub-Api-Version: 2026-03-10` with `X-GitHub-Api-Version: 2022-11-28` (the current stable GitHub REST API version per docs.github.com).

**L3. Plan 04-02 task 8 `<Property Id="WixUILicenseRtf" Value="" />`**
- **Plan/task:** 04-02 task 8.
- **Rationale:** `WixUI_Minimal` doesn't show a license dialog by default — the property is a no-op. Including it suggests there was one to suppress.
- **Suggested fix:** Remove the line from acceptance_criteria; not harmful but slightly misleading.

**L4. Plan 04-04 task 6 test 3 has two contradictory versions in acceptance_criteria**
- **Plan/task:** 04-04 task 6 test 3.
- **Rationale:** First version says `await page.goto('http://127.0.0.1:5043/health')` "should NOT navigate successfully". Second says "Better: use `request.get('http://127.0.0.1:5043/health')`". Both are in the acceptance_criteria — the executor doesn't know which to implement.
- **Suggested fix (PLAN.md location: 04-04 task 6 test 3):** Keep only the `request.get` version; remove the page.goto attempt.

**L5. Plan 04-04 task 1 devDependencies pins `@types/node: "^22"` (caret)**
- **Plan/task:** 04-04 task 1 acceptance_criteria.
- **Rationale:** Project convention per STACK.md "All dependencies use exact-version pinning". Caret range allows drift; for reproducibility, pin exactly.
- **Suggested fix (PLAN.md location: 04-04 task 1):** Change to `@types/node: "22.x"` or pinned `"22.10.0"`.

**L6. Plan 04-04 task 9 — README "Top of file: badges (Release version, License, Build status) — minimal, no CI badge until CI exists in repo"**
- **Plan/task:** 04-04 task 9 acceptance_criteria.
- **Rationale:** Plan 04-02 task 10 introduces `.github/workflows/release.yml` — CI exists by the time README is written. The "no CI badge until CI exists" is now stale.
- **Suggested fix (PLAN.md location: 04-04 task 9):** Update to "Release version badge (links to latest GitHub release), License badge, Build status badge from `release.yml` workflow".

### INFO (observations, not actionable)

**I1. Plan 04-01 task 7 — `config.json` and `config.template.json` are identical at the start**
- Both files are full copies. The split (template in install dir, runtime in ProgramData) only makes sense after the first merge; until then they're equivalent. This is by design and documented — no action needed.

**I2. Plan 04-02 task 4 references `INSTALLFOLDER` property — well-known WiX idiom**
- The CustomAction resolves the install path via `session["INSTALLFOLDER"]`, which is the standard WiX pattern. Verified against wixtoolset.org docs. No action.

**I3. Plan 04-04 task 10 (delete `docs/`) is correctly gated on a grep check**
- The acceptance_criteria includes "Verify that no in-repo references link to `docs/*.md` (grep the repo for `docs/` references)". This prevents orphan-reference breakage. Good practice; no action.

**I4. Plan 04-03 task 8 manual smoke step 2 patches `BASE_INTERVAL_HOURS` constant**
- The plan explicitly says "Revert before commit". This is a recognized dev-only workaround for testing without waiting 6h. The acceptance_criteria explicitly notes this in the comment. No action needed; the discipline is documented.

**I5. Plan 04-02 task 11 smoke test step 17 mentions `Get-WmiObject Win32_Product`**
- Pre-cycle-0 fix replaced this with `Get-Package`. But H2 found that `Get-Package`'s `FastPackageReference` is the wrong field. This is now a residual from an incomplete fix — captured in H2.

**I6. Plan 04-04 task 11 acceptance_criteria counts tests as "63+"**
- The arithmetic: 35 (Phase 1-3) + 16 (04-01) + 12 (04-03) = 63. Plan 04-04 has zero C# tests (only TS specs). The "63+" total is consistent. No action.

## Verification Coverage

I could NOT verify (in this read-only cycle):

- **`Microsoft.Extensions.ResX.SourceGenerator` version pinning**: the actual NuGet package version that ships with `Microsoft.Extensions.Configuration.Json` 8.0.0 (not currently in repo — would need a fresh `dotnet add package` to confirm the API). Marked M6.
- **`Get-Package -Name 'Fingerprint Agent' | Select-Object FastPackageReference`** PowerShell behavior: not directly runnable from this shell. Marked H2 based on documented PackageProvider semantics; would need a one-line PS test on Windows to confirm.
- **`util:EventSource RemoveOnUninstall="yes"`** exact WiX attribute name: standard pattern but I did not fetch the WiX UtilExtension schema. Marked M11 based on the WiX UtilExtension namespace being declared in 04-02 task 8.
- **WixToolset 3.14.0 SDK-style project `<HarvestDirectory>` behavior**: research.md uses `HeatDirectory` (legacy); the plan switches to explicit `<File>` enumeration. Either works; the choice is reasonable but not load-tested by me.
- **`Msiexec /x FingerprintAgent-Setup.msi`** uninstall-by-source-msi: works on modern Windows but I did not run a test instance.
- **Playwright 1.55.1 local-network-access behavior** on `windows-latest` runner with Chromium 142+: per RESEARCH.md §4 the pinning avoids the prompt, but I did not run a Playwright session.
- **`msiexec` exit code propagation when `ProbeHealthAfterInstall` returns Failure AFTER service started**: whether SCM starts service before MSI rolls back is a timing-sensitive question I cannot resolve from docs alone. Recommend executor run smoke step 11 (force msiexec /i on a port-already-in-use system) and observe.

All cited source code symbols (`ConfigFileWatcher`, `ConfigLoader`, `AgentConfig`, `OnStart`, `OnConfigReloaded`, `BindConfig`, `OnDebounceElapsed`) were verified via codegraph_explore against current on-disk source.
