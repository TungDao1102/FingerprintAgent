using System;

namespace FingerprintAgent.Adapters
{
    public class MockScannerAdapter : IScannerAdapter
    {
        public bool IsConnected => false;
        public string DeviceId => string.Empty;
        public string Model => string.Empty;
        public string MimeType => string.Empty;

        public CaptureResult Scan()
        {
            throw new NotImplementedException("MockScannerAdapter not yet implemented");
        }
    }
}
