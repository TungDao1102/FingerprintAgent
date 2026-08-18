using System;
using System.Runtime.InteropServices;
using Xunit;

namespace FingerprintAgent.Tests
{
    /// <summary>
    /// Raw ZK SDK probe — calls libzkfp.dll directly (same pattern as vendor demo Form1.cs).
    /// Bypasses the ZkTecoFingerPrint NuGet wrapper so failures can be attributed to either
    /// (a) SDK/USB state, (b) the service holding the device, or (c) a wrapper quirk.
    ///
    /// Also documents that parameter 106 returns ERROR_CAPTURE on ZK9500 firmware — this
    /// is the SDK behavior that the ZkTecoFingerPrint wrapper's parameterless
    /// AcquireFingerprintAsync overload trips on. The wrapper's buffer-overload
    /// (AcquireFingerprintAsync(byte[], CancellationToken)) skips that query.
    ///
    /// Run with verbose output:
    ///   dotnet test --filter "FullyQualifiedName~ZkSdkProbe_Run" --logger "console;verbosity=detailed"
    /// </summary>
    public class ZkSdkProbe
    {
        [DllImport("libzkfp.dll", EntryPoint = "ZKFPM_Init")]
        private static extern int ZKFPM_Init();

        [DllImport("libzkfp.dll", EntryPoint = "ZKFPM_Terminate")]
        private static extern int ZKFPM_Terminate();

        [DllImport("libzkfp.dll", EntryPoint = "ZKFPM_GetDeviceCount")]
        private static extern int ZKFPM_GetDeviceCount();

        [DllImport("libzkfp.dll", EntryPoint = "ZKFPM_OpenDevice")]
        private static extern int ZKFPM_OpenDevice(int index);

        [DllImport("libzkfp.dll", EntryPoint = "ZKFPM_CloseDevice")]
        private static extern int ZKFPM_CloseDevice(IntPtr handle);

        [DllImport("libzkfp.dll", EntryPoint = "ZKFPM_GetParameters", CallingConvention = CallingConvention.Winapi)]
        private static extern int ZKFPM_GetParameters(IntPtr h, int code, byte[] param, ref int size);

        public static int Run()
        {
            Console.WriteLine("=== ZK9500 Raw SDK Probe ===");

            int init = ZKFPM_Init();
            Console.WriteLine($"[1] ZKFPM_Init() = {init} (0=Ok, -1=InitLib, -2=Init)");
            if (init != 0)
            {
                Console.WriteLine("    FAIL: SDK init failed. Verify libzkfp.dll x86 in test bin dir.");
                return 1;
            }

            int count = ZKFPM_GetDeviceCount();
            Console.WriteLine($"[2] ZKFPM_GetDeviceCount() = {count}");
            if (count <= 0)
            {
                Console.WriteLine("    FAIL: No device visible. Check: (a) ZK9500 plugged in, (b) ZKFinger driver installed (oem62.inf), (c) no other process holding it.");
                ZKFPM_Terminate();
                return 2;
            }

            IntPtr h = IntPtr.Zero;
            for (int idx = 0; idx < count; idx++)
            {
                int rawHandle = ZKFPM_OpenDevice(idx);
                Console.WriteLine($"[3.{idx}] ZKFPM_OpenDevice({idx}) = {rawHandle} (raw; IntPtr.Zero = error per vendor demo)");
                if (rawHandle > 0)
                {
                    h = new IntPtr(rawHandle);
                    Console.WriteLine($"    SUCCESS: got valid handle for device {idx}");
                    break;
                }
                else
                {
                    Console.WriteLine($"    Device {idx} unavailable (handle={rawHandle}). Likely held by another process (e.g., running FingerprintAgent service).");
                }
            }

            if (h == IntPtr.Zero)
            {
                Console.WriteLine("    FAIL: Could not open any device. All held by other processes.");
                Console.WriteLine("    Workaround: stop FingerprintAgent service (admin needed), or run probe from same process as service.");
                ZKFPM_Terminate();
                return 3;
            }

            byte[] paramBuf = new byte[4];
            int paramSize = paramBuf.Length;

            int param106 = ZKFPM_GetParameters(h, 106, paramBuf, ref paramSize);
            int w = (param106 == 0 && paramSize >= 4) ? BitConverter.ToInt32(paramBuf, 0) : 0;
            Console.WriteLine($"[4] ZKFPM_GetParameters(code=106) = {param106}, paramSize={paramSize}, value={w}");
            Console.WriteLine($"    EXPECTED on ZK9500: -8 (ERROR_CAPTURE). The ZkTecoFingerPrint wrapper's");
            Console.WriteLine($"    parameterless AcquireFingerprintAsync overload queries this parameter;");
            Console.WriteLine($"    on ZK9500 it fails immediately. Use the buffer-overload of AcquireFingerprintAsync");
            Console.WriteLine($"    instead — see ZKTecoAdapter for the correct invocation.");

            paramSize = paramBuf.Length;
            int param1 = ZKFPM_GetParameters(h, 1, paramBuf, ref paramSize);
            int width = (param1 == 0 && paramSize >= 4) ? BitConverter.ToInt32(paramBuf, 0) : 0;
            Console.WriteLine($"[5] ZKFPM_GetParameters(code=1, width) = {param1}, value={width} (EXPECTED: 0, width>0)");

            paramSize = paramBuf.Length;
            int param2 = ZKFPM_GetParameters(h, 2, paramBuf, ref paramSize);
            int height = (param2 == 0 && paramSize >= 4) ? BitConverter.ToInt32(paramBuf, 0) : 0;
            Console.WriteLine($"[6] ZKFPM_GetParameters(code=2, height) = {param2}, value={height} (EXPECTED: 0, height>0)");

            paramSize = paramBuf.Length;
            int param3 = ZKFPM_GetParameters(h, 3, paramBuf, ref paramSize);
            int dpi = (param3 == 0 && paramSize >= 4) ? BitConverter.ToInt32(paramBuf, 0) : 0;
            Console.WriteLine($"[7] ZKFPM_GetParameters(code=3, dpi) = {param3}, value={dpi} (EXPECTED: 0, dpi>0)");

            ZKFPM_CloseDevice(h);
            ZKFPM_Terminate();
            Console.WriteLine("=== Probe Complete ===");
            return 0;
        }
    }

    public class ZkSdkProbeTests
    {
        [Fact]
        public void ZkSdkProbe_Run()
        {
            int result = ZkSdkProbe.Run();
            Assert.Equal(0, result);
        }
    }
}
