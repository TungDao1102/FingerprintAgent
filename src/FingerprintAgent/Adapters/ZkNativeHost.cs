using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// Raw P/Invoke layer for libzkfp.dll (ZKTeco fingerprint SDK).
    /// Replaces ZkTecoFingerPrint NuGet wrapper (v1.2.1) — eliminates transitive
    /// deps (SourceAFIS, System.Reactive, Dahomey.Cbor) and fixes W1/W2/W5.
    ///
    /// Prior art:
    /// - tests/.../Scanner/ZkSdkProbe.cs: 6/7 DllImport verified on real ZK9500
    /// - Commit 4c7c358: ZKFPM_AcquireFingerprint signature (StdCall, template 2048)
    /// - Wrapper source rainxh11/ZkTecoFingerPrint@31e5941: GetCaptureParamsEx, param 1102/1103
    /// </summary>
    internal static class ZkNativeHost
    {
        // CallingConvention: default Winapi = StdCall on x86. NEVER set Cdecl (F2).

        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_Init();

        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_Terminate();

        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_GetDeviceCount();

        /// <summary>
        /// Returns raw int handle. Positive = valid; zero OR NEGATIVE = fail.
        /// CRITICAL GUARD: check &gt; 0, not just != 0 — ZkSdkProbe.cs:64-75 observed
        /// negative handles when device held by another process. A negative value
        /// passed to AcquireFingerprint = undefined behavior.
        /// </summary>
        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_OpenDevice(int index);

        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_CloseDevice(IntPtr handle);

        /// <summary>Blocking acquire (~1s on ZK9500). Caller wraps in Task.Run + try/finally FreeHGlobal.</summary>
        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_AcquireFingerprint(
            IntPtr hDevice, IntPtr fpImage, uint cbFPImage,
            IntPtr fpTemplate, ref uint cbTemplate);

        /// <summary>
        /// Read parameter by code. Codes verified on ZK9500:
        /// 1=width, 2=height, 3=dpi, 1102=product_name, 1103=serial_number.
        /// </summary>
        [DllImport("libzkfp.dll")]
        private static extern int ZKFPM_GetParameters(
            IntPtr hDev, int nParamCode, byte[] paramValue, ref uint cbParamValue);

        internal const int ZKFP_OK = 0;
        internal const int ZKFP_ALREADY_INIT = 1;   // F5: host usable
        internal const int ZKFP_ERR_INITLIB = -1;
        internal const int ZKFP_ERR_INIT = -2;
        internal const int ZKFP_ERR_NO_DEVICE = -3;
        internal const int ZKFP_ERR_NOT_SUPPORT = -4;
        internal const int ZKFP_ERR_INVALID_PARAM = -5;
        internal const int ZKFP_ERR_OPEN = -6;
        internal const int ZKFP_ERR_INVALID_HANDLE = -7;
        internal const int ZKFP_ERR_CAPTURE = -8;
        internal const int ZKFP_ERR_EXTRACT_FP = -9;
        internal const int ZKFP_ERR_ABORT = -10;
        internal const int ZKFP_ERR_MEMORY = -11;
        internal const int ZKFP_ERR_BUSY = -12;
        internal const int ZKFP_ERR_ADD_FINGER = -13;
        internal const int ZKFP_ERR_DELETE_FINGER = -14;
        internal const int ZKFP_ERR_FAIL = -17;
        internal const int ZKFP_ERR_CANCEL = -18;
        internal const int ZKFP_ERR_VERIFY = -20;
        internal const int ZKFP_ERR_MERGE = -22;
        internal const int ZKFP_ERR_NOT_OPENED = -23;
        internal const int ZKFP_ERR_NOT_INIT = -24;
        internal const int ZKFP_ERR_ALREADY_OPENED = -25;
        internal const int ZKFP_ERR_LOAD_IMAGE = -26;
        internal const int ZKFP_ERR_ANALYZE_IMAGE = -27;
        internal const int ZKFP_ERR_TIMEOUT = -28;

        // SDK parameter codes (empirically verified on ZK9500 / ZK SDK 5.3; see tests/.../ZkSdkProbe.cs)
        internal const int PARAM_WIDTH = 1;     // Image width in pixels
        internal const int PARAM_HEIGHT = 2;     // Image height in pixels
        internal const int PARAM_DPI = 3;     // Sensor DPI
        internal const int PARAM_PRODUCT_NAME = 1102;  // Product name (UTF-8 string)
        internal const int PARAM_SERIAL_NUMBER = 1103;  // Serial number (UTF-8 string)

        internal static int Initialize() => ZKFPM_Init();
        internal static int Close() => ZKFPM_Terminate();      // double-call benign
        internal static int GetDeviceCount() => ZKFPM_GetDeviceCount();
        internal static int CloseDevice(IntPtr handle) => ZKFPM_CloseDevice(handle);

        internal static int AcquireFingerprint(
            IntPtr hDevice, IntPtr imagePtr, uint cbImage,
            IntPtr templatePtr, ref uint cbTemplate)
            => ZKFPM_AcquireFingerprint(hDevice, imagePtr, cbImage, templatePtr, ref cbTemplate);

        /// <summary>
        /// Opens device at index, reads dims/dpi/serial/product.
        /// Returns false if any critical step fails.
        ///
        /// GUARD: rawHandle &gt; 0 (negative handles observed on ZK9500).
        /// W5-leak fix: CloseDevice on ANY intermediate failure before return false.
        /// Fail-open for serial/product: keep defaults if param 1103/1102 read fails.
        /// </summary>
        internal static bool TryOpenDevice(
            int index,
            out IntPtr handle,
            out int width,
            out int height,
            out int dpi,
            out string serialNumber,
            out string productName)
        {
            handle = IntPtr.Zero;
            width = 0; height = 0; dpi = 0;
            serialNumber = "ZKTeco-unknown";
            productName = "ZKTeco Device";

            // Step 1: Open — guard against zero AND negative handles
            int rawHandle = ZKFPM_OpenDevice(index);
            if (rawHandle <= 0)
                return false;
            handle = new IntPtr(rawHandle);

            try
            {
                // Step 2: Read width/height/dpi via GetParameters codes 1/2/3
                // (verified on ZK9500 via ZkSdkProbe.cs:98-110)
                byte[] buf4 = new byte[4];

                uint sz = (uint)buf4.Length;
                if (ZKFPM_GetParameters(handle, PARAM_WIDTH, buf4, ref sz) == 0 && sz >= 4)
                    width = BitConverter.ToInt32(buf4, 0);

                sz = (uint)buf4.Length;
                if (ZKFPM_GetParameters(handle, PARAM_HEIGHT, buf4, ref sz) == 0 && sz >= 4)
                    height = BitConverter.ToInt32(buf4, 0);

                sz = (uint)buf4.Length;
                if (ZKFPM_GetParameters(handle, PARAM_DPI, buf4, ref sz) == 0 && sz >= 4)
                    dpi = BitConverter.ToInt32(buf4, 0);

                if (width <= 0 || height <= 0)
                {
                    CloseDevice(handle);
                    handle = IntPtr.Zero;
                    return false;
                }

                // Step 3: Serial (1103) + product name (1102) — fail-open
                byte[] buf64 = new byte[64];
                sz = (uint)buf64.Length;
                if (ZKFPM_GetParameters(handle, PARAM_SERIAL_NUMBER, buf64, ref sz) == 0 && (int)sz > 0)
                    serialNumber = Encoding.UTF8.GetString(buf64, 0, (int)sz)
                        .TrimEnd('\0').Replace("\0", "");

                sz = (uint)buf64.Length;
                if (ZKFPM_GetParameters(handle, PARAM_PRODUCT_NAME, buf64, ref sz) == 0 && (int)sz > 0)
                    productName = Encoding.UTF8.GetString(buf64, 0, (int)sz)
                        .TrimEnd('\0').Replace("\0", "");

                return true;
            }
            catch
            {
                // Any unexpected managed exception during param reads:
                // close handle to avoid leak, report failure
                CloseDevice(handle);
                handle = IntPtr.Zero;
                return false;
            }
        }
    }
}
