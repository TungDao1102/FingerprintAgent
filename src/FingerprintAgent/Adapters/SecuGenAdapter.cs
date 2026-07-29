using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

#if !SECUGEN_SDK_PRESENT
// Stub types for compilation without the SecuGen SDK DLL.
// When the DLL is present, these are replaced by the actual SDK types.
internal enum SGFPMDeviceName { DEV_AUTO = 1 }
internal enum SGFPMPortAddr { USB_AUTO_DETECT = 0x28 }
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct SGDevInfo
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string DevName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string DeviceSerialNumber;
    public Int32 ImageWidth;
    public Int32 ImageHeight;
}
internal class SGFingerPrintManager
{
    public Int32 Init(SGFPMDeviceName name) => 55;
    public Int32 OpenDevice(Int32 port) => 55;
    public Int32 EnumerateDevice(ref Int32 count, [MarshalAs(UnmanagedType.LPArray)] SGDevInfo[] devices) { count = 0; return 55; }
    public Int32 GetImageEx(byte[] buffer, Int32 timeout, IntPtr hWnd, Int32 quality) => 55;
}
#endif

namespace FingerprintAgent.Adapters
{
    public class SecuGenAdapter : BaseScannerAdapter
    {
        private SGFingerPrintManager _fpm;
        private int _width;
        private int _height;
        private string _deviceId;
        private string _model;
        private bool _isConnected;
        private string _vendorErrorCode;

        private static readonly Dictionary<Int32, string> _errorStrings =
            new Dictionary<Int32, string>
            {
                { 0,   "ERROR_NONE" },
                { 1,   "ERROR_CREATION_FAILED" },
                { 2,   "ERROR_FUNCTION_FAILED" },
                { 5,   "ERROR_DLLLOAD_FAILED" },
                { 6,   "ERROR_DLLLOAD_FAILED_DRV" },
                { 7,   "ERROR_DLLLOAD_FAILED_ALGO" },
                { 51,  "ERROR_SYSLOAD_FAILED" },
                { 52,  "ERROR_INITIALIZE_FAILED" },
                { 54,  "ERROR_TIME_OUT" },
                { 55,  "ERROR_DEVICE_NOT_FOUND" },
                { 56,  "ERROR_DRVLOAD_FAILED" },
                { 57,  "ERROR_WRONG_IMAGE" },
                { 58,  "ERROR_LACK_OF_BANDWIDTH" },
                { 59,  "ERROR_DEV_ALREADY_OPEN" },
                { 60,  "ERROR_GETSN_FAILED" },
                { 61,  "ERROR_UNSUPPORTED_DEV" }
            };

        public override bool IsConnected => _isConnected;
        public override string DeviceId => _deviceId ?? "";
        public override string Model => _model ?? "SecuGen Device";

        protected override int ImageWidth => _width;
        protected override int ImageHeight => _height;

        public override bool InitializeDevice()
        {
            if (_fpm != null)
            {
                (_fpm as IDisposable)?.Dispose();
                _fpm = null;
            }
            _fpm = new SGFingerPrintManager();
            Int32 err = _fpm.Init(SGFPMDeviceName.DEV_AUTO);
            if (err != 0)
            {
                _fpm = null;
                _vendorErrorCode = MapError(err);
                return false;
            }

            err = _fpm.OpenDevice((Int32)SGFPMPortAddr.USB_AUTO_DETECT);
            if (err != 0)
            {
                _vendorErrorCode = MapError(err);
                return false;
            }

            Int32 deviceCount = 0;
            _fpm.EnumerateDevice(ref deviceCount, null);
            if (deviceCount == 0)
            {
                _vendorErrorCode = MapError(55);
                return false;
            }

            SGDevInfo[] deviceList = new SGDevInfo[deviceCount];
            _fpm.EnumerateDevice(ref deviceCount, deviceList);

            if (deviceCount > 0)
            {
                var info = deviceList[0];
                _deviceId = "SecuGen-" + info.DeviceSerialNumber;
                _model = info.DevName.TrimEnd('\0');
                _width = info.ImageWidth;
                _height = info.ImageHeight;
            }
            else
            {
                _deviceId = "SecuGen-unknown";
                _model = "SecuGen Device";
                _width = 260;
                _height = 300;
            }

            _isConnected = true;
            _vendorErrorCode = "NONE";
            return true;
        }

        public override byte[] CaptureRawImage()
        {
            if (_fpm == null)
                return null;

            byte[] buffer = new byte[_width * _height];
            Int32 quality = 80;
            Int32 err = _fpm.GetImageEx(buffer, 5000, IntPtr.Zero, quality);
            if (err != 0)
            {
                _vendorErrorCode = MapError(err);
                return null;
            }

            return buffer;
        }

        private string MapError(Int32 code)
        {
            return _errorStrings.TryGetValue(code, out var str) ? str : $"ERROR_UNKNOWN_{code}";
        }
    }
}