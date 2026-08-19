# Testing Patterns

**Analysis Date:** 2026-08-19

## Framework & Dependencies

**Test Runner:**
- xUnit 2.9.3 — primary test framework
- Microsoft.NET.Test.Sdk 17.13.0 — VSTest adapter
- xunit.runner.visualstudio 2.8.2 — IDE/test runner integration

**Mocking Library:**
- Moq 4.20.72 — referenced but **rarely used**. Custom test doubles are preferred for `IScannerAdapter` and other vendor-SDK boundaries.

**JSON Helpers:**
- Newtonsoft.Json 13.0.3 — parsed by tests via `JObject.Parse` for HTTP response assertions
- `JsonConvert.DeserializeObject<T>` for typed response assertions

**Project file:** `tests/FingerprintAgent.Tests/FingerprintAgent.Tests.csproj`
- `<TargetFramework>net48</TargetFramework>`
- `<PlatformTarget>x86</PlatformTarget>` (matches vendor SDK requirement)
- `<RootNamespace>FingerprintAgent.Tests</RootNamespace>`
- `InternalsVisibleTo` set on production assembly → `FingerprintAgent.Tests`

```xml
<PackageReference Include="xunit" Version="2.9.3" />
<PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.13.0" />
<PackageReference Include="Moq" Version="4.20.72" />
```

## Test Organization

**Location:** `tests/FingerprintAgent.Tests/` — single test project rooted at the repo.

**Subfolder layout (mirrors src structure by system boundary):**

```
tests/FingerprintAgent.Tests/
├── Api/
│   ├── CorsMiddlewareTests.cs              # CORS via real HttpServer
│   ├── ErrorHandlingTests.cs               # CaptureHandler/HealthHandler with HttpListener
│   └── HttpServerIntegrationTests.cs       # End-to-end /health and /api/capture
├── Configuration/
│   └── ConfigLoaderTests.cs                # ConfigLoader with temp directories
├── Logging/
│   └── AgentLoggerTests.cs                 # File sink + concurrent writes
└── Scanner/
    ├── MockScannerAdapterTests.cs          # Mock adapter property assertions
    ├── MockScannerAdapterTestDoubles.cs    # Shared doubles + CaptureHandlerTestFixture
    ├── ScannerManagerProbeIntegrationTests.cs   # Real ZK9500 device required
    ├── ScannerManagerTests.ExponentialBackoff.cs # Mocked ScannerManager behavior
    ├── ZKTecoDeviceIntegrationTests.cs     # Real-device, skipped when no SDK
    └── ZkSdkProbe.cs                       # SDK presence probe helper
```

**Test file naming:**
- `<ClassName>Tests.cs` for unit tests against a class (e.g., `MockScannerAdapterTests.cs`)
- `<ClassName>Tests.<Concern>.cs` for partial-class split by concern (e.g., `ScannerManagerTests.ExponentialBackoff.cs`)
- `<Feature>Tests.cs` for cross-cutting integration tests (e.g., `HttpServerIntegrationTests.cs`, `ErrorHandlingTests.cs`)
- Test doubles/fixtures live in `MockScannerAdapterTestDoubles.cs` — central shared file

**Test namespace pattern:** `FingerprintAgent.Tests.<Module>` — mirrors src modules.

```csharp
namespace FingerprintAgent.Tests.Api            // matches src/FingerprintAgent/Api
namespace FingerprintAgent.Tests.Configuration // matches src/FingerprintAgent/Configuration
namespace FingerprintAgent.Tests.Logging       // matches src/FingerprintAgent/Logging
namespace FingerprintAgent.Tests.Scanner       // matches src/FingerprintAgent/Adapters
```

## Test Naming Convention

**Pattern:** `<Subject>_<Scenario>_<ExpectedResult>` (PascalCase + underscores).

