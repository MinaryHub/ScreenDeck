using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ScreenDeck
{
    internal static class ScreenCapture
    {
        /// <summary>Captures a monitor and returns a frozen, downscaled bitmap (safe to pass across threads).</summary>
        public static BitmapSource? Capture(Int32Rect bounds, int maxWidth)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            try
            {
                double scale = Math.Min(1.0, maxWidth / (double)bounds.Width);
                int tw = Math.Max(1, (int)(bounds.Width * scale));
                int th = Math.Max(1, (int)(bounds.Height * scale));

                using var full = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(full))
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height),
                        CopyPixelOperation.SourceCopy);

                using var small = new Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(full, 0, 0, tw, th);
                }

                IntPtr hBmp = small.GetHbitmap();
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(hBmp, IntPtr.Zero, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { Native.DeleteObject(hBmp); }
            }
            catch
            {
                return null; // secure desktop / UAC prompt / locked workstation
            }
        }
    }
}
