namespace FingerprintAgent.Adapters
{
    public interface IScannerAdapter
    {
        bool IsConnected { get; }
        string DeviceId { get; }
        string Model { get; }

        CaptureResult Scan();

        string MimeType { get; }
    }
}