**Examples from the codebase:**
- `BackoffStep_StartsAtZero` — `tests/FingerprintAgent.Tests/Scanner/ScannerManagerTests.ExponentialBackoff.cs:11`
- `BackoffStep_IncrementsAfterAllAdapterFailure` — `tests/FingerprintAgent.Tests/Scanner/ScannerManagerTests.ExponentialBackoff.cs:26`
- `BackoffStep_CapsAtThree` — `tests/FingerprintAgent.Tests/Scanner/ScannerManagerTests.ExponentialBackoff.cs:43`
- `InBackoff_IsFalseWhenStepIsZero` — `tests/FingerprintAgent.Tests/Scanner/ScannerManagerTests.ExponentialBackoff.cs:81`
- `InFlight_FailsImmediately_WhenScannerDisconnects` — `tests/FingerprintAgent.Tests/Scanner/ScannerManagerTests.ExponentialBackoff.cs:96`
- `CaptureHandler_Returns503_WhenScannerReturnsScannerNotConnected` — `tests/FingerprintAgent.Tests/Api/ErrorHandlingTests.cs:119`
- `CaptureHandler_Returns504_WhenScannerReturnsCaptureTimeout` — `tests/FingerprintAgent.Tests/Api/ErrorHandlingTests.cs:149`
- `CaptureHandler_Returns400_WhenRequestHasMissingFields` — `tests/FingerprintAgent.Tests/Api/ErrorHandlingTests.cs:206`
- `HealthHandler_Returns503_WhenDisconnectedAndMaxBackoff` — `tests/FingerprintAgent.Tests/Api/ErrorHandlingTests.cs:287`
- `Preflight_WithOrigin_Returns204` — `tests/FingerprintAgent.Tests/Api/CorsMiddlewareTests.cs:109`
- `Preflight_DeniedOrigin_Returns403` — `tests/FingerprintAgent.Tests/Api/CorsMiddlewareTests.cs:171`
- `Log_Info_CreatesFileWithStructuredEntry` — `tests/FingerprintAgent.Tests/Logging/AgentLoggerTests.cs:41`
- `Log_ConcurrentWrites_AreNotCorrupted` — `tests/FingerprintAgent.Tests/Logging/AgentLoggerTests.cs:164`
- `Load_ValidConfig_ReturnsAgentConfigWithCorrectValues` — `tests/FingerprintAgent.Tests/Configuration/ConfigLoaderTests.cs:20`

**Guidelines:**
- The subject is the class/property under test (e.g., `BackoffStep`, `CaptureHandler`, `Log_Info`).
- The scenario describes the precondition or input.
- The expected result describes the assertion (use `Returns<Code>`, `Is<True/False>`, `Matches<regex>`, etc.).
- Use `_` to separate the parts — **no camelCase test method names**.

## Test Types

### Unit Tests

**Scope:** A single class with mock/fake dependencies injected.

**Example:** `ScannerManagerExponentialBackoffTests` — exercises `ScannerManager` with `MockScannerAdapterWithSettableProperties` to drive backoff state.

```csharp
// tests/FingerprintAgent.Tests/Scanner/ScannerManagerTests.ExponentialBackoff.cs:26
[Fact]
public async Task BackoffStep_IncrementsAfterAllAdapterFailure()
{
    var failing = new MockScannerAdapterWithSettableProperties
    {
        IsConnectedValue = false,
        InitializeResult = false
    };
    var manager = new ScannerManager(new[] { failing }, null);

    await manager.ScanAsync();
    Assert.Equal(1, manager.BackoffStep);

    await manager.ScanAsync();
    Assert.Equal(2, manager.BackoffStep);
}
```

**Example:** `MockScannerAdapterTests` — drives the real `MockScannerAdapter` to verify PNG header bytes, SHA-256 verification data, dimensions.

```csharp
// tests/FingerprintAgent.Tests/Scanner/MockScannerAdapterTests.cs:24
[Fact]
public async Task Scan_ReturnsValidPngHeader()
{
    CaptureResult result = await _adapter.ScanAsync();
    byte[] pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
    byte[] actualHeader = new byte[4];
    System.Array.Copy(result.ImageBytes, actualHeader, 4);
    Assert.Equal(pngHeader, actualHeader);
}
```

### HTTP Integration Tests (Real HttpServer)

**Scope:** Boots a real `HttpServer` on a random free port, hits it with `HttpClient`.

**Example:** `HttpServerIntegrationTests` uses `TcpListener` to discover a free port:

