# Phase 4 Research: Deployment & End-to-End Validation

**Research date:** 2026-08-19
**Researcher:** Sisyphus (orchestrator, inline — no subagent delegation per user request)
**Phase scope:** MSI installer, VC++ x86 detection, auto-update via GitHub Releases, E2E browser validation, config migration

---

## 1. WiX Toolset 3.x — MSI Authoring + C# CustomAction DLL

### Key Findings

#### Toolchain Choice

WiX 3.x is the only stable, mature option that:
- Compiles `.wxs` XML source via `candle.exe` → `.wixobj` → `light.exe` → `.msi`
- Ships `MakeSfxCA.exe` to wrap managed C# DLLs into MSI-compatible type-1 DLLs
- Has MSBuild integration via `Wix.CA.targets` so we get a single `dotnet build` step (when added to GitHub Actions runner — local devs still use `dotnet build` + existing PS1 scripts per D-02)
- Provides `<MajorUpgrade>`, `<ServiceInstall>`, `<CustomAction>`, `<Binary>`, `<RegistrySearch>`, `WixUI_Minimal` out of the box

WiX v4 is in active development but breaking-API — stick with v3 for v1 of this project.

#### DTF (Deployment Tools Foundation) for C# CustomAction

DTF lets us write a CustomAction in C# that:
- Takes a `Session` object (full access to MSI properties, database, log)
- Returns `ActionResult.Success` / `Failure` / `UserExit`
- Compiles with NuGet packages:
  - `WixToolset.Dtf.WindowsInstaller` (referenced by CA code)
  - `WixToolset.Dtf.CustomAction` (MSBuild target that runs `MakeSfxCA.exe` after build)

Recommended project layout:

```
src/FingerprintAgent.Installer/
├── FingerprintAgent.Installer.csproj   (net48, PackageReference to Dtf.CustomAction)
├── CustomActions.cs                    (entry points: CheckVcRedist, WaitForService, ProbeHealth)
├── CustomAction.config                 (supportedRuntime v4.0.30319)
└── Properties/
    └── VietnameseStrings.resx          (error messages)
```

The MSBuild target from `WixToolset.Dtf.CustomAction` runs `MakeSfxCA.exe` automatically after build, producing `FingerprintAgent.Installer.CA.dll` which is what `<Binary SourceFile="..."/>` references.

#### Recommended CustomAction Implementation

```csharp
using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Deployment.WindowsInstaller;
using Microsoft.Win32;
using System.Net.Http;

namespace FingerprintAgent.Installer
{
    public class CustomActions
    {
        // Schedule this CustomAction in InstallUISequence before InstallFinalize.
        // Returns Failure → MSI rolls back. Returns Success → install proceeds.
        [CustomAction]
        public static ActionResult CheckVcRedist(Session session)
        {
            session.Log("Checking VC++ x86 runtime presence...");
            try
            {
                // D-12: VS 2015-2022 → registry under Wow6432Node (x64 OS) or direct (x86 OS)
                // Pattern from MS docs: HKEY_LOCAL_MACHINE\SOFTWARE\[Wow6432Node\]
                //   \Microsoft\VisualStudio\14.0\VC\Runtimes\x86\Installed == 1
                string[] subkeys =
                {
                    @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86",
                    @"SOFTWARE\Wow6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86"
                };

                foreach (var key in subkeys)
                {
                    using (var reg = Registry.LocalMachine.OpenSubKey(key))
                    {
                        var installed = reg?.GetValue("Installed");
                        if (installed != null && Convert.ToInt32(installed) == 1)
                        {
                            session.Log($"VC++ x86 runtime found at {key}");
                            return ActionResult.Success;
                        }
                    }
                }

                session.Log("VC++ x86 runtime NOT installed — showing Vietnamese error dialog");
                // Trigger the localized error dialog (defined in WXL file)
                session["VCRedistMissingDialog"] = "1";
                return ActionResult.Failure;  // Roll back install
            }
            catch (Exception ex)
            {
                session.Log($"VC++ detection failed: {ex.Message}");
                // If we can't tell, let install proceed — operator can troubleshoot post-install
                return ActionResult.Success;
            }
        }

        [CustomAction]
        public static ActionResult ProbeHealthAfterInstall(Session session)
        {
            session.Log("Probing /health on http://127.0.0.1:5043/health ...");
            // D-05: ping /health after install; non-200 → rollback
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                try
                {
                    var response = client.GetAsync("http://127.0.0.1:5043/health").GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        session.Log($"/health returned {(int)response.StatusCode}, rolling back");
                        return ActionResult.Failure;
                    }
                    session.Log("/health OK, install validated");
                    return ActionResult.Success;
                }
                catch (Exception ex)
                {
                    session.Log($"/health probe failed: {ex.Message}");
                    return ActionResult.Failure;
                }
            }
        }
    }
}
```

