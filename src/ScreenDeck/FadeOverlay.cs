using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using static ScreenDeck.Native;

namespace ScreenDeck
{
    /// <summary>
    /// A "ghost" of a window that is being moved: a snapshot of the window's last on-screen pixels shown at
    /// its old position and faded out. The real window is never touched, so this works for every program
    /// (elevated, GPU-rendered, games). The ghost is click-through, never takes focus and is excluded from
    /// screen capture, so ScreenDeck's own previews don't show it.
    /// <para>
    /// The ghost is a plain Win32 layered window. Its pixels are handed to the system once
    /// (UpdateLayeredWindow); the fade only changes the window's constant alpha, which DWM applies on the GPU.
    /// (A WPF window with AllowsTransparency re-renders and re-uploads the whole bitmap in software on every
    /// animation frame, which is slow for a full-screen 4K window.)
    /// </para>
    /// </summary>
    internal sealed class FadeOverlay
    {
        public const int MinDurationMs = 100;
        public const int MaxDurationMs = 3000;
        public const int DefaultDurationMs = 300;

        private readonly Int32Rect _bounds;   // physical pixels
        private readonly System.Windows.Forms.NativeWindow _window = new();
        private DispatcherTimer? _fadeTimer;
        private bool _closed;

        private FadeOverlay(Int32Rect bounds) => _bounds = bounds;

        /// <summary>
        /// Captures the visible part of <paramref name="hWnd"/> on <paramref name="source"/> and shows its ghost.
        /// Returns null when there is nothing to show (minimized, off-screen, capture failed).
        /// </summary>
        public static FadeOverlay? TryShow(IntPtr hWnd, MonitorInfo source)
        {
            if (IsIconic(hWnd)) return null;

            var b = Intersect(WindowManager.GetVisualBounds(hWnd), source.Bounds);
            if (b.Width <= 0 || b.Height <= 0) return null;

            var overlay = new FadeOverlay(b);
            try
            {
                if (overlay.Create()) return overlay;
            }
            catch { }
            overlay.Close();
            return null;
        }

        private bool Create()
        {
            _window.CreateHandle(new System.Windows.Forms.CreateParams
            {
                Caption = "ScreenDeck fade",
                X = _bounds.X, Y = _bounds.Y, Width = _bounds.Width, Height = _bounds.Height,
                Style = WS_POPUP,
                ExStyle = (int)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST),
            });
            IntPtr hwnd = _window.Handle;
            if (hwnd == IntPtr.Zero) return false;
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);   // Windows 10 2004+; ignored if unsupported

            if (!SetSnapshot(hwnd)) return false;
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            return true;
        }

        /// <summary>Copies the screen pixels under the ghost into the layered window, fully opaque.</summary>
        private bool SetSnapshot(IntPtr hwnd)
        {
            int w = _bounds.Width, h = _bounds.Height;
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return false;
            IntPtr mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                mem = CreateCompatibleDC(screen);
                if (mem == IntPtr.Zero) return false;
                var bmi = new BITMAPINFOHEADER
                {
                    biSize = System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
                };
                dib = CreateDIBSection(mem, ref bmi, DIB_RGB_COLORS, out _, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero) return false;
                old = SelectObject(mem, dib);

                // Secure desktop / UAC prompt / locked workstation: nothing to show.
                if (!BitBlt(mem, 0, 0, w, h, screen, _bounds.X, _bounds.Y, SRCCOPY)) return false;
                GdiFlush();

                // AlphaFormat 0: per-pixel alpha is ignored (BitBlt leaves it 0), only the constant alpha applies.
                var dst = new POINT { X = _bounds.X, Y = _bounds.Y };
                var size = new SIZE { cx = w, cy = h };
                var src = new POINT();
                var blend = Blend(255);
                return UpdateLayeredWindow(hwnd, screen, ref dst, ref size, mem, ref src, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                // The system keeps its own copy of the pixels; ours can go right away.
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private static BLENDFUNCTION Blend(byte alpha) =>
            new() { BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 0 };

        /// <summary>
        /// Completes once the ghost has been composed on screen (or after a short timeout), so the real window
        /// can move without a visible gap.
        /// </summary>
        public Task WaitUntilVisibleAsync() =>
            Task.WhenAny(Task.Run(() => { DwmFlush(); DwmFlush(); }), Task.Delay(150));

        public void FadeOut(int durationMs)
        {
            if (_closed) return;
            durationMs = Math.Clamp(durationMs, MinDurationMs, MaxDurationMs);
            var clock = Stopwatch.StartNew();
            _fadeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
            _fadeTimer.Tick += (_, _) =>
            {
                double t = clock.Elapsed.TotalMilliseconds / durationMs;
                if (t >= 1 || _window.Handle == IntPtr.Zero) { Close(); return; }
                // Quadratic ease-in from opaque to transparent, as before.
                var blend = Blend((byte)Math.Round(255 * (1 - t * t)));
                UpdateLayeredWindow(_window.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    0, ref blend, ULW_ALPHA);
            };
            _fadeTimer.Start();
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            _fadeTimer?.Stop();
            if (_window.Handle != IntPtr.Zero) _window.DestroyHandle();
        }

        private static Int32Rect Intersect(Int32Rect a, Int32Rect b)
        {
            int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
            int r = Math.Min(a.X + a.Width, b.X + b.Width), btm = Math.Min(a.Y + a.Height, b.Y + b.Height);
            return r > x && btm > y ? new Int32Rect(x, y, r - x, btm - y) : Int32Rect.Empty;
        }
    }
}