```csharp
// tests/FingerprintAgent.Tests/Api/HttpServerIntegrationTests.cs:22
public HttpServerIntegrationTests()
{
    // Use TcpListener to find an available port to avoid conflicts
    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    _port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();

    _scanner = new MockScannerAdapter();
    _server = new HttpServer("127.0.0.1", _port, _scanner);
    _server.Start();

    _client = new HttpClient();
    _client.BaseAddress = new Uri($"http://127.0.0.1:{_port}");
    _client.Timeout = TimeSpan.FromSeconds(5);
}
```

**Example assertion against the running server:**

```csharp
// tests/FingerprintAgent.Tests/Api/HttpServerIntegrationTests.cs:54
[Fact]
public async Task CaptureEndpoint_WithValidBody_Returns200_AndImageBytes()
{
    var requestBody = new
    {
        thamChieuId = "t1",
        maPhieu = "P1"
    };

    var content = new StringContent(
        JsonConvert.SerializeObject(requestBody),
        Encoding.UTF8,
        "application/json");

    var response = await _client.PostAsync("/api/capture", content);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    string body = await response.Content.ReadAsStringAsync();
    var json = JObject.Parse(body);

    Assert.True((bool)json["isSuccess"]);
    Assert.NotNull(json["imageBytes"]);
    Assert.Equal("image/png", (string)json["mimeType"]);
    Assert.Equal(44, ((string)json["verificationData"]).Length); // SHA-256 base64 length
    Assert.Equal("mock-scanner-001", (string)json["deviceId"]);
}
```

### Handler Integration Tests (Raw HttpListener + WebRequest)

**Scope:** Tests `CaptureHandler`/`HealthHandler` directly against a real `HttpListenerContext` produced by an in-process `HttpListener`.

**Why not HttpServer?** Direct handler testing isolates the handler from routing — no need to spin up the full `HttpServer` loop.

**Example:** `ErrorHandlingTests` constructs a real `HttpListener` on a free port, dispatches a `WebRequest`, captures the `HttpListenerContext` once it arrives, and hands it to the handler.

```csharp
// tests/FingerprintAgent.Tests/Api/ErrorHandlingTests.cs:103
private async Task<HttpWebResponse> SendHttpRequestAsync(string body = "", string path = "/api/capture",
    string method = "POST", string contentType = "application/json")
{
    var request = WebRequest.CreateHttp(BaseUrl + path);
    request.Method = method;
    if (method != "GET")
    {
        request.ContentType = contentType;
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        request.ContentLength = bodyBytes.Length;
        using (var rs = await request.GetRequestStreamAsync())
            await rs.WriteAsync(bodyBytes, 0, bodyBytes.Length);
    }
    return (HttpWebResponse)await request.GetResponseAsync();
}
```

**End-to-end test of a single handler call:**

```csharp
// tests/FingerprintAgent.Tests/Api/ErrorHandlingTests.cs:119
[Fact]
public async Task CaptureHandler_Returns503_WhenScannerReturnsScannerNotConnected()
{
    var mock = new MockScannerAdapterWithSettableProperties
    {
        IsConnectedValue = false,
        InitializeResult = false,
        ScanResult = CaptureResult.Fail("SCANNER_NOT_CONNECTED", "No scanner connected"),
        VendorErrorCodeValue = "NO_DEVICE"
    };
    var handler = new CaptureHandler(null);

    ResetContextReady();
    var responseTask = SendHttpRequestAsync("{\"thamChieuId\":\"test\",\"maPhieu\":\"P001\"}");

    await WaitForContextAsync(5000);
    await handler.HandleAsync(_capturedContext, mock);

    var response = await GetResponseAsync(responseTask);
    Assert.Equal(503, (int)response.StatusCode);

    string json = await ReadResponseBodyAsync(response);
    var captureResponse = JsonConvert.DeserializeObject<CaptureResponse>(json);

    Assert.False(captureResponse.IsSuccess);
    Assert.Equal("SCANNER_NOT_CONNECTED", captureResponse.ErrorCode);
    Assert.Equal("NO_DEVICE", captureResponse.VendorErrorCode);
    Assert.NotNull(captureResponse.Timestamp);
}
```

### Real-Device Integration Tests (Skipped Gracefully)

**Scope:** Tests that require vendor SDK DLLs (e.g., `libzkfp.dll`) or physical hardware (e.g., ZK9500 scanner). Skip cleanly when not present so `dotnet test` stays green.

