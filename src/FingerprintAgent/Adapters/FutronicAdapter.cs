#if FUTRONIC_SDK_PRESENT
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// Futronic Scanner adapter using P/Invoke ftrScanAPI.dll.
    /// Implements pixel inversion (255 - rawValue) per D-07 before PNG encoding.
    /// REVIEW NOTE (Futronic pixel inversion): ASSUMPTION — research cites "multiple sources" for inversion,
    /// not Futronic SDK documentation. If inversion is wrong, all Futronic images appear inverted.
    /// TODO (Phase 2 post-integrate): verify against known test fingerprint image — if display is inverted,
    /// inversion is incorrect and must be removed.
    /// </summary>
    public class FutronicAdapter : IScannerAdapter
    {
        private IntPtr _device;
        private int _imageWidth;
        private int _imageHeight;
        private string _deviceId;
        private string _model;
        private string _vendorErrorCode;
        private bool _isConnected;

        public bool IsConnected => _isConnected && _device != IntPtr.Zero;

        public string DeviceId => _deviceId ?? "no-device";

        public string Model => _model ?? "Futronic Scanner";

        public string MimeType => "image/png";

        public string VendorErrorCode => _vendorErrorCode ?? "NONE";

        public bool Initialize()
        {
            _vendorErrorCode = "NONE";
            _device = FutronicSDK.ftrScanOpenDevice();
            if (_device == IntPtr.Zero)
            {
                _vendorErrorCode = "DEVICE_OPEN_FAILED";
                _isConnected = false;
                return false;
            }

            FTRSCAN_IMAGE_SIZE size;
            if (!FutronicSDK.ftrScanGetImageSize(_device, out size))
            {
                _vendorErrorCode = "GET_SIZE_FAILED";
                FutronicSDK.ftrScanCloseDevice(_device);
                _device = IntPtr.Zero;
                _isConnected = false;
                return false;
            }

            _imageWidth = size.nWidth;
            _imageHeight = size.nHeight;

            // Get serial number
            byte[] serialBuf = new byte[32];
            if (FutronicSDK.ftrScanGetSerialNumber(_device, serialBuf))
            {
                _deviceId = Encoding.ASCII.GetString(serialBuf).TrimEnd('\0');
            }
            else
            {
                _deviceId = "unknown-serial";
            }

            _model = "Futronic Scanner";
            _isConnected = true;
            return true;
        }

        public CaptureResult Scan()
        {
            if (_device == IntPtr.Zero)
            {
                _vendorErrorCode = "NOT_INITIALIZED";
                return CaptureResult.Fail("SCANNER_NOT_CONNECTED", "Futronic scanner not initialized. Call Initialize() first.");
            }

            _vendorErrorCode = "NONE";

            byte[] rawBuffer = new byte[_imageWidth * _imageHeight];
            bool ok = FutronicSDK.ftrScanGetImage(_device, 4, rawBuffer);
            if (!ok)
            {
                uint err = FutronicSDK.ftrScanGetLastError();
                _vendorErrorCode = MapErrorCode(err);
                return CaptureResult.Fail("CAPTURE_ERROR", $"Futronic:{_vendorErrorCode}");
            }

            // CRITICAL: invert pixels per D-07
            byte[] inverted = new byte[rawBuffer.Length];
            for (int i = 0; i < rawBuffer.Length; i++)
                inverted[i] = (byte)(255 - rawBuffer[i]);

            // Convert to PNG using BaseScannerAdapter helper (inversion already done)
            byte[] png = ToPngGrayscale(inverted, _imageWidth, _imageHeight);

            string verificationData;
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(png);
                verificationData = Convert.ToBase64String(hash);
            }

            return new CaptureResult
            {
                IsSuccess = true,
                ImageBytes = png,
                MimeType = "image/png",
                CapturedAt = DateTime.UtcNow.ToString("O"),
                DeviceId = DeviceId,
                VerificationData = verificationData,
                ErrorMessage = null,
                Width = _imageWidth,
                Height = _imageHeight
            };
        }

        private static byte[] ToPngGrayscale(byte[] raw, int width, int height)
        {
            using (var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format8bppIndexed))
            {
                var palette = bitmap.Palette;
                for (int i = 0; i < 256; i++)
                    palette.Entries[i] = System.Drawing.Color.FromArgb(i, i, i);
                bitmap.Palette = palette;

                var bitmapData = bitmap.LockBits(
                    new System.Drawing.Rectangle(0, 0, width, height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format8bppIndexed);

                Marshal.Copy(raw, 0, bitmapData.Scan0, raw.Length);
                bitmap.UnlockBits(bitmapData);

                using (var ms = new MemoryStream())
                {
                    bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        private static string MapErrorCode(uint err)
        {
            unchecked
            {
                switch (err)
                {
                    case 0x200010E2: return "FTR_ERROR_EMPTY_FRAME";
                    case 0x20000001: return "FTR_ERROR_MOVABLE_FINGER";
                    case 0x20000002: return "FTR_ERROR_NO_FRAME";
                    case 0x20000003: return "FTR_ERROR_USER_CANCELED";
                    case 0x20000004: return "FTR_ERROR_HARDWARE_INCOMPATIBLE";
                    case 0x20000005: return "FTR_ERROR_FIRMWARE_INCOMPATIBLE";
                    case 0x20000006: return "FTR_ERROR_INVALID_AUTHORIZATION_CODE";
                    default: return $"0x{err:X}";
                }
            }
        }

        #region P/Invoke Declarations

        private static class FutronicSDK
        {
            private const string DllName = "ftrScanAPI.dll";

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern IntPtr ftrScanOpenDevice();

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern void ftrScanCloseDevice(IntPtr device);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern bool ftrScanGetImageSize(IntPtr device, out FTRSCAN_IMAGE_SIZE imageSize);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern bool ftrScanGetImage(IntPtr device, uint nDose, byte[] pBuffer);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern uint ftrScanGetLastError(IntPtr device);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern bool ftrScanGetSerialNumber(IntPtr device, byte[] pSerialBuffer);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern bool ftrScanGetVersionInfo(IntPtr device, ref FTRSCAN_VERSION_INFO versionInfo);

            [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
            public static extern bool ftrScanGetDeviceInfo(IntPtr device, ref FTRSCAN_DEVICE_INFO deviceInfo);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct FTRSCAN_IMAGE_SIZE
        {
            public int nWidth;
            public int nHeight;
            public int nImageSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct FTRSCAN_DEVICE_INFO
        {
            public int nBytes;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] bcdDevice;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] bcdSensor;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct FTRSCAN_FRAME_PARAMETERS
        {
            public int nProgress;
            public int nQuality;
            public uint nFlags;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct FTRSCAN_VERSION_INFO
        {
            public int nMajor;
            public int nMinor;
            public int nBuild;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] bData;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct FTRSCAN_VERSION
        {
            public int nMajor;
            public int nMinor;
            public int nBuild;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] cVersion;
        }

        #endregion
    }
}
#else
// Stub implementation when FUTRONIC_SDK_PRESENT is not defined.
// Allows compilation and unit testing without the vendor SDK DLL present.
namespace FingerprintAgent.Adapters
{
    public class FutronicAdapter : IScannerAdapter
    {
        public bool IsConnected => false;
        public string DeviceId => "stub-device";
        public string Model => "Futronic (stub)";
        public string MimeType => "image/png";
        public string VendorErrorCode => "NONE";

        public bool Initialize()
        {
            return false;
        }

        public CaptureResult Scan()
        {
            return CaptureResult.Fail("SCANNER_NOT_CONNECTED", "Futronic: Stub adapter — SDK not present");
        }
    }
}
#endif