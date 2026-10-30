using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ScreenDeck
{
    /// <summary>
    /// Shows another monitor live and enlarged on the host (control) monitor, and forwards clicks, drags and
    /// the wheel to it. The window never takes activation, so after a click the clicked program keeps the
    /// keyboard focus and typing goes straight to it.
    /// </summary>
    public partial class LiveViewWindow : Window
    {
        private const double DragThreshold = 5;   // DIPs; less than this between press and release = click

        private readonly MonitorInfo _monitor;
        private readonly MonitorInfo _host;
        private readonly MonitorStream? _stream;   // null in demo mode
        private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private WriteableBitmap? _bitmap;
        private Point? _downPoint;
        private MouseButtonKind _downButton;

        public LiveViewWindow(MonitorInfo monitor, MonitorInfo host)
        {
            InitializeComponent();
            _monitor = monitor;
            _host = host;
            Title = $"{monitor.DisplayName} Live View - ScreenDeck";
            MonitorText.Text = monitor.DisplayName;

            string size = $"{_monitor.Bounds.Width}×{_monitor.Bounds.Height}";
            if (DemoData.Enabled)
            {
                Screen.Source = DemoData.Preview(monitor, monitor.Bounds.Width);
                FpsText.Text = $"30 fps · {size}";
            }
            else
            {
                _stream = new MonitorStream(monitor.Bounds, fps: 30, OnFrame);
                _statusTimer.Tick += (_, _) => FpsText.Text = $"{_stream.Fps:0} fps · {size}";
                _statusTimer.Start();
            }

            SourceInitialized += (_, _) => OnSourceInitialized();
            StateChanged += (_, _) => { if (_stream != null) _stream.Paused = WindowState == WindowState.Minimized; };
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Closed += (_, _) =>
            {
                _stream?.Dispose();
                _statusTimer.Stop();
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            };
        }

        public MonitorInfo Monitor => _monitor;

        private void OnSourceInitialized()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            // No activation on click: the program clicked through this view keeps the keyboard.
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE));

            // Open maximized on the host monitor (in demo: on whatever real monitor ScreenDeck is on).
            if (DemoData.Enabled)
            {
                DemoData.SizeForScreenshot(this);
                return;
            }
            var wa = _host.WorkArea;
            Native.SetWindowPos(hwnd, IntPtr.Zero, wa.X, wa.Y, wa.Width, wa.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            WindowState = WindowState.Maximized;
        }

        // The monitor layout changed; our capture rectangle is stale.
        private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(Close));

        private void OnFrame(IntPtr pixels, int width, int height, int stride)
        {
            if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                Screen.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride * height, stride);
        }

        // ───────────────────────── Input forwarding ─────────────────────────

        private bool Controlling => ControlCheck.IsChecked == true;

        /// <summary>Maps a point on the image (DIPs) to physical screen coordinates on the monitor.</summary>
        private bool TryMap(Point p, out int x, out int y)
        {
            x = y = 0;
            double w = Screen.ActualWidth, h = Screen.ActualHeight;
            if (w <= 0 || h <= 0 || p.X < 0 || p.Y < 0 || p.X >= w || p.Y >= h) return false;
            var b = _monitor.Bounds;
            x = b.X + (int)(p.X / w * b.Width);
            y = b.Y + (int)(p.Y / h * b.Height);
            return true;
        }

        private void Screen_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!Controlling || _downPoint != null) return;
            MouseButtonKind? kind = e.ChangedButton switch
            {
                MouseButton.Left => MouseButtonKind.Left,
                MouseButton.Right => MouseButtonKind.Right,
                MouseButton.Middle => MouseButtonKind.Middle,
                _ => null,
            };
            if (kind == null) return;
            _downPoint = e.GetPosition(Screen);
            _downButton = kind.Value;
            Screen.CaptureMouse();
            e.Handled = true;
        }

        private void Screen_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_downPoint is not Point start) return;
            if (!Matches(e.ChangedButton, _downButton)) return;
            _downPoint = null;
            Screen.ReleaseMouseCapture();
            e.Handled = true;

            Point end = e.GetPosition(Screen);
            if (!TryMap(start, out int x1, out int y1)) return;

            if (DemoData.Enabled) { ShowRipple(start); return; }   // nothing real to control
            if ((end - start).Length < DragThreshold)
            {
                InputInjector.Click(x1, y1, _downButton);
                ShowRipple(start);
            }
            else
            {
                // Clamp the drop point to the image so a drag released outside still ends on the monitor.
                end = new Point(Math.Clamp(end.X, 0, Screen.ActualWidth - 1), Math.Clamp(end.Y, 0, Screen.ActualHeight - 1));
                TryMap(end, out int x2, out int y2);
                InputInjector.Drag(x1, y1, x2, y2, _downButton);
                ShowRipple(end);
            }
        }

        private void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!Controlling || DemoData.Enabled || !TryMap(e.GetPosition(Screen), out int x, out int y)) return;
            InputInjector.Wheel(x, y, e.Delta);
            e.Handled = true;
        }

        private static bool Matches(MouseButton b, MouseButtonKind k) =>
            (b, k) is (MouseButton.Left, MouseButtonKind.Left) or (MouseButton.Right, MouseButtonKind.Right)
                or (MouseButton.Middle, MouseButtonKind.Middle);

        /// <summary>Brief ring where the click landed — feedback, since the view updates a frame later.</summary>
        private void ShowRipple(Point onImage)
        {
            Point p = Screen.TranslatePoint(onImage, Overlay);
            var ring = new Ellipse
            {
                Width = 28, Height = 28, Stroke = (Brush)FindResource("Accent"), StrokeThickness = 3,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.4, 0.4),
            };
            Canvas.SetLeft(ring, p.X - 14);
            Canvas.SetTop(ring, p.Y - 14);
            Overlay.Children.Add(ring);

            var duration = TimeSpan.FromMilliseconds(400);
            var grow = new DoubleAnimation(0.4, 1.2, duration);
            var fade = new DoubleAnimation(1, 0, duration);
            fade.Completed += (_, _) => Overlay.Children.Remove(ring);
            ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(OpacityProperty, fade);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