**Pattern:** `if (!adapter.Initialize()) { Console.WriteLine(...); return; }` — never throw, never fail, just log and skip assertions.

```csharp
// tests/FingerprintAgent.Tests/Scanner/ZKTecoDeviceIntegrationTests.cs:15
[Fact]
public void ZKTecoAdapter_Initializes_WhenDeviceConnected()
{
    var adapter = new ZKTecoAdapter();
    bool ok = adapter.Initialize();

    // Report the actual SDK state, not just pass/fail.
    string vendorError = adapter.VendorErrorCode;

    if (ok)
    {
        Assert.True(adapter.IsConnected);
        Assert.False(string.IsNullOrEmpty(adapter.DeviceId));
        Assert.False(string.IsNullOrEmpty(adapter.Model));
        Console.WriteLine($"[ZKTeco] CONNECTED: DeviceId={adapter.DeviceId}, Model={adapter.Model}");
    }
    else
    {
        Console.WriteLine($"[ZKTeco] NOT CONNECTED: VendorErrorCode={vendorError}");
    }
    adapter.Dispose();
}
```

**Harder-gate variant — `Assert.True` with diagnostic message:**

```csharp
// tests/FingerprintAgent.Tests/Scanner/ScannerManagerProbeIntegrationTests.cs:56
private void RequireDevice(string testName)
{
    Assert.True(_deviceAvailable,
        $"[{testName}] ZK9500 device not detected (VendorErrorCode={_adapter.VendorErrorCode}). " +
        "This test requires a real device — verify: (1) ZK9500 plugged into USB, " +
        "(2) ZKFinger SDK 5.3+ driver installed, (3) libzkfp.dll present in System32/SysWOW64, " +
        "(4) FingerprintAgent Windows service NOT running (holds device exclusively). " +
        "Do NOT mark this test as passing on machines without hardware — that defeats its purpose.");
}
```

Use `RequireDevice(...)` when a test MUST fail loudly if no device is present (gates the test on real hardware). Use the `if (ok) {...} else { Console.WriteLine... }` pattern when the test should run green even without hardware.

## Test Doubles

### Custom Test Doubles (Preferred Over Moq)

**Location:** `tests/FingerprintAgent.Tests/Scanner/MockScannerAdapterTestDoubles.cs`

**Why custom doubles?** `IScannerAdapter` returns `CaptureResult` whose setters are not virtual, so Moq cannot fully mock them. Custom doubles give you full control without `protected virtual` pollution.

**`MockScannerAdapterWithSettableProperties`** — the workhorse double for `ScannerManager` behavior tests:

```csharp
// tests/FingerprintAgent.Tests/Scanner/MockScannerAdapterTestDoubles.cs:14
public class MockScannerAdapterWithSettableProperties : IScannerAdapter
{
    public bool IsConnectedValue { get; set; } = true;
    public bool InitializeResult { get; set; } = true;
    public bool ProbeConnectionResult { get; set; } = true;
    public CaptureResult ScanResult { get; set; } = CaptureResult.Ok(new byte[] { 1, 2, 3 });
    public string VendorErrorCodeValue { get; set; } = "MOCK";
    public string DeviceIdValue { get; set; } = "mock-test-device";
    public string ModelValue { get; set; } = "Mock Scanner (Test Double)";
    public string MimeTypeValue { get; set; } = "image/png";

    public bool IsConnected => IsConnectedValue;
    public string DeviceId => DeviceIdValue;
    public string Model => ModelValue;
    public string MimeType => MimeTypeValue;

    public bool Initialize() => InitializeResult;
    public bool ProbeConnection() => ProbeConnectionResult;

    public CaptureResult Scan(CancellationToken cancellationToken = default) => ScanResult;

    public Task<CaptureResult> ScanAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(ScanResult);

    public string VendorErrorCode => VendorErrorCodeValue;
}
```

**Guidelines:**
- **Default values** for all settable properties → a freshly constructed double returns a successful `Ok` result.
- **Property setters (not constructors)** → easy mutation mid-test (e.g., flip `IsConnectedValue = false` to simulate disconnect).
- Implement both `Scan` (sync) and `ScanAsync` (async) to match `IScannerAdapter`'s contract.

### `CaptureHandlerTestFixture` — HttpListener Wrapper

