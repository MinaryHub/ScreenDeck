using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WpfImage = System.Windows.Controls.Image;

namespace ScreenDeck
{
    public partial class MainWindow : Window
    {
        private const string DragFormat = "ScreenDeck.Window";

        private sealed class Tile
        {
            public MonitorInfo Monitor = null!;
            public Border Root = null!;
            public WpfImage Preview = null!;
            public Canvas Overlay = null!;
            public Border Highlight = null!;
            public TextBlock Title = null!;
            public TextBox NameEditor = null!;
            public TextBlock Count = null!;
            public Border ControlBadge = null!;
        }

        private readonly Settings _settings = Settings.Load();
        private readonly uint _ownPid = (uint)Environment.ProcessId;
        private readonly ObservableCollection<WindowInfo> _items = new();
        private readonly Dictionary<IntPtr, ImageSource?> _iconCache = new();
        private readonly Dictionary<string, ImageSource?> _exeIconCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _windowTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
        private readonly DispatcherTimer _captureTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };

        private List<MonitorInfo> _monitors = new();
        private List<WindowInfo> _windows = new();
        private readonly List<Tile> _tiles = new();
        private string _monitorSignature = "";
        private string? _selectedDevice;
        private string? _controlDevice;
        private bool _isDragging;
        private bool _capturing;
        private bool _suppressComboEvent;
        private Point _dragStart;
        private WindowInfo? _dragCandidate;
        private Tile? _dropHoverTile;
        private LiveViewWindow? _liveView;

        public MainWindow()
        {
            InitializeComponent();
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;
            VersionText.Text = $"v{version.ToString(3)}";
            Title = $"ScreenDeck v{version.ToString(3)}";
            WindowList.ItemsSource = _items;
            MaximizeCheck.IsChecked = _settings.MaximizeOnMove;

            _windowTimer.Tick += (_, _) => RefreshWindows();
            _captureTimer.Tick += async (_, _) => await CapturePreviewsAsync();

            SourceInitialized += (_, _) => OnSourceInitialized();
            Loaded += async (_, _) =>
            {
                RefreshAll();
                _windowTimer.Start();
                _captureTimer.Start();
                await CapturePreviewsAsync();

                // "--select N" selects monitor N; "--live N" opens its live view on start.
                if (MonitorArg("--select") is { } sel) SelectMonitor(sel);
                if (MonitorArg("--live") is { } live) OpenLiveView(live);
            };
            Closed += (_, _) =>
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _settings.Save();
            };
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        private IntPtr OwnHandle => new WindowInteropHelper(this).Handle;