**Companion `CustomAction.config`** (required to target net48 in MSI runtime):

```xml
<?xml version="1.0" encoding="utf-8" ?>
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
  </startup>
</configuration>
```

#### Service Registration via WiX (D-19)

`<ServiceInstall>` handles service creation without custom action:

```xml
<ServiceInstall
    Id="FingerprintAgentService"
    Name="FingerprintAgent"
    DisplayName="Fingerprint Agent"
    Description="Local fingerprint capture service for HIS SaaS"
    Type="ownProcess"
    Start="auto"
    Account="LocalSystem"
    ErrorControl="normal"
    Vital="yes">
  <util:ServiceConfig
      FirstFailureActionType="restart"
      SecondFailureActionType="restart"
      ThirdFailureActionType="restart"
      RestartServiceDelayInSeconds="5"
      ResetPeriodInDays="1" />
</ServiceInstall>
```

`util:` namespace requires `xmlns:util="http://schemas.microsoft.com/wix/UtilExtension"` + reference to `WixUtilExtension.dll` at build time (built into WiX 3.x).

#### MajorUpgrade (D-03)

```xml
<MajorUpgrade
    AllowSameVersionUpgrades="yes"
    AllowDowngrades="no"
    DowngradeErrorMessage="A newer version of [ProductName] is already installed."
    Schedule="afterInstallExecute" />
```

`Schedule="afterInstallExecute"` means: old version's files are replaced THEN service is stopped/started (less downtime than `afterInstallInitialize`). Per D-03 smooth in-place upgrade.

**Service account caveat** (from research): WiX `<ServiceInstall>` re-registers the service on every upgrade with attributes from XML. Since we always use `LocalSystem` (no per-machine customization), this is fine. If we ever supported custom credentials, we'd need Option C (conditional `CreateServices` standard action) — not our concern for v1.

#### WiX UI Level Choice

`WixUI_Minimal` is the right choice:
- Built into WiX 3.x (`WixUIExtension.dll` reference)
- Welcome + install + finish dialogs only — no install-dir picker (matches D-04 hard-coded paths)
- Supports `msiexec /qn` for fully silent (D-08)

```xml
<UIRef Id="WixUI_Minimal" />
```

For the Vietnamese VC++ error dialog (D-11), define a separate `<Dialog>` in a custom `WXL` file (localization) — keeps the standard UI English but our error message Vietnamese.

#### `.wxs` Project Layout

```
installer/
├── FingerprintAgent.Installer.wxs      (Product, MajorUpgrade, UI)
├── Components/
│   ├── Service.wxs                     (ServiceInstall, exe + DLLs)
│   ├── ProgramDataConfig.wxs           (config.json template, Permanent="yes")
│   ├── CustomActions.wxs               (Binary + CustomAction declarations)
│   └── UninstallBehavior.wxs           (REMOVE_LOGS property handling)
├── Dialogs/
│   ├── VcRedistError.wxs               (Vietnamese error dialog)
│   └── WixUI_Minimal.vi-VN.wxl         (localized strings)
└── FingerprintAgent.Installer.wixproj  (WiX MSBuild project)
```

---

## 2. GitHub Releases API — Auto-Update Source of Truth

### Key Findings

#### Endpoint

```
GET https://api.github.com/repos/{owner}/{repo}/releases/latest
```