**Purpose:** Creates a real `HttpListenerContext` on a random free port for direct handler integration testing. Reusable across tests that need a live `HttpListenerContext`.

**Location:** `tests/FingerprintAgent.Tests/Scanner/MockScannerAdapterTestDoubles.cs:46`

```csharp
// tests/FingerprintAgent.Tests/Scanner/MockScannerAdapterTestDoubles.cs:46
public class CaptureHandlerTestFixture : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Thread _serverThread;
    private readonly ManualResetEventSlim _contextReady = new ManualResetEventSlim(false);
    private HttpListenerContext _capturedContext;
    private bool _disposed;
    private readonly string _baseUrl;

    public HttpListenerContext CapturedContext => _capturedContext;
    public string BaseUrl => _baseUrl;

    public CaptureHandlerTestFixture()
    {
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _baseUrl = $"http://localhost:{((IPEndPoint)socket.LocalEndPoint).Port}/";
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();

        _serverThread = new Thread(() =>
        {
            try
            {
                _capturedContext = _listener.GetContext();
                _contextReady.Set();
                _contextReady.Wait(_disposed ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5));
            }
            catch (HttpListenerException) when (_disposed) { /* Expected on disposal */ }
        });
        _serverThread.IsBackground = true;
        _serverThread.Start();
    }

    public HttpListenerContext WaitForContext(int timeoutMs = 5000)
    {
        if (!_contextReady.Wait(timeoutMs))
            throw new TimeoutException($"HttpListener context not received within {timeoutMs}ms");
        return _capturedContext;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _contextReady.Set();
        _contextReady.Dispose();
        _serverThread.Join(2000);
    }
}
```

**Usage pattern:**
1. Construct `CaptureHandlerTestFixture` to start the listener.
2. Fire a `WebRequest` to `fixture.BaseUrl`.
3. Call `fixture.WaitForContext(5000)` to retrieve the `HttpListenerContext`.
4. Pass the captured context to the handler under test.
5. Read the response back from the `WebRequest`.

## xUnit Patterns

### `IClassFixture<T>` for Shared Resources

**When:** The fixture is expensive to construct (boots an `HttpServer`, allocates a port) and is read-only across tests.

**Pattern:** Outer fixture class implements `IDisposable`; inner test class takes it via constructor injection with `[Collection("name")]` to enable parallel-safety declarations.

```csharp
// tests/FingerprintAgent.Tests/Api/CorsMiddlewareTests.cs:24
public class WildcardModeFixture : IDisposable
{
    public HttpServer Server { get; }
    public MockScannerAdapter Scanner { get; }
    public HttpClient Client { get; }
    private bool _disposed;

    public WildcardModeFixture()
    {
        Scanner = new MockScannerAdapter();
        var config = new AgentConfig();
        config.Cors.Mode = "wildcard";
        config.Http.Port = 5045;
        Server = new HttpServer(config, Scanner);
        Server.Start();

        Client = new HttpClient();
        Client.BaseAddress = new Uri("http://127.0.0.1:5045");
        Client.Timeout = TimeSpan.FromSeconds(5);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Client?.Dispose();
            Server?.Stop();
            Server?.Dispose();
        }
    }
}

[Collection("WildcardMode")]
public class WildcardMode : IClassFixture<WildcardModeFixture>
{
    private readonly WildcardModeFixture _fixture;

    public WildcardMode(WildcardModeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Preflight_WithOrigin_Returns204()
    {
        var request = new HttpRequestMessage(new HttpMethod("OPTIONS"), "/api/capture");
        request.Headers.Add("Origin", "http://example.com");

        var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
    }
}
```

### `IDisposable` for Per-Test Cleanup

**When:** Each test needs fresh resources (e.g., a temp directory, an `HttpServer`, a `HttpListener`).

**Pattern:** Test class implements `IDisposable`; xUnit calls `Dispose` after every test. Guard with `_disposed` for idempotency.

```csharp
// tests/FingerprintAgent.Tests/Api/HttpServerIntegrationTests.cs:14
public class HttpServerIntegrationTests : IDisposable
{
    private readonly HttpServer _server;
    private readonly MockScannerAdapter _scanner;
    private readonly HttpClient _client;
    private readonly int _port;
    private bool _disposed;

    public HttpServerIntegrationTests()
    {
        // ... setup ...
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _client?.Dispose();
            _server?.Stop();
            _server?.Dispose();
        }
    }
}
```

