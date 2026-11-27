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
    /// <para>
    /// With <c>useGpu</c> the frames come from DXGI Desktop Duplication, which only delivers a frame when the
    /// monitor changed. Whenever that is unavailable (rotated monitor, UAC prompt, hybrid-GPU limits...) the
    /// stream uses GDI BitBlt instead and tries the GPU again a little later.
    /// </para>
    /// </summary>
    internal sealed class MonitorStream : IDisposable
    {
        public delegate void FrameHandler(IntPtr pixels, int width, int height, int stride);

        private const int GpuRetryMs = 3000;      // after the GPU path failed, use GDI this long before retrying
        private const int GpuMaxFailures = 5;     // consecutive failures before giving up on the GPU for good

        private readonly MonitorInfo _monitor;
        private readonly int _intervalMs;
        private readonly bool _useGpu;
        private readonly FrameHandler _onFrame;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private readonly System.Threading.Thread _thread;
        private volatile bool _running = true;
        private volatile bool _paused;
        private volatile bool _usingGpu;
        private volatile string? _gpuError;

        public double Fps { get; private set; }
        public bool Paused { get => _paused; set => _paused = value; }
        /// <summary>True while frames come from Desktop Duplication (only changed frames are counted in <see cref="Fps"/>).</summary>
        public bool UsingGpu => _usingGpu;
        /// <summary>Why the GPU path is not in use, when it was asked for.</summary>
        public string? GpuError => _gpuError;

        public MonitorStream(MonitorInfo monitor, int fps, bool useGpu, FrameHandler onFrame)
        {
            _monitor = monitor;
            _intervalMs = 1000 / Math.Max(1, fps);
            _useGpu = useGpu;
            _onFrame = onFrame;
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _thread = new System.Threading.Thread(Run) { IsBackground = true, Name = "MonitorStream" };
            _thread.Start();
        }

        /// <summary>Hands a frame to the UI thread; false when the dispatcher has shut down.</summary>
        private bool Deliver(IntPtr pixels, int width, int height, int stride)
        {
            try { _dispatcher.Invoke(() => { if (_running) _onFrame(pixels, width, height, stride); }); return true; }
            catch (TaskCanceledException) { _running = false; return false; }
        }

        private void Run()
        {
            using var gdi = new GdiGrabber(_monitor.Bounds);
            DxgiDuplicator? gpu = null;
            long gpuRetryAt = 0;
            int gpuFailures = 0;
            bool gpuGaveUp = !_useGpu;
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long fpsStart = 0; int frames = 0;
                while (_running)
                {
                    long start = clock.ElapsedMilliseconds;
                    if (!_paused)
                    {
                        if (gpu == null && !gpuGaveUp && start >= gpuRetryAt)
                        {
                            gpu = DxgiDuplicator.TryCreate(_monitor.Handle, _monitor.DeviceName, out string? err);
                            _gpuError = err;
                            if (gpu == null)
                            {
                                gpuRetryAt = start + GpuRetryMs;
                                if (++gpuFailures >= GpuMaxFailures) gpuGaveUp = true;
                            }
                            else if (gdi.Grab(Deliver)) frames++;   // duplication only reports changes: show the current image now
                            _usingGpu = gpu != null;
                        }

                        if (gpu != null)
                        {
                            int timeout = Math.Max(1, _intervalMs - (int)(clock.ElapsedMilliseconds - start));
                            switch (gpu.TryGetFrame(timeout, (p, w, h, s) => Deliver(p, w, h, s)))
                            {
                                case DxgiDuplicator.FrameResult.Frame:
                                    frames++;
                                    gpuFailures = 0;
                                    break;
                                case DxgiDuplicator.FrameResult.NoChange:
                                    break;
                                default:   // lost (mode change, desktop switch) or failed: GDI for now, GPU again later
                                    gpu.Dispose();
                                    gpu = null;
                                    _usingGpu = false;
                                    gpuRetryAt = clock.ElapsedMilliseconds + 500;
                                    break;
                            }
                        }
                        else if (gdi.Grab(Deliver)) frames++;
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
                gpu?.Dispose();
            }
        }

        public void Dispose() => _running = false;

        /// <summary>GDI capture of one screen rectangle into a reused DIB.</summary>
        private sealed class GdiGrabber : IDisposable
        {
            private readonly Int32Rect _bounds;
            private readonly IntPtr _screen, _mem, _dib, _old, _bits;

            public GdiGrabber(Int32Rect bounds)
            {
                _bounds = bounds;
                _screen = Native.GetDC(IntPtr.Zero);
                _mem = Native.CreateCompatibleDC(_screen);
                var bmi = new Native.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = bounds.Width, biHeight = -bounds.Height, biPlanes = 1, biBitCount = 32, // top-down rows
                };
                _dib = Native.CreateDIBSection(_mem, ref bmi, Native.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
                _old = Native.SelectObject(_mem, _dib);
            }

            public bool Grab(Func<IntPtr, int, int, int, bool> deliver)
            {
                int w = _bounds.Width, h = _bounds.Height;
                if (_bits == IntPtr.Zero || !Native.BitBlt(_mem, 0, 0, w, h, _screen, _bounds.X, _bounds.Y, Native.SRCCOPY))
                    return false;   // secure desktop / UAC prompt / locked workstation
                Native.GdiFlush();
                return deliver(_bits, w, h, w * 4);
            }

            public void Dispose()
            {
                Native.SelectObject(_mem, _old);
                Native.DeleteObject(_dib);
                Native.DeleteDC(_mem);
                Native.ReleaseDC(IntPtr.Zero, _screen);
            }
        }
    }
}
