using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenDeck
{
    internal static class ScreenCapture
    {
        /// <summary>
        /// Captures a screen rectangle and returns a frozen bitmap at most <paramref name="maxWidth"/> pixels wide
        /// (safe to pass across threads). The screen is scaled straight into a small DIB with one GDI StretchBlt,
        /// so a 4K monitor is never copied at full size just to be shrunk to a thumbnail.
        /// </summary>
        public static BitmapSource? Capture(Int32Rect bounds, int maxWidth)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            double scale = Math.Min(1.0, maxWidth / (double)bounds.Width);
            int tw = Math.Max(1, (int)(bounds.Width * scale));
            int th = Math.Max(1, (int)(bounds.Height * scale));
            int stride = tw * 4;

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            IntPtr mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                mem = Native.CreateCompatibleDC(screen);
                if (mem == IntPtr.Zero) return null;
                var bmi = new Native.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = tw, biHeight = -th, biPlanes = 1, biBitCount = 32, // negative height = top-down rows
                };
                dib = Native.CreateDIBSection(mem, ref bmi, Native.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) return null;
                old = Native.SelectObject(mem, dib);

                bool ok;
                if (tw == bounds.Width && th == bounds.Height)   // full resolution: plain copy
                    ok = Native.BitBlt(mem, 0, 0, tw, th, screen, bounds.X, bounds.Y, Native.SRCCOPY);
                else
                {
                    Native.SetStretchBltMode(mem, Native.HALFTONE);   // averages pixels: smooth thumbnails
                    Native.SetBrushOrgEx(mem, 0, 0, IntPtr.Zero);     // required after selecting HALFTONE
                    ok = Native.StretchBlt(mem, 0, 0, tw, th, screen, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                        Native.SRCCOPY);
                }
                if (!ok) return null;   // secure desktop / UAC prompt / locked workstation
                Native.GdiFlush();

                var src = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgr32, null, bits, stride * th, stride);
                src.Freeze();
                return src;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (old != IntPtr.Zero) Native.SelectObject(mem, old);
                if (dib != IntPtr.Zero) Native.DeleteObject(dib);
                if (mem != IntPtr.Zero) Native.DeleteDC(mem);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }

    /// <summary>
    /// Continuously captures one monitor at native resolution on a background thread and hands each frame
    /// to the UI thread as raw BGRA pixels. The callback runs synchronously, so a slow UI simply lowers the rate.
    /// </summary>
    internal sealed class MonitorStream : IDisposable
    {
        public delegate void FrameHandler(IntPtr pixels, int width, int height, int stride);

        private readonly Int32Rect _bounds;
        private readonly int _intervalMs;
        private readonly FrameHandler _onFrame;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private readonly System.Threading.Thread _thread;
        private volatile bool _running = true;
        private volatile bool _paused;

        public double Fps { get; private set; }
        public bool Paused { get => _paused; set => _paused = value; }

        public MonitorStream(Int32Rect bounds, int fps, FrameHandler onFrame)
        {
            _bounds = bounds;
            _intervalMs = 1000 / Math.Max(1, fps);
            _onFrame = onFrame;
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _thread = new System.Threading.Thread(Run) { IsBackground = true, Name = "MonitorStream" };
            _thread.Start();
        }

        private void Run()
        {
            int w = _bounds.Width, h = _bounds.Height, stride = w * 4;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            var bmi = new Native.BITMAPINFOHEADER
            {
                biSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, // negative height = top-down rows
            };
            IntPtr dib = Native.CreateDIBSection(mem, ref bmi, Native.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = Native.SelectObject(mem, dib);
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long fpsStart = 0; int frames = 0;
                while (_running)
                {
                    long start = clock.ElapsedMilliseconds;
                    if (!_paused && Native.BitBlt(mem, 0, 0, w, h, screen, _bounds.X, _bounds.Y, Native.SRCCOPY))
                    {
                        Native.GdiFlush();
                        try { _dispatcher.Invoke(() => { if (_running) _onFrame(bits, w, h, stride); }); }
                        catch (TaskCanceledException) { break; }   // dispatcher shut down
                        frames++;
                    }
                    if (clock.ElapsedMilliseconds - fpsStart >= 1000)
                    {
                        Fps = frames * 1000.0 / (clock.ElapsedMilliseconds - fpsStart);
                        fpsStart = clock.ElapsedMilliseconds; frames = 0;
                    }
                    int wait = _intervalMs - (int)(clock.ElapsedMilliseconds - start);
                    System.Threading.Thread.Sleep(Math.Max(1, wait));
                }
            }
            finally
            {
                Native.SelectObject(mem, old);
                Native.DeleteObject(dib);
                Native.DeleteDC(mem);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        public void Dispose() => _running = false;
    }
}
