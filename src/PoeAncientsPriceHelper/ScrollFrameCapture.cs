using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PoeAncientsPriceHelper;

// Samples a capture bitmap into the small horizontal-gradient ScrollFrame the tracker matches on
// (experimental scroll tracking, #68). Only the middle horizontal span is sampled so the price
// overlay drawn to the right of the panel never feeds back into the motion estimate.
internal static class ScrollFrameCapture
{
    public static ScrollFrame Sample(Bitmap bitmap)
    {
        const int width = 80;
        const int scale = 1;
        int height = bitmap.Height / scale;
        var pixels = new short[width * height];
        int bytesPerPixel = Image.GetPixelFormatSize(bitmap.PixelFormat) / 8;
        if (bytesPerPixel is not (3 or 4)) throw new ArgumentException("Expected a 24/32-bit capture bitmap.");
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            var row = new byte[Math.Abs(data.Stride)];
            var gray = new int[width + 2];
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * scale * data.Stride), row, 0, row.Length);
                for (int x = 0; x < gray.Length; x++)
                {
                    int sourceX = Math.Clamp((int)(bitmap.Width * (0.10 + 0.81 * x / (gray.Length - 1))), 0, bitmap.Width - 1);
                    int i = sourceX * bytesPerPixel;
                    gray[x] = (row[i] + 2 * row[i + 1] + row[i + 2]) / 4;
                }
                for (int x = 0; x < width; x++) pixels[y * width + x] = (short)(gray[x + 2] - gray[x]);
            }
        }
        finally { bitmap.UnlockBits(data); }
        return new ScrollFrame(width, height, scale, pixels);
    }
}
