using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using static ScreenDeck.Native;

namespace ScreenDeck
{
    /// <summary>
    /// A "ghost" of a window that is being moved: a snapshot of the window's last on-screen pixels shown at
    /// its old position and faded out. The real window is never touched, so this works for every program
    /// (elevated, GPU-rendered, games). The ghost is click-through, never takes focus and is excluded from
    /// screen capture, so ScreenDeck's own previews don't show it.
    /// </summary>
    internal sealed class FadeOverlay : Window
    {
        public const int MinDurationMs = 100;
        public const int MaxDurationMs = 3000;
        public const int DefaultDurationMs = 300;

        private readonly Int32Rect _bounds;   // physical pixels
        private readonly TaskCompletionSource _rendered = new();

        private FadeOverlay(BitmapSource snapshot, Int32Rect bounds)
        {
            _bounds = bounds;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;          // required for Window.Opacity to work
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            IsHitTestVisible = false;
            Title = "ScreenDeck fade";
            Content = new Image { Source = snapshot, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode((Image)Content, BitmapScalingMode.NearestNeighbor);

            SourceInitialized += (_, _) => OnSourceInitialized();
            Loaded += (_, _) => PlaceOnScreen();
            ContentRendered += (_, _) => _rendered.TrySetResult();
        }

        /// <summary>
        /// Captures the visible part of <paramref name="hWnd"/> on <paramref name="source"/> and shows its ghost.
        /// Returns null when there is nothing to show (minimized, off-screen, capture failed).
        /// </summary>
        public static FadeOverlay? TryShow(IntPtr hWnd, MonitorInfo source)
        {
            if (IsIconic(hWnd)) return null;

            var b = Intersect(WindowManager.GetVisualBounds(hWnd), source.Bounds);
            if (b.Width <= 0 || b.Height <= 0) return null;

            var snapshot = ScreenCapture.Capture(b, b.Width);
            if (snapshot == null) return null;

            var overlay = new FadeOverlay(snapshot, b);
            try { overlay.Show(); }
            catch { overlay.Close(); return null; }
            return overlay;
        }

        /// <summary>Completes once the ghost is on screen (or after a short timeout), so the real window can move without a visible gap.</summary>
        public Task WaitUntilVisibleAsync() => Task.WhenAny(_rendered.Task, Task.Delay(150));

        public void FadeOut(int durationMs)
        {
            durationMs = Math.Clamp(durationMs, MinDurationMs, MaxDurationMs);
            var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            anim.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, anim);
        }

        private void OnSourceInitialized()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);   // Windows 10 2004+; ignored if unsupported

            // Keep our exact pixel rectangle even when the ghost sits on a monitor with a different DPI.
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == WM_DPICHANGED) handled = true;
                return IntPtr.Zero;
            });
            PlaceOnScreen();
        }

        private void PlaceOnScreen()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, HWND_TOPMOST, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, SWP_NOACTIVATE);
        }

        private static Int32Rect Intersect(Int32Rect a, Int32Rect b)
        {
            int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
            int r = Math.Min(a.X + a.Width, b.X + b.Width), btm = Math.Min(a.Y + a.Height, b.Y + b.Height);
            return r > x && btm > y ? new Int32Rect(x, y, r - x, btm - y) : Int32Rect.Empty;
        }
    }
}