### `async Task` for Async Tests

**Pattern:** All async test methods return `Task`. For non-throwing assertions, use `Record.Exception`:

```csharp
// tests/FingerprintAgent.Tests/Logging/AgentLoggerTests.cs:138
[Fact]
public void Log_EventLogSink_WritesToEventLog()
{
    // Non-elevated runs may not be able to create the source; test only checks it doesn't throw.
    using (var logger = CreateLogger("INFO"))
    {
        var ex = Record.Exception(() => logger.Info("evtid", "event log test"));
        Assert.Null(ex);
    }
}
```

### Assertion Library

xUnit's `Assert` class. Common patterns observed:

| Method | Purpose | Example location |
|---|---|---|
| `Assert.Equal(expected, actual)` | Value equality | `ScannerManagerTests.ExponentialBackoff.cs:21` |
| `Assert.True(condition)` / `Assert.False(condition)` | Boolean assertions | `MockScannerAdapterTests.cs:54` |
| `Assert.NotNull(value)` / `Assert.Null(value)` | Nullability | `MockScannerAdapterTests.cs:20` |
| `Assert.Single(collection)` | Single-item enumeration | `ConfigLoaderTests.cs:69` |
| `Assert.Empty(collection)` | Empty enumeration | `AgentLoggerTests.cs:77` |
| `Assert.Contains(substring, str)` / `Assert.DoesNotContain` | Substring | `AgentLoggerTests.cs:51` |
| `Assert.Matches(regex, str)` | Regex match | `AgentLoggerTests.cs:65` |
| `Assert.All(collection, assertion)` | Universal | `AgentLoggerTests.cs:181` |
| `Assert.Throws<T>(action)` | Exception type | `ConfigLoaderTests.cs:95` |
| `Record.Exception(action)` | No-throw assertion | `AgentLoggerTests.cs:143` |

## AAA Pattern (Arrange / Act / Assert)

**Convention:** Use `// Arrange`, `// Act`, `// Assert` comments to separate phases. Most prominent in `ConfigLoaderTests.cs`.

```csharp
// tests/FingerprintAgent.Tests/Configuration/ConfigLoaderTests.cs:19
[Fact]
public void Load_ValidConfig_ReturnsAgentConfigWithCorrectValues()
{
    // Arrange
    string configPath = Path.Combine(_tempDir, "config.json");
    File.WriteAllText(configPath, @"{
        ""service"": { ""name"": ""TestAgent"", ... },
        ...
    }");

    // Act
    var config = ConfigLoader.LoadFromDirectory(_tempDir);

    // Assert
    Assert.NotNull(config);
    Assert.Equal("TestAgent", config.Service.Name);
    ...
}
```

**Smaller unit tests** (e.g., `MockScannerAdapterTests`, `ScannerManagerTests.ExponentialBackoff.cs`) often skip the comment markers — the structure is implicit when setup is one line. Use comments when the test has three or more setup steps.

## Concurrent Testing

**Pattern:** Drive N concurrent calls to the system under test using `Task.WhenAll`, assert no interleaving corruption.

```csharp
// tests/FingerprintAgent.Tests/Logging/AgentLoggerTests.cs:164
[Fact]
public async Task Log_ConcurrentWrites_AreNotCorrupted()
{
    const int count = 100;
    using (var logger = CreateLogger("INFO"))
    {
        var tasks = new List<Task>();
        for (int i = 0; i < count; i++)
        {
            var n = i;
            tasks.Add(Task.Run(() => logger.Info($"cid{n:D3}", $"message {n}")));
        }

        await Task.WhenAll(tasks);
    }

    var lines = File.ReadAllLines(_logFile);
    Assert.Equal(count, lines.Length);
    Assert.All(lines, line => Assert.Matches(new Regex(@"\[INFO\] \[cid\d{3}\] message \d+"), line));
}
```

**Use this pattern for:**
- Locking primitives (`AgentLogger._lock`)
- Thread-safe collections (`HttpServer._inFlightRequests`)
- State mutation under contention (`ScannerManager._backoffLock`, `_adapterLock`)

