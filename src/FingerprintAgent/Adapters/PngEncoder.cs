using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace FingerprintAgent.Adapters
{
    /// <summary>
    /// Shared 8bpp grayscale raw-pixel → PNG encoder. Single source of truth — keeps
    /// BaseScannerAdapter and ZKTecoAdapter (which does not extend BaseScannerAdapter)
    /// producing identical PNG output. Wraps LockBits/UnlockBits in try/finally.
    /// </summary>
    internal static class PngEncoder
    {
        public static byte[] ToPngGrayscale(byte[] rawPixels, int width, int height)
        {
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format8bppIndexed))
            {
                ColorPalette palette = bitmap.Palette;
                for (int i = 0; i < 256; i++)
                    palette.Entries[i] = Color.FromArgb(i, i, i);
                bitmap.Palette = palette;

                var bitmapData = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format8bppIndexed);
                try
                {
                    int stride = bitmapData.Stride;
                    for (int row = 0; row < height; row++)
                    {
                        Marshal.Copy(rawPixels, row * width, bitmapData.Scan0 + row * stride, width);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }

                using (var ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }
    }
}
