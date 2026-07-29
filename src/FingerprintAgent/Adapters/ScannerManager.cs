using System;
using System.Collections.Generic;
using System.Threading;
using FingerprintAgent.Configuration;
using FingerprintAgent.Logging;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// Composite IScannerAdapter that tries multiple adapters in priority order
    /// until one succeeds. Handles 10-second total timeout (D-06) and per-adapter
    /// ~3-second timeout via linked CancellationTokenSource.
    ///
    /// SCAN-06 backoff: if the previously-active adapter reports IsConnected=false
    /// on a new Scan() call, retry Initialize() once before falling back to the
    /// priority list. This handles temporary disconnection / device busy conditions.
    ///
    /// Unknown vendor names in config.Scanner.Priority throw InvalidOperationException
    /// on construction — fail-fast on config typos, not silent reduction (T-02-09).
    /// </summary>
    public class ScannerManager : IScannerAdapter, IDisposable
    {
        private bool _disposed;
        private readonly IScannerAdapter[] _adapters;
        private readonly AgentLogger _logger;
        private readonly ScannerConfig _config;
        private readonly CancellationTokenSource _cts;
        private readonly bool _mockMode;
        private IScannerAdapter _activeAdapter;

        public bool IsConnected => _mockMode
            ? _activeAdapter?.IsConnected ?? false
            : (_activeAdapter?.IsConnected ?? false);

        public string DeviceId => _mockMode
            ? (_activeAdapter?.DeviceId ?? "mock-device")
            : (_activeAdapter?.DeviceId ?? "no-device");

        public string Model => _mockMode
            ? (_activeAdapter?.Model ?? "Mock Scanner")
            : (_activeAdapter?.Model ?? "no-device");

        public string MimeType => "image/png";

        public string VendorErrorCode => _mockMode
            ? "MOCK"
            : (_activeAdapter?.VendorErrorCode ?? "NO_ADAPTER");

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
                _activeAdapter = new MockScannerAdapter();
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
        /// Total budget: 10 seconds across all adapter attempts (D-06).
        /// Per-adapter budget: ~3 seconds via linked CTS (D-06).
        /// </summary>
        public CaptureResult Scan()
        {
            // MockMode: delegate directly to mock
            if (_mockMode)
            {
                var result = _activeAdapter.Scan();
                return result;
            }

            // SCAN-06 backoff: retry active adapter once if it was previously connected
            // but is now disconnected (temporary disconnection / device busy)
            if (_activeAdapter != null && !_activeAdapter.IsConnected)
            {
                _logger?.Warn(null, "ScannerManager: active adapter disconnected, retrying once");
                if (_activeAdapter.Initialize())
                {
                    _logger?.Info(null, "ScannerManager: active adapter reconnected, proceeding");
                    var retryResult = _activeAdapter.Scan();
                    if (retryResult.IsSuccess)
                    {
                        return retryResult;
                    }
                    _logger?.Warn(null, $"ScannerManager: active adapter retry scan failed: {retryResult.ErrorMessage}");
                    // fall through to normal priority fallback
                }
                else
                {
                    _logger?.Warn(null, $"ScannerManager: active adapter retry initialize failed: {_activeAdapter.VendorErrorCode}");
                    // fall through to normal priority fallback
                }
            }

            // 10-second total budget (D-06)
            using (var totalCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                totalCts.CancelAfter(TimeSpan.FromSeconds(10));

                foreach (var adapter in _adapters)
                {
                    if (totalCts.Token.IsCancellationRequested)
                    {
                        _logger?.Warn(null, "ScannerManager: total timeout exceeded");
                        return CaptureResult.Fail("CAPTURE_TIMEOUT", "Capture timed out after 10 seconds across all adapters");
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
                                var result = adapter.Scan();
                                if (result.IsSuccess)
                                {
                                    _activeAdapter = adapter;
                                    _logger?.Info(null, $"ScannerManager: {adapter.GetType().Name} succeeded, DeviceId={adapter.DeviceId}");
                                    return result;
                                }
                                else
                                {
                                    _logger?.Warn(null, $"ScannerManager: {adapter.GetType().Name} scan failed: {result.ErrorMessage}");
                                }
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

            _logger?.Error(null, "ScannerManager: all adapters failed");
            return CaptureResult.Fail("SCANNER_NOT_CONNECTED", "No scanner connected — all adapters failed to initialize or capture");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Dispose();
            (_activeAdapter as IDisposable)?.Dispose();
        }
    }
}