## Temp Directory Isolation

**Pattern:** Use `Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))` to allocate a unique temp directory per test, clean up in `Dispose`.

```csharp
// tests/FingerprintAgent.Tests/Configuration/ConfigLoaderTests.cs:13
public ConfigLoaderTests()
{
    _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(_tempDir);
}

public void Dispose()
{
    if (!_disposed)
    {
        _disposed = true;
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }
}
```

**Used by:**
- `ConfigLoaderTests` — temp `config.json` per test
- `AgentLoggerTests` — temp log file per test
- `CorsMiddlewareTests` — no filesystem, but uses port-based isolation

## Random Free Port Discovery (TcpListener)

**Pattern:** Bind a `TcpListener` to port 0 to let the OS assign a free port, then read `LocalEndpoint` to discover it. Avoids hard-coded port conflicts between parallel test runs.

```csharp
// tests/FingerprintAgent.Tests/Api/HttpServerIntegrationTests.cs:25
var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
listener.Start();
_port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();

_server = new HttpServer("127.0.0.1", _port, _scanner);
```

**Alternative (HttpListener-friendly):**

```csharp
// tests/FingerprintAgent.Tests/Api/ErrorHandlingTests.cs:28
using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
{
    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    _baseUrl = string.Format("http://localhost:{0}/", ((IPEndPoint)socket.LocalEndPoint).Port);
}
```

**Note:** `HttpListener` does not expose its bound port without a prefix, so a raw `Socket`/`TcpListener` is needed to discover a free port before constructing `HttpListener.Prefixes`.

## Coverage

**Requirements:** **None enforced.** No `coverlet`/`altcover`/etc. package, no coverage thresholds in `.csproj`.

**Viewing coverage:** Run from IDE (`Test Explorer → Right-click → Analyze Code Coverage`) or add a coverage tool ad hoc.

**Implicit coverage strategy:** The test suite targets every public API surface:
- `MockScannerAdapterTests` covers `MockScannerAdapter`
- `ScannerManagerTests.ExponentialBackoff.cs` covers `ScannerManager.ScanAsync` and backoff state
- `ScannerManagerProbeIntegrationTests` covers `ScannerManager.TryProbe` (real device required)
- `HttpServerIntegrationTests` covers `HttpServer` routing
- `ErrorHandlingTests` covers `CaptureHandler` + `HealthHandler` (every error code path)
- `CorsMiddlewareTests` covers both CORS modes (wildcard + allowlist)
- `ConfigLoaderTests` covers `ConfigLoader.LoadFromDirectory` (valid config, missing file, invalid JSON, defaults)
- `AgentLoggerTests` covers `AgentLogger` (every level, correlation IDs, redaction, concurrency, directory creation)
- `ZKTecoDeviceIntegrationTests` covers `ZKTecoAdapter` against real hardware

## Test Counts by Folder

| Folder | Files | Tests |
|---|---|---|
| `Api/` | 3 | ~17 |
| `Configuration/` | 1 | 5 |
| `Logging/` | 1 | 11 |
| `Scanner/` | 7 | ~25 |
| **Total** | **12** | **58** |

Real-device tests (ZKTeco, ScannerManagerProbeIntegration) execute only when SDK DLLs are present and a ZK9500 is connected.

## Common Patterns — Quick Reference

**Async testing:**
```csharp
[Fact]
public async Task SomeAsyncMethod_Works() { await ... }
```

**Exception testing:**
```csharp
var ex = Assert.Throws<FileNotFoundException>(() => ConfigLoader.LoadFromDirectory(emptyDir));
Assert.Contains("config.json", ex.Message, StringComparison.OrdinalIgnoreCase);
```

**Disposable test class:**
```csharp
public class FooTests : IDisposable
{
    private bool _disposed;
    public void Dispose() { if (!_disposed) { _disposed = true; /* cleanup */ } }
}
```

**Skip on missing hardware/SDK:**
```csharp
if (!adapter.Initialize()) { Console.WriteLine("..."); return; }
Assert.True(adapter.IsConnected);
```

**Skip on missing hardware (hard-fail with diagnostic):**
```csharp
Assert.True(_deviceAvailable, "[TestName] Device not detected — see message for setup steps");
```

---

*Testing analysis: 2026-08-19*
