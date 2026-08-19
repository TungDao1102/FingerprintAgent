using System.Threading.Tasks;

namespace FingerprintAgent.Adapters
{
    public interface IScannerAdapter
    {
        bool IsConnected { get; }
        string DeviceId { get; }
        string Model { get; }
        string MimeType { get; }

        /// <summary>
        /// Initializes the scanner device. Called by ScannerManager before each Scan().
        /// Returns true if the device is ready; false otherwise.
        /// </summary>
        bool Initialize();

        /// <summary>
        /// Lightweight real-time connection check (~1-10ms). Re-verifies the cached
        /// IsConnected flag against actual device state without opening or closing the
        /// device. Updates IsConnected to false if the device was disconnected.
        ///
        /// Default implementation (in BaseScannerAdapter) returns the cached IsConnected
        /// flag, which is correct for adapters without a native "query device count"
        /// SDK call. Adapters that can do so (e.g. ZKTeco's ZkTecoFingerHost.GetDeviceCount)
        /// override this for real-time verification — without it, /health reports stale
        /// "healthy" state until the next /api/capture triggers full re-initialization.
        /// </summary>
        bool ProbeConnection();

        /// <summary>
        /// Human-readable SDK error string set by Initialize() and Scan().
        /// Returns "NONE" when no error has occurred.
        /// </summary>
        string VendorErrorCode { get; }

        /// <summary>
        /// Performs capture asynchronously. Optional CancellationToken enables ScannerManager's per-adapter
        /// 3s budget (D-06). Adapters should honor cancellation at the next checkpoint
        /// — not interrupt mid-native-call (corrupts native state).
        /// </summary>
        Task<CaptureResult> ScanAsync(System.Threading.CancellationToken cancellationToken = default);
    }
}
