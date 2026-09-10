using System;
using System.Runtime.InteropServices;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// Raw P/Invoke layer for dpfpdd.dll (DigitalPersona U.are.U SDK capture API).
    /// Replaces the DPUruNet NuGet package (1.0.0.1) and the conditional DPFPDevNET
    /// managed-wrapper path: the NuGet assembly exports only the DPUruNet namespace
    /// which no compiled code referenced, and the managed path forced DPFPDevNET.dll
    /// onto the user's machine.
    ///
    /// Runtime model = ZkNativeHost (libzkfp.dll) precedent: the adapter code compiles
    /// unconditionally, the MSI ships no vendor DLLs, and dpfpdd.dll is resolved at
    /// request time from the U.are.U driver installed on the user's machine
    /// (standard loader search: app dir → SysWOW64/System32 → PATH).
    /// Missing driver → DllNotFoundException, mapped to DRIVER_NOT_INSTALLED by the adapter.
    ///
    /// API reference: dpfpdd.h v2.0.0 — DPAPICALL is __stdcall on Win32, which is the
    /// default Winapi calling convention. NEVER set Cdecl.
    /// </summary>
    internal static class DpNativeHost
    {
        // DPERROR(err) = err | (0x05BA << 16) — see dpfpdd.h
        private const int DpFacility = 0x05BA << 16;

        internal const int DpfpddSuccess = 0;
        internal const int DpfpddENotImplemented = unchecked((int)(0x0a | DpFacility));
        internal const int DpfpddEFailure = unchecked((int)(0x0b | DpFacility));
        internal const int DpfpddENoData = unchecked((int)(0x0c | DpFacility));
        internal const int DpfpddEMoreData = unchecked((int)(0x0d | DpFacility));
        internal const int DpfpddEInvalidParameter = unchecked((int)(0x14 | DpFacility));
        internal const int DpfpddEInvalidDevice = unchecked((int)(0x15 | DpFacility));
        internal const int DpfpddEDeviceBusy = unchecked((int)(0x1e | DpFacility));
        internal const int DpfpddEDeviceFailure = unchecked((int)(0x1f | DpFacility));

        /// <summary>"Raw" format: 8bpp grayscale pixel buffer without a header (width × height bytes).</summary>
        internal const uint ImgFmtPixelBuffer = 0;

        /// <summary>Recommended (fastest) acquisition processing.</summary>
        internal const uint ImgProcDefault = 0;

        // DPFPDD_QUALITY bitfield — subset relevant to the capture-only flow.
        internal const uint QualityGood = 0;
        internal const uint QualityTimedOut = 1;
        internal const uint QualityCanceled = 1 << 1;
        internal const uint QualityNoFinger = 1 << 2;

        private const int MaxStrLength = 128;
        private const int MaxDeviceNameLength = 1024;
        private const uint DefaultCaptureDpi = 500;

        /// <summary>
        /// DPFPDD_DEV_INFO (fixed size — no variable-length member; only DEV_CAPS has one).
        /// Flat layout: size + name[1024] + descr{3 × char[128]} + id(2×ushort) +
        /// ver(6×int + ushort) + modality + technology. Offsets match the C struct with
        /// default packing (all fields 4-byte aligned; total 1452 bytes).
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        internal struct DpDeviceInfo
        {
            public uint Size;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxDeviceNameLength)]
            public string Name;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStrLength)]
            public string VendorName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStrLength)]
            public string ProductName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxStrLength)]
            public string SerialNumber;

            public ushort VendorId;
            public ushort ProductId;
            public int HwVersionMajor;
            public int HwVersionMinor;
            public int HwVersionMaintenance;
            public int FwVersionMajor;
            public int FwVersionMinor;
            public int FwVersionMaintenance;
            public ushort BcdRevision;
            public uint Modality;
            public uint Technology;
        }

        /// <summary>DPFPDD_CAPTURE_PARAM — all four members uint, 16 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct DpCaptureParam
        {
            public uint Size;
            public uint ImageFmt;
            public uint ImageProc;
            public uint ImageRes;
        }

        /// <summary>DPFPDD_IMAGE_INFO — 20 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct DpImageInfo
        {
            public uint Size;
            public uint Width;
            public uint Height;
            public uint Res;
            public uint Bpp;
        }

        /// <summary>DPFPDD_CAPTURE_RESULT — 36 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct DpCaptureResult
        {
            public uint Size;
            public int Success;
            public uint Quality;
            public uint Score;
            public DpImageInfo Info;
        }

        [DllImport("dpfpdd.dll")]
        private static extern int dpfpdd_init();

        [DllImport("dpfpdd.dll")]
        private static extern int dpfpdd_query_devices(ref uint devCnt, [In, Out] DpDeviceInfo[] devInfos);

        [DllImport("dpfpdd.dll", CharSet = CharSet.Ansi)]
        private static extern int dpfpdd_open(string devName, out IntPtr pdev);

        [DllImport("dpfpdd.dll")]
        private static extern int dpfpdd_close(IntPtr dev);

        /// <summary>
        /// Blocking capture. With a preallocated buffer large enough for the sensor the
        /// E_MORE_DATA size-probe pass is unnecessary — imageSize [in] is the buffer
        /// capacity, [out] the actual image size. Caller runs it inside Task.Run and
        /// frees imageData (AllocHGlobal) in a finally.
        /// </summary>
        [DllImport("dpfpdd.dll")]
        private static extern int dpfpdd_capture(
            IntPtr dev,
            ref DpCaptureParam captureParam,
            uint timeoutCnt,
            ref DpCaptureResult captureResult,
            ref uint imageSize,
            IntPtr imageData);

        /// <summary>Cancels a pending dpfpdd_capture on the same reader (thread-safe by design).</summary>
        [DllImport("dpfpdd.dll")]
        private static extern int dpfpdd_cancel(IntPtr dev);

        internal static int Init() => dpfpdd_init();

        // NOTE: dpfpdd_exit() is deliberately NOT exposed. Like ZkNativeHost.Close() it is
        // process-wide teardown; per-instance Dispose only closes the reader handle and the
        // OS reclaims the rest at process exit.

        /// <summary>
        /// Queries connected readers and opens the first one exclusively.
        /// Returns false when no reader is present or open fails (DEVICE_BUSY etc.) —
        /// the adapter maps its own vendor error code for those cases.
        /// Fail-open for serial/product strings: keep fallbacks when descriptors are empty.
        /// </summary>
        internal static bool TryOpenFirstDevice(out IntPtr handle, out string deviceId, out string model)
        {
            handle = IntPtr.Zero;
            deviceId = "no-device";
            model = "DigitalPersona Scanner";

            // Pass 1: required entry count (E_MORE_DATA carries it in devCnt; zero
            // readers surface as SUCCESS + count 0, or E_MORE_DATA + count 0).
            uint count = 0;
            int rc = dpfpdd_query_devices(ref count, null);
            if (count == 0 || (rc != DpfpddEMoreData && rc != DpfpddSuccess))
                return false;

            var infos = new DpDeviceInfo[count];
            uint entrySize = (uint)Marshal.SizeOf(typeof(DpDeviceInfo));
            for (int i = 0; i < infos.Length; i++)
                infos[i].Size = entrySize;

            uint requested = count;
            rc = dpfpdd_query_devices(ref requested, infos);
            if (rc != DpfpddSuccess || requested == 0)
                return false;

            if (dpfpdd_open(infos[0].Name, out handle) != DpfpddSuccess)
            {
                handle = IntPtr.Zero;
                return false;
            }

            deviceId = string.IsNullOrEmpty(infos[0].SerialNumber)
                ? "unknown-serial"
                : infos[0].SerialNumber;
            model = string.IsNullOrEmpty(infos[0].ProductName)
                ? "DigitalPersona Scanner"
                : infos[0].ProductName;
            return true;
        }

        internal static int Capture(IntPtr dev, uint timeoutMs, ref DpCaptureResult result, IntPtr imageData, uint imageDataBytes)
        {
            DpCaptureParam param = NewCaptureParam();
            uint imageSize = imageDataBytes;
            return dpfpdd_capture(dev, ref param, timeoutMs, ref result, ref imageSize, imageData);
        }

        internal static int Cancel(IntPtr dev) => dpfpdd_cancel(dev);

        internal static int Close(IntPtr dev) => dpfpdd_close(dev);

        internal static DpCaptureResult NewCaptureResult()
        {
            return new DpCaptureResult
            {
                Size = (uint)Marshal.SizeOf(typeof(DpCaptureResult)),
                Success = 0,
                Quality = QualityGood,
                Score = 0,
                Info = new DpImageInfo { Size = (uint)Marshal.SizeOf(typeof(DpImageInfo)) }
            };
        }

        private static DpCaptureParam NewCaptureParam()
        {
            return new DpCaptureParam
            {
                Size = (uint)Marshal.SizeOf(typeof(DpCaptureParam)),
                ImageFmt = ImgFmtPixelBuffer,
                ImageProc = ImgProcDefault,
                ImageRes = DefaultCaptureDpi
            };
        }
    }
}
