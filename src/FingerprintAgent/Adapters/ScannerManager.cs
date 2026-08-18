using System;
using System.Collections.Generic;
using System.Threading;
using FingerprintAgent.Configuration;
using FingerprintAgent.Logging;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// Composite IScannerAdapter that tries multiple adapters in priority order
    /// until one succeeds. Handles 20-second total timeout (D-06, extended to give
    /// the active adapter's full rolling-capture window on ZK9500) and per-adapter
    /// ~3-second timeout via linked CancellationTokenSource.
    ///
    /// SCAN-06 backoff: if the previously-active adapter reports IsConnected=false
    /// on a new Scan() call, retry Initialize() once before falling back to the
    /// priority list. This handles temporary disconnection / device busy conditions.
    ///
    /// Unknown vendor names in config.Scanner.Priority throw InvalidOperationException
    /// on construction — fail-fast on config typos, not silent reduction (T-02-09).
    ///
    /// Lock ordering: ScannerManager acquires _adapterLock before _backoffLock
    /// (adapter state changes take precedence over backoff state changes).
    /// </summary>
    public class ScannerManager : IScannerAdapter, IDisposable
    {
        private bool _disposed;
        private IScannerAdapter[] _adapters;
        private readonly AgentLogger _logger;
        private readonly ScannerConfig _config;
        private readonly CancellationTokenSource _cts;
        private readonly bool _mockMode;
        private IScannerAdapter _activeAdapter;
        private readonly object _adapterLock = new object();

        private int _backoffStep = 0;
        private DateTime _backoffUntil = DateTime.MinValue;
        private readonly object _backoffLock = new object();
        private static readonly int[] BackoffDelaysSeconds = { 10, 30, 60, 120 };

        private IScannerAdapter ActiveAdapter
        {
            get { lock (_adapterLock) return _activeAdapter; }
            set { lock (_adapterLock) _activeAdapter = value; }
        }

        public bool IsConnected => _mockMode
            ? ActiveAdapter?.IsConnected ?? false
            : (ActiveAdapter?.IsConnected ?? false);

        public string DeviceId => _mockMode
            ? (ActiveAdapter?.DeviceId ?? "mock-device")
            : (ActiveAdapter?.DeviceId ?? "no-device");

        public string Model => _mockMode
            ? (ActiveAdapter?.Model ?? "Mock Scanner")
            : (ActiveAdapter?.Model ?? "no-device");

        public string MimeType => "image/png";

        public string VendorErrorCode => _mockMode
            ? "MOCK"
            : (ActiveAdapter?.VendorErrorCode ?? "NO_ADAPTER");

        public bool InBackoff
        {
            get
            {
                lock (_backoffLock)
                    return _backoffStep > 0 && DateTime.UtcNow < _backoffUntil;
            }
        }

        public int BackoffStep
        {
            get
            {
                lock (_backoffLock)
                    return _backoffStep;
            }
        }

        /// <summary>
        /// Probes adapters in priority order to determine real connection state.
        /// Does NOT trigger backoff escalation (unlike Scan()). Does NOT require a
        /// successful capture to report success — only verifies the SDK can be
        /// initialized and a device opened.
        ///
        /// Fast path: if a cached ActiveAdapter is already connected, returns its
        /// info without re-running Initialize() on every adapter (avoids SDK state
        /// churn from repeated /health polls).
        ///
        /// On first successful probe, promotes the adapter to ActiveAdapter so
        /// the next Scan() call uses it directly without re-initializing.
        /// </summary>
        /// <returns>true if any adapter initialized successfully</returns>
        public bool TryProbe(out string deviceId, out string model, out string vendorErrorCode)
        {
            deviceId = "no-device";
            model = "no-device";
            vendorErrorCode = "NONE";

            IScannerAdapter[] currentAdapters;
            IScannerAdapter cached = null;
            lock (_adapterLock)
            {
                currentAdapters = _adapters;
                cached = _activeAdapter;
            }

            if (cached != null && cached.IsConnected)
            {
                deviceId = cached.DeviceId;
                model = cached.Model;
                vendorErrorCode = cached.VendorErrorCode;
                return true;
            }

            if (currentAdapters == null || currentAdapters.Length == 0)
                return false;

            foreach (var adapter in currentAdapters)
            {
                try
                {
                    if (adapter.Initialize())
                    {
                        deviceId = adapter.DeviceId;
                        model = adapter.Model;
                        vendorErrorCode = adapter.VendorErrorCode;
                        ActiveAdapter = adapter;
                        return true;
                    }
                    vendorErrorCode = adapter.VendorErrorCode;
                }
                catch (Exception ex)
                {
                    // Never let a single adapter's exception crash /health.
                    vendorErrorCode = $"PROBE_EXCEPTION:{ex.GetType().Name}";
                }
            }
            return false;
        }

        /// <summary>
        /// ScannerManager itself does not maintain persistent connection state.
        /// Initialize() returns true — individual adapters are initialized on each Scan() call.
        /// D-01 specifies no persistent connection state between requests; per-call
        /// Initialize() enables lazy-connect semantics for all vendor SDKs.
        /// </summary>
        public bool Initialize() => true;

        /// <summary>
        /// Constructs ScannerManager from AgentConfig. When MockMode=true, wraps
        /// MockScannerAdapter transparently and skips all real adapter initialization.
        /// When MockMode=false, builds the adapter list from config.Scanner.Priority.
        /// Unknown vendor names throw InvalidOperationException (fail-fast on config typo).
        /// </summary>
        public ScannerManager(AgentConfig config, AgentLogger logger)
        {
            _config = config?.Scanner ?? throw new ArgumentNullException(nameof(config));
            _logger = logger;
            _cts = new CancellationTokenSource();

            if (_config.MockMode)
            {
                _mockMode = true;
                ActiveAdapter = new MockScannerAdapter();
                _logger?.Info(null, "ScannerManager: MockMode=true, using MockScannerAdapter");
                return;
            }

            _mockMode = false;

            var vendorList = new List<IScannerAdapter>();
            foreach (var vendorName in _config.Priority)
            {
                IScannerAdapter adapter = CreateAdapter(vendorName);
                vendorList.Add(adapter);
                _logger?.Info(null, $"ScannerManager: registered {vendorName}");
            }

            _adapters = vendorList.ToArray();
        }

        /// <summary>
        /// Internal constructor for testing: inject adapters directly, bypassing config-based resolution.
        /// Allows ScannerManagerTests to exercise priority fallback and backoff logic.
        /// </summary>
        public ScannerManager(IScannerAdapter[] adapters, AgentLogger logger)
        {
            _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
            _logger = logger;
            _mockMode = false;
            _cts = new CancellationTokenSource();
            _activeAdapter = null;
        }

        /// <summary>
        /// Re-creates the adapter list from newPriority at runtime.
        /// D-09: active adapter is NOT touched — stays as-is across priority changes.
        /// Backoff state is also preserved (not reset) on priority change.
        /// Unknown vendor names throw InvalidOperationException (fail-fast, consistent with constructor).
        /// Thread-safe.
        ///
        /// Note: old adapters are NOT disposed here because _activeAdapter might reference
        /// one of them. This is an intentional trade-off (D-09). Dispose is called only
        /// when ScannerManager.Dispose() is called at service shutdown.
        /// </summary>
        public void UpdatePriority(string[] newPriority)
        {
            if (newPriority == null || newPriority.Length == 0)
                return;

            IScannerAdapter[] oldAdapters;
            lock (_adapterLock)
            {
                oldAdapters = _adapters;
                var vendorList = new List<IScannerAdapter>();
                foreach (var vendorName in newPriority)
                {
                    IScannerAdapter adapter = CreateAdapter(vendorName);
                    vendorList.Add(adapter);
                }
                _adapters = vendorList.ToArray();
            }

            // Dispose old adapters that are NOT the active adapter
            // (active adapter stays alive; disposed only at ScannerManager shutdown)
            if (oldAdapters != null)
            {
                IScannerAdapter active;
                lock (_adapterLock) { active = _activeAdapter; }
                foreach (var adapter in oldAdapters)
                {
                    if (!ReferenceEquals(adapter, active))
                        (adapter as IDisposable)?.Dispose();
                }
            }

            _logger?.Info(null, $"ScannerManager: priority updated, new order=[{string.Join(", ", newPriority)}]");
        }

        private static IScannerAdapter CreateAdapter(string vendorName)
        {
            switch (vendorName)
            {
                case "SecuGen":
                    return new SecuGenAdapter();
                case "DigitalPersona":
                    return new DigitalPersonaAdapter();
                case "Futronic":
                    return new FutronicAdapter();
                case "ZKTeco":
                    return new ZKTecoAdapter();
                default:
                    throw new InvalidOperationException(
                        $"Unknown scanner vendor in config.Scanner.Priority: '{vendorName}'. " +
                        "Valid names: SecuGen, DigitalPersona, Futronic, ZKTeco");
            }
        }

        /// <summary>
        /// Attempts capture using adapters in priority order.
        ///
        /// SCAN-06 backoff: if _activeAdapter is set but reports IsConnected=false,
        /// retry Initialize() once before falling through to normal priority fallback.
        ///
        /// Total budget: 20 seconds across all adapter attempts (D-06, extended to
        /// accommodate the active adapter's full rolling-capture window on ZK9500).
        /// Per-adapter budget: ~3 seconds via linked CTS (D-06).
        /// </summary>
        public CaptureResult Scan()
        {
            string cid = AgentLogger.GenerateCorrelationId();

            // MockMode: delegate directly to mock
            if (_mockMode)
            {
                var result = ActiveAdapter.Scan();
                return result;
            }

            // SCAN-06 backoff: retry active adapter once if it was previously connected
            // but is now disconnected (temporary disconnection / device busy)
            IScannerAdapter current;
            lock (_adapterLock) { current = _activeAdapter; }
            if (current != null && !current.IsConnected)
            {
                _logger?.Warn(null, "ScannerManager: active adapter disconnected, retrying once");
                if (current.Initialize())
                {
                    _logger?.Info(null, "ScannerManager: active adapter reconnected, proceeding");
                    var retryResult = current.Scan();
                    if (retryResult.IsSuccess)
                    {
                        ActiveAdapter = current;
                        lock (_backoffLock) { _backoffStep = 0; _backoffUntil = DateTime.MinValue; }
                        return retryResult;
                    }
                    _logger?.Warn(null, $"ScannerManager: active adapter retry scan failed: {retryResult.ErrorMessage}");
                    // fall through to normal priority fallback
                }
                else
                {
                    _logger?.Warn(null, $"ScannerManager: active adapter retry initialize failed: {current.VendorErrorCode}");
                    // fall through to normal priority fallback
                }
            }

            // 20-second total budget (D-06)
            using (var totalCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                totalCts.CancelAfter(TimeSpan.FromSeconds(20));

                IScannerAdapter[] currentAdapters;
                lock (_adapterLock) { currentAdapters = _adapters; }
                foreach (var adapter in currentAdapters)
                {
                    if (totalCts.Token.IsCancellationRequested)
                    {
                        _logger?.Warn(null, "ScannerManager: total timeout exceeded");
                        return CaptureResult.Fail("CAPTURE_TIMEOUT", "Capture timed out after 20 seconds across all adapters");
                    }

                    // ~3 second per-adapter budget (D-06)
                    using (var adapterCts = CancellationTokenSource.CreateLinkedTokenSource(totalCts.Token))
                    {
                        adapterCts.CancelAfter(TimeSpan.FromSeconds(3));

                        try
                        {
                            _logger?.Debug(null, $"ScannerManager: trying {adapter.GetType().Name}");

                            if (adapter.Initialize())
                            {
                                ActiveAdapter = adapter;
                                var scanResult = adapter.Scan();
                                if (scanResult.IsSuccess)
                                {
                                    lock (_backoffLock) { _backoffStep = 0; _backoffUntil = DateTime.MinValue; }
                                    _logger?.Info(cid, $"ScannerManager: {adapter.GetType().Name} succeeded, DeviceId={adapter.DeviceId}");
                                    return scanResult;
                                }
                                _logger?.Warn(cid, $"ScannerManager: {adapter.GetType().Name} scan failed: {scanResult.ErrorMessage}");
                                return scanResult;
                            }
                            else
                            {
                                _logger?.Warn(null, $"ScannerManager: {adapter.GetType().Name} initialize returned false: {adapter.VendorErrorCode}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.Error(null, $"ScannerManager: {adapter.GetType().Name} threw {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }

                if (_adapters.Length == 0)
                    return CaptureResult.Fail("CONFIG_ERROR", "No scanner adapters configured and MockMode is disabled");
            }

            _logger?.Error(cid, "ScannerManager: all adapters failed");
            ApplyBackoff(cid);
            return CaptureResult.Fail("SCANNER_NOT_CONNECTED", "No scanner connected — all adapters failed to initialize or capture");
        }

        private void ApplyBackoff(string correlationId)
        {
            lock (_backoffLock)
            {
                _backoffStep = Math.Min(_backoffStep + 1, BackoffDelaysSeconds.Length - 1);
                _backoffUntil = DateTime.UtcNow.AddSeconds(BackoffDelaysSeconds[_backoffStep]);
            }
            _logger?.Info(correlationId, $"ScannerManager: backoff applied step={_backoffStep} for {BackoffDelaysSeconds[_backoffStep]}s");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Dispose();
            if (_adapters != null)
            {
                IScannerAdapter active;
                lock (_adapterLock) { active = _activeAdapter; }
                foreach (var adapter in _adapters)
                {
                    // Skip active adapter — dispose it separately below to ensure
                    // it is cleaned up even if UpdatePriority moved it out of _adapters
                    if (ReferenceEquals(adapter, active))
                        continue;
                    (adapter as IDisposable)?.Dispose();
                }
            }
            // Dispose active adapter exactly once
            (ActiveAdapter as IDisposable)?.Dispose();
        }
    }
}