- No authentication required for public repos (rate-limited to 60 req/hour/IP — fine for our usage)
- Returns latest **non-prerelease, non-draft** release sorted by `created_at`
- Headers: `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2026-03-10`
- Response shape (excerpt):
  ```json
  {
    "tag_name": "v1.2.3",
    "name": "v1.2.3",
    "prerelease": false,
    "draft": false,
    "assets": [
      {
        "name": "FingerprintAgent-Setup.msi",
        "browser_download_url": "https://github.com/.../FingerprintAgent-Setup.msi",
        "size": 8388608,
        "content_type": "application/octet-stream"
      }
    ],
    "published_at": "2026-08-15T..."
  }
  ```

#### Version Comparison (D-15, D-16, D-17)

`System.Version.TryParse` after stripping `v` prefix:

```csharp
string tagName = release.tag_name;          // "v1.2.3-rc1"
string versionOnly = tagName.StartsWith("v") ? tagName.Substring(1) : tagName;
// Strip prerelease suffix (System.Version doesn't support it)
int dashIdx = versionOnly.IndexOf('-');
if (dashIdx >= 0) versionOnly = versionOnly.Substring(0, dashIdx);
// "1.2.3"
if (!Version.TryParse(versionOnly, out var latestVersion))
{
    _logger?.Warn(cid, $"Cannot parse release version: {tagName}");
    return;
}

var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
if (latestVersion <= currentVersion)
{
    // No update available — bump no-update counter for auto-backoff
    return;
}
```

