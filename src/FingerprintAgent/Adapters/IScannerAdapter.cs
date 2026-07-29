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
        /// Human-readable SDK error string set by Initialize() and Scan().
        /// Returns "NONE" when no error has occurred.
        /// </summary>
        string VendorErrorCode { get; }

        CaptureResult Scan();
    }
}