        private MonitorInfo? MonitorArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int n)
                ? _monitors.FirstOrDefault(m => m.Index == n) : null;
        }

        private void OnSourceInitialized()
        {
            _monitors = LoadMonitors();
            if (DemoData.Enabled) { DemoData.SizeForScreenshot(this); return; }
            var control =_monitors.FirstOrDefault(m => m.DeviceName == _settings.ControlMonitorDevice)
                          ?? _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();
            if (control != null) PlaceSelfOn(control);
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
            Dispatcher.BeginInvoke(new Action(() => { _monitorSignature = ""; RefreshAll(); }));

        // ───────────────────────── Refresh ─────────────────────────

        private void RefreshAll()
        {
            RefreshMonitors();
            RefreshWindows(force: true);
        }

        private List<MonitorInfo> LoadMonitors()
        {
            if (DemoData.Enabled) return DemoData.Monitors;
            var monitors = WindowManager.GetMonitors();
            foreach (var m in monitors)
                if (_settings.MonitorNames.TryGetValue(m.DeviceName, out var name)) m.CustomName = name;
            return monitors;
        }

        private void RefreshMonitors()
        {
            _monitors = LoadMonitors();
            string sig = string.Join("|", _monitors.Select(m => $"{m.DeviceName}:{m.Bounds}:{m.WorkArea}"));
            if (sig == _monitorSignature) return;
            _monitorSignature = sig;

            _controlDevice = DemoData.Enabled ? DemoData.ControlDevice
                : MonitorOf(OwnHandle)?.DeviceName ?? _monitors.FirstOrDefault()?.DeviceName;

            _suppressComboEvent = true;
            ControlCombo.ItemsSource = _monitors;
            ControlCombo.SelectedItem = _monitors.FirstOrDefault(m => m.DeviceName == _controlDevice);
            _suppressComboEvent = false;

            if (_selectedDevice == null || _monitors.All(m => m.DeviceName != _selectedDevice))
                _selectedDevice = (_monitors.FirstOrDefault(m => m.DeviceName != _controlDevice) ?? _monitors.FirstOrDefault())?.DeviceName;

            BuildTiles();
        }

        private void RefreshWindows(bool force = false)
        {
            if (_isDragging && !force) return;

            // Monitor set may change without a DisplaySettingsChanged (e.g. taskbar resize changes work area).
            var current = DemoData.Enabled ? DemoData.Monitors : WindowManager.GetMonitors();
            string sig = string.Join("|", current.Select(m => $"{m.DeviceName}:{m.Bounds}:{m.WorkArea}"));
            if (sig != _monitorSignature) RefreshMonitors();

            // Which monitor hosts ScreenDeck right now = control monitor.
            var self = MonitorOf(OwnHandle);
            if (self != null && self.DeviceName != _controlDevice)
            {
                _controlDevice = self.DeviceName;
                _suppressComboEvent = true;
                ControlCombo.SelectedItem = _monitors.FirstOrDefault(m => m.DeviceName == _controlDevice);
                _suppressComboEvent = false;
            }

            if (DemoData.Enabled) _windows = DemoData.Windows;   // icons are built in
            else
            {
                _windows = WindowManager.GetWindows(_ownPid);
                foreach (var w in _windows) w.Icon = GetIcon(w);
            }

            // Drop icon cache entries of closed windows.
            var alive = new HashSet<IntPtr>(_windows.Select(w => w.Handle));
            foreach (var dead in _iconCache.Keys.Where(k => !alive.Contains(k)).ToList()) _iconCache.Remove(dead);

            UpdateTiles();
            UpdateList();
        }

        private void UpdateList()
        {
            var mon = SelectedMonitor;
            var newItems = mon == null ? new List<WindowInfo>() : _windows.Where(w => w.MonitorHandle == mon.Handle).ToList();

            ListHeader.Text = mon == null ? "Select a monitor"
                : $"Programs on {mon.DisplayName}  ({newItems.Count})";
            EmptyText.Visibility = newItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Avoid flicker: only rebuild when something visible changed.
            bool same = newItems.Count == _items.Count && newItems.Zip(_items).All(p =>
                p.First.Handle == p.Second.Handle && p.First.Title == p.Second.Title &&
                p.First.StateText == p.Second.StateText);
            if (same)
            {
                // Keep bounds fresh for hover highlight.
                for (int i = 0; i < newItems.Count; i++) _items[i].Bounds = newItems[i].Bounds;
                return;
            }

            IntPtr selected = (WindowList.SelectedItem as WindowInfo)?.Handle ?? IntPtr.Zero;
            _items.Clear();
            foreach (var w in newItems) _items.Add(w);
            WindowList.SelectedItem = _items.FirstOrDefault(w => w.Handle == selected);
        }

        // ───────────────────────── Monitor tiles ─────────────────────────

        private MonitorInfo? SelectedMonitor => _monitors.FirstOrDefault(m => m.DeviceName == _selectedDevice);
        private MonitorInfo? ControlMonitor => _monitors.FirstOrDefault(m => m.DeviceName == _controlDevice);

        private MonitorInfo? MonitorOf(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return null;
            IntPtr h = Native.MonitorFromWindow(hWnd, Native.MONITOR_DEFAULTTONEAREST);
            return _monitors.FirstOrDefault(m => m.Handle == h);
        }

        private void BuildTiles()
        {
            LayoutCanvas.Children.Clear();
            _tiles.Clear();

            foreach (var m in _monitors)
            {
                var tile = new Tile { Monitor = m };

                tile.Preview = new WpfImage { Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(tile.Preview, BitmapScalingMode.HighQuality);

                tile.Highlight = new Border
                {
                    BorderBrush = (Brush)FindResource("Accent"), BorderThickness = new Thickness(2),
                    Background = new SolidColorBrush(Color.FromArgb(0x40, 0x4C, 0x8D, 0xFF)),
                    Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(2),
                };
                tile.Overlay = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
                tile.Overlay.Children.Add(tile.Highlight);

                tile.Title = new TextBlock
                {
                    FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Double-click to rename",
                };
                tile.Title.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { BeginRename(tile); e.Handled = true; } };
                tile.NameEditor = new TextBox
                {
                    MinWidth = 120, MaxLength = 40, Visibility = Visibility.Collapsed, Padding = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                tile.NameEditor.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter) { EndRename(tile, commit: true); e.Handled = true; }
                    else if (e.Key == Key.Escape) { EndRename(tile, commit: false); e.Handled = true; }
                };
                tile.NameEditor.LostKeyboardFocus += (_, _) => EndRename(tile, commit: true);
                tile.Count = new TextBlock
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCE, 0xD8)), Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var labelBar = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x12, 0x16)),
                    VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(10, 5, 10, 5),
                    Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { tile.Title, tile.NameEditor, tile.Count } },
                };

                tile.ControlBadge = new Border
                {
                    Background = (Brush)FindResource("Accent"), CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(8),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = "Control", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold },
                };

                var dropHint = new Border
                {
                    Name = "DropHint",
                    Background = new SolidColorBrush(Color.FromArgb(0x55, 0x38, 0xC1, 0x72)),
                    Visibility = Visibility.Collapsed,
                    Child = new TextBlock
                    {
                        Text = "Drop here", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                };

                var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x14)) };
                grid.Children.Add(tile.Preview);
                grid.Children.Add(tile.Overlay);
                grid.Children.Add(dropHint);
                grid.Children.Add(labelBar);
                grid.Children.Add(tile.ControlBadge);

                tile.Root = new Border
                {
                    Child = grid, BorderThickness = new Thickness(3), CornerRadius = new CornerRadius(6),
                    Cursor = Cursors.Hand, AllowDrop = true, Tag = tile, ClipToBounds = true,
                };
                var liveItem = new MenuItem { Header = "Live view & control", InputGestureText = "Double-click" };
                liveItem.Click += (_, _) => OpenLiveView(tile.Monitor);
                var renameItem = new MenuItem { Header = "Rename..." };
                renameItem.Click += (_, _) => BeginRename(tile);
                var resetItem = new MenuItem { Header = "Reset to default name" };
                resetItem.Click += (_, _) => RenameMonitor(tile.Monitor, null);
                tile.Root.ContextMenu = new ContextMenu { Items = { liveItem, new Separator(), renameItem, resetItem } };
                tile.Root.ContextMenuOpening += (_, _) =>
                {
                    resetItem.IsEnabled = tile.Monitor.CustomName != null;
                    liveItem.IsEnabled = tile.Monitor.DeviceName != _controlDevice;
                };
                tile.Root.MouseLeftButtonUp += (_, _) => SelectMonitor(tile.Monitor);
                tile.Root.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { OpenLiveView(tile.Monitor); e.Handled = true; } };
                tile.Root.DragEnter += Tile_DragOver;
                tile.Root.DragOver += Tile_DragOver;
                tile.Root.DragLeave += Tile_DragLeave;
                tile.Root.Drop += Tile_Drop;

                _tiles.Add(tile);
                LayoutCanvas.Children.Add(tile.Root);
            }
            LayoutTiles();
            UpdateTiles();
        }

        private void LayoutCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTiles();

        private void LayoutTiles()
        {
            if (_monitors.Count == 0) return;
            double aw = LayoutCanvas.ActualWidth, ah = LayoutCanvas.ActualHeight;
            if (aw <= 0 || ah <= 0) return;

            int minX = _monitors.Min(m => m.Bounds.X), minY = _monitors.Min(m => m.Bounds.Y);
            int maxX = _monitors.Max(m => m.Bounds.X + m.Bounds.Width), maxY = _monitors.Max(m => m.Bounds.Y + m.Bounds.Height);
            double vw = maxX - minX, vh = maxY - minY;
            double scale = Math.Min(aw / vw, ah / vh);
            double ox = (aw - vw * scale) / 2, oy = (ah - vh * scale) / 2;
            const double gap = 6;

            foreach (var t in _tiles)
            {
                var b = t.Monitor.Bounds;
                double x = ox + (b.X - minX) * scale + gap / 2, y = oy + (b.Y - minY) * scale + gap / 2;
                t.Root.Width = Math.Max(10, b.Width * scale - gap);
                t.Root.Height = Math.Max(10, b.Height * scale - gap);
                Canvas.SetLeft(t.Root, x);
                Canvas.SetTop(t.Root, y);
            }
        }

        private void UpdateTiles()
        {
            foreach (var t in _tiles)
            {
                bool selected = t.Monitor.DeviceName == _selectedDevice;
                bool isControl = t.Monitor.DeviceName == _controlDevice;
                bool hover = t == _dropHoverTile;
                t.Root.BorderBrush = hover ? (Brush)FindResource("Drop")
                    : selected ? (Brush)FindResource("Accent") : (Brush)FindResource("Border");
                t.ControlBadge.Visibility = isControl ? Visibility.Visible : Visibility.Collapsed;
                t.Title.Text = t.Monitor.DisplayName + (t.Monitor.IsPrimary ? " (Primary)" : "");
                t.Root.ToolTip = $"{t.Monitor.DisplayName} · {t.Monitor.DeviceName.TrimStart('\\', '.')} · {t.Monitor.Description}";
                int n = _windows.Count(w => w.MonitorHandle == t.Monitor.Handle);
                t.Count.Text = $"· {t.Monitor.Bounds.Width}×{t.Monitor.Bounds.Height} · {n} program{(n == 1 ? "" : "s")}";

                if (((Grid)t.Root.Child).Children.OfType<Border>().FirstOrDefault(c => c.Name == "DropHint") is { } hint)
                    hint.Visibility = hover ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void BeginRename(Tile t)
        {
            t.NameEditor.Text = t.Monitor.DisplayName;
            t.Title.Visibility = Visibility.Collapsed;
            t.NameEditor.Visibility = Visibility.Visible;
            t.NameEditor.Focus();
            t.NameEditor.SelectAll();
        }

        private void EndRename(Tile t, bool commit)
        {
            if (t.NameEditor.Visibility != Visibility.Visible) return;
            t.NameEditor.Visibility = Visibility.Collapsed;
            t.Title.Visibility = Visibility.Visible;
            if (commit) RenameMonitor(t.Monitor, t.NameEditor.Text);
        }

        /// <summary>Sets (or clears, when empty / equal to the default) a monitor's custom name and saves it.</summary>
        private void RenameMonitor(MonitorInfo m, string? name)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name == m.DefaultName) name = null;
            if (name == m.CustomName) return;

            m.CustomName = name;
            if (name == null) _settings.MonitorNames.Remove(m.DeviceName);
            else _settings.MonitorNames[m.DeviceName] = name;
            _settings.Save();

            _suppressComboEvent = true;
            ControlCombo.Items.Refresh();
            _suppressComboEvent = false;
            UpdateTiles();
            UpdateList();
            StatusText.Text = $"Monitor renamed: {m.DisplayName}";
        }

        private void OpenLiveView(MonitorInfo m)
        {
            var host = ControlMonitor ?? MonitorOf(OwnHandle);
            if (host == null) return;
            if (m.DeviceName == host.DeviceName)
            {
                StatusText.Text = "The control monitor can't show a live view of itself. Choose another monitor.";
                return;
            }

            if (_liveView != null)
            {
                if (_liveView.Monitor.DeviceName == m.DeviceName) { _liveView.WindowState = WindowState.Maximized; return; }
                _liveView.Close();
            }
            _liveView = new LiveViewWindow(m, host) { Owner = this };
            _liveView.Closed += (s, _) => { if (_liveView == s) _liveView = null; };
            _liveView.Show();
            StatusText.Text = $"Live view of {m.DisplayName} · Clicks, drags and scrolling go to that monitor";
        }

        private void SelectMonitor(MonitorInfo m)
        {
            _selectedDevice = m.DeviceName;
            UpdateTiles();
            UpdateList();
        }

        private async Task CapturePreviewsAsync()
        {
            if (_capturing || WindowState == WindowState.Minimized) return;
            _capturing = true;
            try
            {
                foreach (var t in _tiles.ToList())
                {
                    var bounds = t.Monitor.Bounds;
                    // Physical pixels of the tile (approx.) → avoid capturing more detail than we can show.
                    double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                    int maxW = (int)Math.Clamp(t.Root.ActualWidth * dpi * 1.25, 160, 1280);
                    if (DemoData.Enabled) { t.Preview.Source = DemoData.Preview(t.Monitor, maxW); continue; }
                    var bmp = await Task.Run(() => ScreenCapture.Capture(bounds, maxW));
                    if (bmp != null) t.Preview.Source = bmp;
                }
            }
            finally { _capturing = false; }
        }

        // ───────────────────────── Hover highlight ─────────────────────────

        private void Card_MouseEnter(object sender, MouseEventArgs e)
        {
            if (_isDragging || sender is not ListBoxItem { DataContext: WindowInfo w }) return;
            ShowHighlight(w);
        }

        private void Card_MouseLeave(object sender, MouseEventArgs e)
        {
            foreach (var t in _tiles) t.Highlight.Visibility = Visibility.Collapsed;
        }

        private void ShowHighlight(WindowInfo w)
        {
            foreach (var t in _tiles) t.Highlight.Visibility = Visibility.Collapsed;
            if (w.IsMinimized) return;
            var tile = _tiles.FirstOrDefault(t => t.Monitor.Handle == w.MonitorHandle);
            if (tile == null) return;
            var mb = tile.Monitor.Bounds;
            double sx = (tile.Root.ActualWidth - 6) / mb.Width, sy = (tile.Root.ActualHeight - 6) / mb.Height;
            var b = w.Bounds;
            double x = Math.Max(0, (b.X - mb.X) * sx), y = Math.Max(0, (b.Y - mb.Y) * sy);
            double r = Math.Min(mb.Width, b.X - mb.X + b.Width) * sx, btm = Math.Min(mb.Height, b.Y - mb.Y + b.Height) * sy;
            if (r - x < 2 || btm - y < 2) return;
            Canvas.SetLeft(tile.Highlight, x);
            Canvas.SetTop(tile.Highlight, y);
            tile.Highlight.Width = r - x;
            tile.Highlight.Height = btm - y;
            tile.Highlight.Visibility = Visibility.Visible;
        }

        // ───────────────────────── Drag & drop ─────────────────────────

        private void WindowList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(this);
            _dragCandidate = (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject))?.DataContext as WindowInfo;
        }

        private void WindowList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null || _isDragging) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            var w = _dragCandidate;
            _dragCandidate = null;
            WindowList.SelectedItem = w;
            Card_MouseLeave(this, e);

            GhostIcon.Source = w.Icon;
            GhostText.Text = w.Title;
            DragGhost.Visibility = Visibility.Visible;
            MoveGhost(p);
            _isDragging = true;
            try
            {
                DragDrop.DoDragDrop(WindowList, new DataObject(DragFormat, w), DragDropEffects.Move);
            }
            finally
            {
                _isDragging = false;
                _dropHoverTile = null;
                DragGhost.Visibility = Visibility.Collapsed;
                UpdateTiles();
            }
        }

        private void MoveGhost(Point p)
        {
            Canvas.SetLeft(DragGhost, p.X + 14);
            Canvas.SetTop(DragGhost, p.Y + 10);
        }

        private void Window_PreviewDragOver(object sender, DragEventArgs e) => MoveGhost(e.GetPosition(this));

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            // Reached only when not over a monitor tile.
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            if (_dropHoverTile != null) { _dropHoverTile = null; UpdateTiles(); }
        }

        private void Tile_DragOver(object sender, DragEventArgs e)
        {
            var tile = (Tile)((Border)sender).Tag;
            bool ok = e.Data.GetDataPresent(DragFormat) && e.Data.GetData(DragFormat) is WindowInfo;
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            if (ok && _dropHoverTile != tile) { _dropHoverTile = tile; UpdateTiles(); }
        }

        private void Tile_DragLeave(object sender, DragEventArgs e)
        {
            var tile = (Tile)((Border)sender).Tag;
            if (_dropHoverTile == tile) { _dropHoverTile = null; UpdateTiles(); }
        }

        private void Tile_Drop(object sender, DragEventArgs e)
        {
            var tile = (Tile)((Border)sender).Tag;
            e.Handled = true;
            if (e.Data.GetData(DragFormat) is WindowInfo w) MoveWindow(w, tile.Monitor);
        }

        // ───────────────────────── Moving ─────────────────────────

        private void MoveWindow(WindowInfo w, MonitorInfo target)
        {
            if (DemoData.Enabled)
            {
                bool max = MaximizeCheck.IsChecked == true;
                DemoData.Move(w, target, max);
                StatusText.Text = $"'{w.Title}' → {target.DisplayName}" + (max ? " (maximized)" : "");
                RefreshWindows(force: true);
                _ = CapturePreviewsAsync();
                return;
            }

            var source = _monitors.FirstOrDefault(m => m.Handle == Native.MonitorFromWindow(w.Handle, Native.MONITOR_DEFAULTTONEAREST))
                         ?? _monitors.FirstOrDefault(m => m.Handle == w.MonitorHandle);
            if (source == null) { RefreshAll(); return; }

            bool maximize = MaximizeCheck.IsChecked == true;
            if (source.Handle == target.Handle && !maximize)
            {
                StatusText.Text = $"'{w.Title}' is already on {target.DisplayName}.";
                return;
            }

            var result = WindowManager.MoveToMonitor(w.Handle, source, target, maximize);
            StatusText.Text = result switch
            {
                MoveResult.Ok => $"'{w.Title}' → {target.DisplayName}" + (maximize ? " (maximized)" : ""),
                MoveResult.AccessDenied => $"'{w.Title}' runs as administrator and can't be moved. Run ScreenDeck as administrator.",
                MoveResult.Gone => "The program has already closed.",
                _ => $"Couldn't move '{w.Title}'.",
            };

            // Maximizing activates the moved window; give focus back to the control screen
            // (unless the window was sent to the control monitor itself, where it should stay on top).
            if (result == MoveResult.Ok && target.DeviceName != _controlDevice) Activate();
            RefreshWindows(force: true);
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;   // typing a monitor name
            if (WindowList.SelectedItem is not WindowInfo w) return;
            int n = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D1 + 1,
                >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1 + 1,
                _ => 0,
            };
            if (e.Key == Key.Enter) { WindowManager.Activate(w.Handle); e.Handled = true; return; }
            var target = _monitors.FirstOrDefault(m => m.Index == n);
            if (target == null) return;
            MoveWindow(w, target);
            e.Handled = true;
        }

        private void Card_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBoxItem { DataContext: WindowInfo w }) WindowManager.Activate(w.Handle);
        }

        private void WindowList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (item?.DataContext is not WindowInfo w) { e.Handled = true; return; }
            WindowList.SelectedItem = w;

            var menu = WindowList.ContextMenu!;
            menu.Items.Clear();
            foreach (var m in _monitors)
            {
                var mi = new MenuItem
                {
                    Header = $"Move to {m.DisplayName}" + (m.DeviceName == _controlDevice ? "  · Control" : ""),
                    InputGestureText = m.Index <= 9 ? m.Index.ToString() : "",
                    IsEnabled = m.Handle != w.MonitorHandle || MaximizeCheck.IsChecked == true,
                };
                var target = m;
                mi.Click += (_, _) => MoveWindow(w, target);
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
            var activate = new MenuItem { Header = "Bring to front", InputGestureText = "Enter" };
            activate.Click += (_, _) => WindowManager.Activate(w.Handle);
            menu.Items.Add(activate);
        }

        // ───────────────────────── Control monitor & options ─────────────────────────

        private void ControlCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressComboEvent || ControlCombo.SelectedItem is not MonitorInfo m) return;
            PlaceSelfOn(m);
        }

        private void PlaceSelfOn(MonitorInfo target)
        {
            if (DemoData.Enabled) return;   // demo monitors don't exist; stay where we are

            IntPtr me = OwnHandle;
            if (me == IntPtr.Zero) return;
            var source = MonitorOf(me) ?? target;
            WindowManager.MoveToMonitor(me, source, target, maximize: true);
            WindowState = WindowState.Maximized;
            _controlDevice = target.DeviceName;
            _settings.ControlMonitorDevice = target.DeviceName;
            _settings.Save();
            if (_selectedDevice == _controlDevice)
                _selectedDevice = _monitors.FirstOrDefault(x => x.DeviceName != _controlDevice)?.DeviceName ?? _selectedDevice;
            UpdateTiles();
            UpdateList();
            Activate();
        }

        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            _settings.MaximizeOnMove = MaximizeCheck.IsChecked == true;
            _settings.Save();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _monitorSignature = "";
            RefreshAll();
            await CapturePreviewsAsync();
            StatusText.Text = $"{_monitors.Count} monitors, {_windows.Count} programs";
        }

        // ───────────────────────── Helpers ─────────────────────────

        private ImageSource? GetIcon(WindowInfo w)
        {
            if (_iconCache.TryGetValue(w.Handle, out var cached) && cached != null) return cached;
            ImageSource? src = null;
            try
            {
                IntPtr h = Native.GetWindowIcon(w.Handle);
                if (h != IntPtr.Zero)
                {
                    var bs = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    src = bs;
                }
            }
            catch { }

            if (src == null && w.ProcessPath != null)
            {
                if (!_exeIconCache.TryGetValue(w.ProcessPath, out src))
                {
                    try
                    {
                        using var ico = System.Drawing.Icon.ExtractAssociatedIcon(w.ProcessPath);
                        if (ico != null)
                        {
                            var bs = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bs.Freeze();
                            src = bs;
                        }
                    }
                    catch { }
                    _exeIconCache[w.ProcessPath] = src;
                }
            }
            _iconCache[w.Handle] = src;
            return src;
        }

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d != null && d is not T)
                d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }
    }
}