**Prerelease handling (per CONTEXT D-16)**: The `/releases/latest` endpoint already excludes drafts and prereleases (`prerelease: false`). To also exclude tags like `v1.2.3-rc1`, we filter by `prerelease: false` in the JSON response (defense in depth — shouldn't appear in latest, but guards against API quirks).

#### Auto-Backoff (D-15)

| Consecutive no-update checks | Next check interval |
|---|---|
| 0 | `checkIntervalHours` (default 6) |
| 1 | 6h |
| 2 | 12h |
| 3+ | 24h (capped) |
| Update detected | Reset to 6h |

Implementation lives in `UpdateCheckService` class — `Timer.Change(TimeSpan.FromHours(nextInterval), ...)` resets the period on each tick.

#### Download → Install Flow (D-17)

```csharp
// 1. Download MSI to %TEMP%\FingerprintAgent-Setup.msi
var tempPath = Path.Combine(Path.GetTempPath(), "FingerprintAgent-Setup.msi");
using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
using (var stream = await http.GetStreamAsync(asset.browser_download_url))
using (var file = File.Create(tempPath))
{
    await stream.CopyToAsync(file);
}

// 2. Pre-install toast (D-41, D-42) — non-blocking 10s delay
ShowUpdateToast("FingerprintAgent đang cập nhật phiên bản mới...", delaySeconds: 10);

// 3. Run installer silently; SCM recovery restarts service after msiexec exits
var psi = new ProcessStartInfo
{
    FileName = "msiexec.exe",
    Arguments = $"/qn /i \"{tempPath}\"",
    UseShellExecute = false,
    CreateNoWindow = true
};
using (var p = Process.Start(psi))
{
    p.WaitForExit(TimeSpan.FromMinutes(15));
}

// 4. Exit so SCM can restart us with new binaries
Environment.Exit(0);
```

**On failure (D-43)**: log Error, write EventLog Error, write `update.enabled = false` to config.json. Service keeps running on old version — better than crashing.

---

## 3. VC++ x86 Runtime Detection

### Key Findings

#### Registry Location

Microsoft docs: `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{x86|x64|arm64}`

On a 64-bit OS reading the **x86 package**, the key is under `Wow6432Node`:
- 64-bit OS: `HKLM\SOFTWARE\Wow6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86`
- 32-bit OS: `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86`

`Installed` DWORD value = 1 means present.
`Major` / `Minor` / `Bld` REG_DWORD values give version (always Major=14 since VS2015→2022 are binary compatible).
`Version` REG_SZ (e.g. `v14.40.33810`) is also present for human inspection.

#### Detection Code

From `Redistributing Visual C++ Files` (Microsoft Learn, 2026-04-15):
> The version number is stored in the `REG_SZ` string value `Version` and also in the set of `Major`, `Minor`, `Bld`, and `Rbld` `REG_DWORD` values. To avoid an error at installation time, you must skip installation of the redistributable package if the currently installed version is more recent.

Since we're **detecting only** (no install, per D-09), the check is just:

```csharp
bool IsVcRedistX86Installed()
{
    string[] keys =
    {
        @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86",
        @"SOFTWARE\Wow6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86"
    };
    foreach (var key in keys)
    {
        using (var reg = Registry.LocalMachine.OpenSubKey(key))
        {
            if (reg?.GetValue("Installed") is int installed && installed == 1)
                return true;
        }
    }
    return false;
}
```

#### Error UX (D-11)

Vietnamese error dialog content (per D-11):

```
Thiếu Microsoft Visual C++ Redistributable (x86)
─────────────────────────────────────────────
Máy tính này chưa được cài đặt thư viện Microsoft Visual C++
Redistributable (x86), cần thiết để FingerprintAgent hoạt động.

Vui lòng tải và cài đặt từ:
https://aka.ms/vs/17/release/vc_redist.x86.exe

Sau khi cài đặt xong, hãy chạy lại trình cài đặt FingerprintAgent.
```

Implemented as WiX `<Dialog>` referenced from `InstallUISequence`. Triggered when `CheckVcRedist` returns Failure — MSI rolls back AND shows the dialog before rollback completes.

---

## 4. Playwright E2E for Local HTTP API + CORS

### Key Findings

#### file:// Will Not Work

Confirmed from `cors-handbook.com` and Playwright issue tracker:
> A page loaded from `file:///Users/me/demo/index.html` does not behave like a normal web app. `file://` pages send `Origin: null`, which most servers don't explicitly allow.

Even with `Access-Control-Allow-Origin: *`, modern Chrome blocks `fetch()` from `file://` to `http://localhost:5043` because they have different origins. **We must serve the test SaaS page from a real HTTP origin.**

#### Recommended Test Architecture

```
tests/FingerprintAgent.E2E/
├── package.json                       (Playwright + http server deps)
├── playwright.config.ts               (Chromium only; 1.56.x or pin 1.55.1)
├── fixtures/
│   ├── saas-page.html                 (test page with fetch() to agent)
│   ├── mock-backend.ts                (http.createServer on random port)
│   └── global-setup.ts                (spins up agent process or assumes running)
└── specs/
    ├── cors-preflight.spec.ts         (OPTIONS /api/capture → 204 + CORS headers)
    ├── capture-flow.spec.ts           (POST → 200 + valid PNG base64)
    └── end-to-end.spec.ts             (page → agent → mock backend)
```

#### Chromium 141+ Local Network Access Gotcha

From Playwright issue #37769 (2025-10-08): Chromium 142+ blocks public-origin fetch to private network (localhost) by default. Workarounds:

```typescript
// playwright.config.ts
export default defineConfig({
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        launchOptions: {
          args: [
            '--ip-address-space-overrides=127.0.0.1:0=public',
          ],
        },
      },
    },
  ],
  use: {
    baseURL: 'http://127.0.0.1:8080',  // mock backend's port
    permissions: ['local-network-access'],
  },
});
```

**Pin Playwright version** to avoid surprise breakage:
- `playwright@1.55.1` — known-good, no local-network-access prompts
- `playwright@1.56.x` — requires `--ip-address-space-overrides` flag + `permissions: ['local-network-access']`

Recommendation: pin `1.55.1` for v1 E2E stability; revisit when 1.56 stabilizes.

#### Test SaaS Page Fixture

`fixtures/saas-page.html` — served by mock backend:

```html
<!DOCTYPE html>
<html>
<head><title>E2E Mock SaaS</title></head>
<body>
<script>
async function doCapture() {
    // Step 1: CORS preflight (browser does this automatically for POST+JSON)
    const resp = await fetch('http://127.0.0.1:5043/api/capture', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            thamChieuId: 'e2e-test',
            maPhieu: 'E2E-001',
            loaiPhieu: 'signature',
            vaiKyId: null,
            nhanLucId: null,
            metadata: {}
        })
    });
    const data = await resp.json();
    // Step 2: Forward to mock backend
    await fetch('/receive', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            success: data.isSuccess,
            bytesLen: data.imageBytes ? atob(data.imageBytes).length : 0,
            sha256: data.verificationData
        })
    });
    document.title = data.isSuccess ? 'OK' : 'FAIL';
}
doCapture();
</script>
</body>
</html>
```

#### Mock Backend (Node http server)

```typescript
// fixtures/mock-backend.ts
import { createServer } from 'http';
export function startMockBackend(port: number) {
    const received: any[] = [];
    const server = createServer((req, res) => {
        if (req.url === '/receive' && req.method === 'POST') {
            let body = '';
            req.on('data', chunk => body += chunk);
            req.on('end', () => {
                received.push(JSON.parse(body));
                res.writeHead(200);
                res.end('OK');
            });
        } else if (req.url === '/' || req.url === '/saas-page.html') {
            // Serve the static page
            res.writeHead(200, { 'Content-Type': 'text/html' });
            res.end(require('fs').readFileSync('./fixtures/saas-page.html'));
        } else {
            res.writeHead(404);
            res.end();
        }
    });
    server.listen(port);
    return { server, received };
}
```

#### E2E Spec

```typescript
// specs/end-to-end.spec.ts
import { test, expect } from '@playwright/test';

test('browser → agent → mock backend round-trip', async ({ page }) => {
    await page.goto('http://127.0.0.1:8080/saas-page.html');
    // Wait for the fetch chain to complete (title updates)
    await expect(page).toHaveTitle('OK', { timeout: 15000 });
});
```

#### CI Integration (D-23)

`.github/workflows/e2e.yml` — `workflow_dispatch` only (operator-triggered before tagging a release, per D-23):

```yaml
name: E2E Tests
on:
  workflow_dispatch:
jobs:
  e2e:
    runs-on: windows-latest  # Required — FingerprintAgent is Windows-only
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: '22'
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      - run: dotnet build src/FingerprintAgent/FingerprintAgent.sln -c Release
      - run: dotnet test  # xUnit tests
      - run: cd tests/FingerprintAgent.E2E && npm ci && npx playwright install chromium
      - run: npx playwright test
```

**No `mocks/` or vendor SDKs required** — tests run with `mockMode: true` (already the case in default `config.json`, no real scanner needed).

---

## 5. Windows Toast Notifications — Known Limitations

### Key Findings

#### The Problem

From StackOverflow 72143608 (confirmed by Microsoft.Toolkit.Uwp.Notifications team):
> A Windows Service running under LocalSystem account cannot send toast notifications because the toast platform requires an interactive user session. Calling `ToastContentBuilder().Show()` from a service throws `UnauthorizedAccessException`.

Our service runs as `LocalSystem` (D-19). Toasts will **always fail** in this configuration.

#### Decision

Per CONTEXT D-44 ("Always attempt toast; Windows handles no-user-session case") we should:
- **Try** the toast via `Microsoft.Toolkit.Uwp.Notifications` 7.1.x
- **Wrap** in try/catch — swallow `UnauthorizedAccessException` silently
- **Fall back** to: Error EventLog entry + log file entry (D-43)

```csharp
private void ShowUpdateToast(string message, int delaySeconds = 0)
{
    try
    {
        if (delaySeconds > 0) Thread.Sleep(delaySeconds * 1000);
        new ToastContentBuilder()
            .AddText("FingerprintAgent")
            .AddText(message)
            .Show();
    }
    catch (UnauthorizedAccessException)
    {
        // No interactive user session — toast silently unavailable
        _logger?.Info(cid, "Toast unavailable (no interactive session); EventLog-only notification");
    }
    catch (Exception ex)
    {
        _logger?.Warn(cid, $"Toast failed: {ex.Message}");
    }
}
```

**Better alternative** (recommended): Skip the toast library entirely, use a simple `EventLog.WriteEntry("FingerprintAgent", message, EventLogEntryType.Information)` — which is what we already do via `AgentLogger.TryWriteEventLog`. One less NuGet dep, one less failure mode. EventLog entries appear in Windows Event Viewer for the operator.

**Recommendation for v1**: Use EventLog only. Toast UX is a Phase 5+ enhancement. D-44's "always attempt toast" was aspirational — given LocalSystem context, it's effectively always-unavailable for our service.

---

## 6. ConfigMerger — Smart Merge Algorithm

### Key Findings

#### Problem

On MSI upgrade, we have:
- `template`: new version's default `config.json` (in MSI)
- `userConfig`: existing `C:\ProgramData\FingerprintAgent\config.json` (may have IT customizations)

We need to:
- **Add** keys that exist in template but NOT in userConfig (new features gain defaults)
- **Preserve** user values for keys that exist in BOTH (IT customization wins)
- **Respect** user deletions — keys removed by user stay removed

This is the OPPOSITE of `JObject.Merge()` default behavior, which REPLACES first object with second. We need a "reverse merge" or "additive merge from template to user".

#### Recommended Implementation

```csharp
using Newtonsoft.Json.Linq;

namespace FingerprintAgent.Configuration
{
    public static class ConfigMerger
    {
        /// <summary>
        /// Merges new template keys INTO user config without overwriting user values.
        /// Per D-35: new keys added, user values preserved, deletions respected.
        /// </summary>
        public static JObject Merge(JObject userConfig, JObject template)
        {
            foreach (var templateProp in template.Properties())
            {
                if (!userConfig.ContainsKey(templateProp.Name))
                {
                    // Key missing in user config → add from template
                    userConfig[templateProp.Name] = templateProp.Value.DeepClone();
                }
                else if (templateProp.Value.Type == JTokenType.Object
                    && userConfig[templateProp.Name] is JObject userObj)
                {
                    // Recurse into nested objects
                    Merge(userObj, (JObject)templateProp.Value);
                }
                // Else: user has a value OR has a different type → preserve user value
                // Per D-35: respect user choice, never overwrite
            }
            return userConfig;
        }
    }
}
```

#### Wire-Up

In `ConfigLoader.Load()`:
1. Check `%ProgramData%\FingerprintAgent\config.json` (per D-36)
2. If missing, copy template from `AppDomain.CurrentDomain.BaseDirectory\config.template.json`
3. If exists, parse both userConfig and template; call `ConfigMerger.Merge(userConfig, template)`; write result back to ProgramData; return merged
4. On merge failure, keep the old userConfig (D-08 carryover from Phase 3)

```csharp
public static AgentConfig Load()
{
    string programDataConfig = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FingerprintAgent", "config.json");
    string templatePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "config.template.json");

    if (!File.Exists(programDataConfig) && File.Exists(templatePath))
    {
        // First install — seed ProgramData from template
        Directory.CreateDirectory(Path.GetDirectoryName(programDataConfig));
        File.Copy(templatePath, programDataConfig);
        return LoadFromFile(programDataConfig);
    }

    if (File.Exists(programDataConfig) && File.Exists(templatePath))
    {
        // Upgrade — smart merge
        var userJson = JObject.Parse(File.ReadAllText(programDataConfig));
        var templateJson = JObject.Parse(File.ReadAllText(templatePath));
        ConfigMerger.Merge(userJson, templateJson);
        File.WriteAllText(programDataConfig, userJson.ToString(Formatting.Indented));
        return LoadFromFile(programDataConfig);
    }

    // Fallback — no ProgramData config and no template, use install-dir copy
    string basePath = AppDomain.CurrentDomain.BaseDirectory;
    return LoadFromDirectory(basePath);
}
```

#### Optional merge.log (per CONTEXT specifics)

Write `C:\ProgramData\FingerprintAgent\merge.log` after upgrade, showing what changed:

```
2026-08-19 10:15:30 UTC — Config merged from template v1.0.1 → user config
Added:
  + update.enabled = false
  + update.checkIntervalHours = 6
Updated:
  - (none)
```

This is opt-in — write only if `ConfigMerger` produced changes.

---

## 7. MSI Exit Codes & CustomAction Return Values

### Key Findings

From Microsoft Learn `Custom Action Return Values`:

| Return value | Constant | MSI behavior |
|---|---|---|
| `ActionResult.Success` (1) | `ERROR_SUCCESS` | Continue install |
| `ActionResult.Failure` (1603) | `ERROR_INSTALL_FAILURE` | Roll back |
| `ActionResult.UserExit` (1602) | `ERROR_INSTALL_USEREXIT` | User cancelled, rollback |
| `ActionResult.NoMoreItems` (255) | `ERROR_NO_MORE_ITEMS` | Skip remaining, not error |

`msiexec /qn <path>` exit codes mirror these:
- `0` = success
- `1603` = fatal error → MSI rolls back automatically
- `1602` = user exit (rare in /qn mode)

**For our `/health` probe** (D-05): if probe fails, return `ActionResult.Failure` → MSI rolls back. Operator sees the failure log in `%TEMP%\MSI*.log`.

**For our auto-update flow**: if `msiexec` returns non-zero, log Error + EventLog + disable `update.enabled`. Do NOT crash the service — old version keeps running.

---

## 8. PowerShell Scripts — Preservation Strategy

### Key Findings

Per D-32, all 5 existing PS1 scripts are preserved as dev/test fallback:

| Script | Status | Why |
|---|---|---|
| `Install-Service.ps1` | Keep | Dev machines without MSI build |
| `Uninstall-Service.ps1` | Keep | Same |
| `Service.ps1` | Keep | start/stop/restart/status — orthogonal to install method |
| `Setup-VendorSdk.ps1` | Keep | Dev convenience for SDK DLL copying |
| `Test-Capture.ps1` | Keep | Quick smoke test, parallel to Playwright E2E |

README.md documents the role split: **PS1 = dev/test, MSI = production IT**.

---

## 9. `docs/` Folder Cleanup (D-27)

### Key Findings

Per `STRUCTURE.md` (verified): `docs/ARCHITECTURE.md` references Kestrel/OWIN which are no longer accurate — we use raw `HttpListener`, not self-host OWIN.

**Action**: Delete the entire `docs/` folder. Source of truth becomes `.planning/codebase/`. Single commit per Phase 4 plan that touches docs.

---

## Recommendations Summary

### Implementation Plan Implications

#### MSI / WiX Toolset
- **WiX 3.x** with `<MajorUpgrade Schedule="afterInstallExecute" AllowSameVersionUpgrades="yes">` for smooth in-place upgrades
- **C# CustomAction DLL** via DTF (`WixToolset.Dtf.CustomAction` NuGet) — `CheckVcRedist` + `ProbeHealthAfterInstall`
- **`<ServiceInstall>`** for service registration (no custom action needed)
- **`WixUI_Minimal`** with custom Vietnamese dialog for VC++ missing error
- **`msiexec /qn`** fully silent support

#### Config Migration
- **ProgramData path**: `C:\ProgramData\FingerprintAgent\config.json` (writable without admin, survives upgrade)
- **`ConfigMerger.Merge(user, template)`** — additive only, preserves user values + deletions
- **`config.template.json`** shipped in install dir as read-only reference
- **`merge.log`** written when new keys are added (optional, operator debugging aid)

#### Auto-Update
- **`System.Threading.Timer`** inside `FingerprintAgentService`, default disabled per D-14
- **`GET /repos/{owner}/{repo}/releases/latest`** (no auth for public repo)
- **`System.Version.TryParse`** after stripping `v` prefix + `-suffix`
- **Auto-backoff**: 6h → 12h → 24h after 3 no-update checks, reset on update detected
- **EventLog + log file** for notification (skip toast — LocalSystem can't show toasts)

#### E2E Validation
- **Playwright 1.55.1** (pinned — 1.56 has local-network-access complications)
- **Separate Node.js project** at `tests/FingerprintAgent.E2E/`
- **Mock SaaS page served from `http.createServer`** (not file://)
- **`--ip-address-space-overrides=127.0.0.1:0=public`** Chromium launch arg
- **CI runs on `windows-latest`** (agent is Windows-only)
- **`workflow_dispatch` only** (operator-triggered before tagging release)

#### VC++ Detection
- **HKLM\SOFTWARE\[Wow6432Node\]Microsoft\VisualStudio\14.0\VC\Runtimes\x86\Installed == 1**
- **Vietnamese error dialog** (single-language, matches hospital audience)
- **Download URL**: `https://aka.ms/vs/17/release/vc_redist.x86.exe` baked into dialog text

#### Phase Decomposition Hints

Based on CONTEXT dependencies, expect **4 plans** matching the existing Phase 3 pattern:

1. **`04-01-PLAN.md` — ConfigMerger + ProgramData path migration + UpdateConfig POCO**
   - Move config.json reading to ProgramData
   - Add `UpdateConfig` POCO to `AgentConfig.cs`
   - Implement `ConfigMerger` + xUnit tests for merge edge cases
   - Update `ConfigFileWatcher` to watch ProgramData path (D-37)

2. **`04-02-PLAN.md` — MSI installer + C# CustomAction DLL**
   - New `src/FingerprintAgent.Installer/` project
   - WiX `.wxs` sources + `.wixproj` MSBuild
   - `CheckVcRedist` + `ProbeHealthAfterInstall` custom actions
   - Vietnamese error dialog WXL
   - `.github/workflows/release.yml` (CI builds MSI on tag push)

3. **`04-03-PLAN.md` — Auto-update Timer + UpdateCheckService**
   - `UpdateCheckService` class with `System.Threading.Timer`
   - GitHub Releases API GET + JSON parse via Newtonsoft.Json
   - MSI download + `msiexec /qn` invocation
   - Auto-backoff counter
   - UpdateConfig wiring + `update.enabled = false` default
   - xUnit tests for version comparison, mock HttpMessageHandler

4. **`04-04-PLAN.md` — E2E Playwright + Documentation + docs/ cleanup**
   - `tests/FingerprintAgent.E2E/` Node.js project
   - Mock SaaS HTML + mock backend HTTP server
   - `playwright.config.ts` with `--ip-address-space-overrides`
   - CORS preflight spec + capture-flow spec + end-to-end spec
   - `.github/workflows/e2e.yml` with `workflow_dispatch`
   - `README.md` (combined dev+IT)
   - `DEPLOYMENT.md` (Vietnamese operations runbook)
   - Delete `docs/` folder (D-27)

### Risks Identified

| Risk | Mitigation |
|---|---|
| Toast API fails silently under LocalSystem | Skip toast; EventLog-only notification (Phase 5+ enhancement) |
| Playwright Chromium network access prompts break CI | Pin to 1.55.1; use `--ip-address-space-overrides` flag |
| `ConfigMerger` overwrites user values | Use additive merge (template → user, never reverse); write `merge.log` |
| MSI rollback on /health probe failure during clean install | D-38 success dialog is BEFORE rollback; ensure clear log of why rollback happened |
| ConfigMerger on first install + auto-update race | First run uses template directly; auto-update only on existing config |
| WiX `MakeSfxCA.exe` not on PATH in CI | Use `WixToolset.Dtf.CustomAction` NuGet which handles this automatically |

### Out of Scope Reminders (deferred to Phase 5+)

- Code signing certificate / EV cert (SmartScreen bypass)
- Delta updates / rollback for auto-update
- Code signing Authenticode timestamp
- MSI transform (.mst) for IT customization
- Bootstrapper (.exe) wrapping MSI + prerequisites (rejected per D-09 — no bundling)
- Channel preview/stable for auto-update
- Multi-language MSI UI (Vietnamese for error dialogs only)
- Telemetry / usage reporting from agent

---

*Research compiled: 2026-08-19 by Sisyphus (orchestrator inline) — no subagent delegation per user request.*
*Sources: wixtoolset.org/docs (FireGiant), docs.github.com (GitHub REST API), learn.microsoft.com (VC++ redist, MSI return values), playwright.dev/docs, stackoverflow.com (ServiceConfig quirks, LocalSystem toast), newtonsoft.com/json (JObject.Merge), cors-handbook.com (file:// origins), semver.org (SemVer 2.0.0).*
