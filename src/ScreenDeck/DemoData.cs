using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenDeck
{
    /// <summary>
    /// "--demo": fictional monitors, programs and rendered desktops instead of the real ones, so the app can be
    /// shown or screenshotted without exposing anything on this PC. Nothing is captured, moved, injected or saved.
    /// </summary>
    internal static class DemoData
    {
        public static bool Enabled { get; } = Environment.GetCommandLineArgs().Contains("--demo");

        private enum Kind { Document, Spreadsheet, Slides, Mail, Terminal, Chat, Media, Canvas, Browser }

        private sealed record App(Kind Kind, Color Color, string Letter);

        private const int Taskbar = 48;

        private static readonly List<MonitorInfo> _monitors = new()
        {
            MakeMonitor(1, 0, 180, 1920, 1080, primary: false, name: null),
            MakeMonitor(2, 1920, 0, 2560, 1440, primary: true, name: null),
            MakeMonitor(3, 4480, 180, 1920, 1080, primary: false, name: "Presentation"),
        };

        private static readonly Dictionary<IntPtr, App> _apps = new();
        private static readonly List<WindowInfo> _windows = new();
        private static readonly Dictionary<(string, int), (int version, BitmapSource bmp)> _previewCache = new();
        private static int _version;

        public static List<MonitorInfo> Monitors => _monitors;
        public static List<WindowInfo> Windows => _windows;
        public static string ControlDevice => _monitors[1].DeviceName;

        static DemoData()
        {
            var m1 = _monitors[0]; var m2 = _monitors[1]; var m3 = _monitors[2];
            // Z-order: topmost first within each monitor.
            AddWindow("Q3 Product Roadmap.docx - Writer", "writer", new App(Kind.Document, C("#2B6CE6"), "W"), m1, 140, 80, 1060, 740);
            AddWindow("Terminal - npm run build", "terminal", new App(Kind.Terminal, C("#3A3F4B"), ">"), m1, 70, 560, 760, 410);
            AddWindow("Inbox (3) - Mail", "mail", new App(Kind.Mail, C("#0F8FD6"), "M"), m1, 780, 230, 1060, 700);
            AddWindow("Budget 2026.xlsx - Sheets", "sheets", new App(Kind.Spreadsheet, C("#1E9E5A"), "S"), m1, maximized: true);
            AddWindow("Lo-fi Focus - Music", "music", new App(Kind.Media, C("#E5484D"), "♪"), m1, minimized: true);

            AddWindow("#design-team - Chat", "chat", new App(Kind.Chat, C("#7C5CFF"), "C"), m2, minimized: true);
            AddWindow("Calendar - This week", "calendar", new App(Kind.Browser, C("#F08C00"), "31"), m2, minimized: true);

            AddWindow("Brand Assets - Canvas", "canvas", new App(Kind.Canvas, C("#E0457B"), "A"), m3, 260, 110, 1180, 760);
            AddWindow("Sprint Review.pptx - Slides", "slides", new App(Kind.Slides, C("#D9622B"), "P"), m3, maximized: true);
            AddWindow("Docs Portal - Browser", "browser", new App(Kind.Browser, C("#2F9E9E"), "B"), m3, minimized: true);
        }

        private static MonitorInfo MakeMonitor(int index, int x, int y, int w, int h, bool primary, string? name) => new()
        {
            Handle = new IntPtr(0xDE0000 + index),
            Index = index,
            DeviceName = $@"\\.\DEMO{index}",
            Bounds = new Int32Rect(x, y, w, h),
            WorkArea = new Int32Rect(x, y, w, h - Taskbar),
            IsPrimary = primary,
            CustomName = name,
        };

        private static void AddWindow(string title, string process, App app, MonitorInfo m,
            int x = 0, int y = 0, int w = 0, int h = 0, bool maximized = false, bool minimized = false)
        {
            var handle = new IntPtr(0xDE1000 + _windows.Count);
            _apps[handle] = app;
            _windows.Add(new WindowInfo
            {
                Handle = handle,
                Title = title,
                ProcessName = process,
                MonitorHandle = m.Handle,
                Bounds = maximized || minimized ? m.WorkArea : new Int32Rect(m.Bounds.X + x, m.Bounds.Y + y, w, h),
                IsMaximized = maximized,
                IsMinimized = minimized,
                Icon = MakeIcon(app),
            });
        }

        /// <summary>Simulated move: re-parent the window to the target monitor and bring it to the top.</summary>
        public static void Move(WindowInfo w, MonitorInfo target, bool maximize)
        {
            var source = _monitors.First(m => m.Handle == w.MonitorHandle);
            if (maximize) w.Bounds = target.WorkArea;
            else
            {
                var b = w.Bounds;
                int nw = Math.Min(b.Width, target.WorkArea.Width), nh = Math.Min(b.Height, target.WorkArea.Height);
                int nx = target.Bounds.X + Math.Clamp(b.X - source.Bounds.X, 0, target.WorkArea.Width - nw);
                int ny = target.Bounds.Y + Math.Clamp(b.Y - source.Bounds.Y, 0, target.WorkArea.Height - nh);
                w.Bounds = new Int32Rect(nx, ny, nw, nh);
            }
            w.MonitorHandle = target.Handle;
            w.IsMaximized = maximize;
            w.IsMinimized = false;
            _windows.Remove(w);
            _windows.Insert(0, w);
            _version++;
        }

        /// <summary>Store-screenshot size: a 1920×1080 (physical px) window, centered on the current monitor.</summary>
        public static void SizeForScreenshot(Window w)
        {
            var dpi = VisualTreeHelper.GetDpi(w);
            const int border = 7;   // Windows 10/11 invisible resize border (left, right, bottom) at 100%
            w.WindowState = WindowState.Normal;
            w.Width = (1920 + 2 * border * dpi.DpiScaleX) / dpi.DpiScaleX;
            w.Height = (1080 + border * dpi.DpiScaleY) / dpi.DpiScaleY;
            var area = SystemParameters.WorkArea;
            w.Left = area.Left + Math.Max(0, (area.Width - w.Width) / 2);
            w.Top = area.Top + Math.Max(0, (area.Height - w.Height) / 2);
        }

        // ───────────────────────── Rendering ─────────────────────────

        /// <summary>A rendered "screenshot" of the monitor's fictional desktop, <paramref name="width"/> pixels wide.</summary>
        public static BitmapSource Preview(MonitorInfo m, int width)
        {
            width = Math.Clamp(width, 64, m.Bounds.Width);
            if (_previewCache.TryGetValue((m.DeviceName, width), out var c) && c.version == _version) return c.bmp;

            double s = width / (double)m.Bounds.Width;
            int height = Math.Max(1, (int)Math.Round(m.Bounds.Height * s));
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(s, s));
                DrawDesktop(dc, m);
                dc.Pop();
            }
            var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            _previewCache[(m.DeviceName, width)] = (_version, bmp);
            return bmp;
        }

        private static void DrawDesktop(DrawingContext dc, MonitorInfo m)
        {
            double W = m.Bounds.Width, H = m.Bounds.Height;
            var (a, b) = m.Index switch
            {
                1 => (C("#1B3A6B"), C("#4E2A6E")),
                2 => (C("#14323A"), C("#1F4E5F")),
                _ => (C("#5A2340"), C("#2A2F6B")),
            };
            dc.DrawRectangle(new LinearGradientBrush(a, b, 35), null, new Rect(0, 0, W, H));
            Glow(dc, W * 0.78, H * 0.22, H * 0.55, Color.FromArgb(70, 255, 190, 120));
            Glow(dc, W * 0.18, H * 0.85, H * 0.5, Color.FromArgb(60, 120, 200, 255));

            if (m.DeviceName == ControlDevice) { DrawScreenDeck(dc, W, H - Taskbar); }
            else
            {
                var onMonitor = _windows.Where(w => w.MonitorHandle == m.Handle && !w.IsMinimized).Reverse();
                foreach (var w in onMonitor)
                {
                    var r = new Rect(w.Bounds.X - m.Bounds.X, w.Bounds.Y - m.Bounds.Y, w.Bounds.Width, w.Bounds.Height);
                    DrawWindow(dc, r, w.Title, _apps[w.Handle], w.IsMaximized);
                }
            }
            DrawTaskbar(dc, m, W, H);
        }

        private static void DrawTaskbar(DrawingContext dc, MonitorInfo m, double W, double H)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(235, 28, 30, 36)), null, new Rect(0, H - Taskbar, W, Taskbar));
            var apps = _windows.Where(w => w.MonitorHandle == m.Handle).Select(w => _apps[w.Handle]).ToList();
            double size = 30, gap = 10, total = (apps.Count + 1) * (size + gap) - gap;
            double x = (W - total) / 2, y = H - Taskbar + (Taskbar - size) / 2;
            dc.DrawRoundedRectangle(new SolidColorBrush(C("#4C8DFF")), null, new Rect(x, y, size, size), 7, 7); // start
            foreach (var app in apps)
            {
                x += size + gap;
                DrawAppGlyph(dc, app, new Rect(x, y, size, size));
            }
            Text(dc, "9:41", 13, Brushes.White, W - 60, H - Taskbar + 15);
        }

        private static void DrawWindow(DrawingContext dc, Rect r, string title, App app, bool maximized)
        {
            double radius = maximized ? 0 : 8;
            if (!maximized)
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), null,
                    new Rect(r.X - 2, r.Y + 6, r.Width + 4, r.Height + 8), 12, 12);

            bool dark = app.Kind is Kind.Terminal or Kind.Chat or Kind.Media;
            var frame = new SolidColorBrush(dark ? C("#202228") : C("#F7F7F9"));
            dc.PushClip(new RectangleGeometry(r, radius, radius));
            dc.DrawRectangle(frame, null, r);

            // Title bar
            const double tb = 36;
            DrawAppGlyph(dc, app, new Rect(r.X + 12, r.Y + 9, 18, 18));
            Text(dc, title, 13, new SolidColorBrush(dark ? C("#E6E9EF") : C("#1F2328")), r.X + 40, r.Y + 10);
            var cap = new Pen(new SolidColorBrush(dark ? C("#9AA0AA") : C("#5A6270")), 1.2);
            double cx = r.Right - 132;
            dc.DrawLine(cap, new Point(cx + 18, r.Y + 18), new Point(cx + 28, r.Y + 18));
            dc.DrawRectangle(null, cap, new Rect(cx + 62, r.Y + 13, 10, 10));
            dc.DrawLine(cap, new Point(cx + 106, r.Y + 13), new Point(cx + 116, r.Y + 23));
            dc.DrawLine(cap, new Point(cx + 116, r.Y + 13), new Point(cx + 106, r.Y + 23));

            var content = new Rect(r.X, r.Y + tb, r.Width, Math.Max(0, r.Height - tb));
            var rnd = new Random(title.Length * 31 + (int)r.Width);
            switch (app.Kind)
            {
                case Kind.Document: DrawDocument(dc, content, app, rnd); break;
                case Kind.Spreadsheet: DrawSpreadsheet(dc, content, app, rnd); break;
                case Kind.Slides: DrawSlides(dc, content, app); break;
                case Kind.Mail: DrawMail(dc, content, app, rnd); break;
                case Kind.Terminal: DrawTerminal(dc, content, rnd); break;
                case Kind.Chat: DrawChat(dc, content, app, rnd); break;
                case Kind.Media: DrawMedia(dc, content, app); break;
                case Kind.Canvas: DrawCanvas(dc, content); break;
                default: DrawBrowser(dc, content, app, rnd); break;
            }
            dc.Pop();
            if (!maximized) dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 1), r, radius, radius);
        }

        private static void DrawDocument(DrawingContext dc, Rect c, App app, Random rnd)
        {
            dc.DrawRectangle(new SolidColorBrush(C("#E9EBEF")), null, c);
            dc.DrawRectangle(Brushes.White, null, new Rect(c.X, c.Y, c.Width, 44));
            for (int i = 0; i < 9; i++) Bar(dc, c.X + 16 + i * 46, c.Y + 16, 30, 12, C("#C9CED6"));
            double pw = Math.Min(c.Width * 0.7, 760), px = c.X + (c.Width - pw) / 2;
            var page = new Rect(px, c.Y + 64, pw, c.Height + 200);
            dc.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(C("#D5D9E0")), 1), page);
            double y = page.Y + 56;
            Bar(dc, px + 60, y, pw * 0.55, 22, app.Color); y += 50;
            while (y < c.Bottom)
            {
                for (int i = 0; i < 4 + rnd.Next(3) && y < c.Bottom; i++, y += 24)
                    Bar(dc, px + 60, y, (pw - 120) * (i == 0 ? 1 : 0.7 + rnd.NextDouble() * 0.3), 10, C("#C3C8D0"));
                y += 22;
            }
        }

        private static void DrawSpreadsheet(DrawingContext dc, Rect c, App app, Random rnd)
        {
            dc.DrawRectangle(Brushes.White, null, c);
            dc.DrawRectangle(new SolidColorBrush(C("#EEF6F1")), null, new Rect(c.X, c.Y, c.Width, 56));
            for (int i = 0; i < 10; i++) Bar(dc, c.X + 16 + i * 52, c.Y + 20, 36, 14, C("#A9D3BA"));
            var grid = new Pen(new SolidColorBrush(C("#E1E4E8")), 1);
            double top = c.Y + 56, rowH = 26, colW = 118;
            dc.DrawRectangle(new SolidColorBrush(C("#F3F4F6")), null, new Rect(c.X, top, c.Width, rowH));
            for (double y = top; y < c.Bottom; y += rowH) dc.DrawLine(grid, new Point(c.X, y), new Point(c.Right, y));
            for (double x = c.X + 48; x < c.Right; x += colW) dc.DrawLine(grid, new Point(x, top), new Point(x, c.Bottom));
            int row = 0;
            for (double y = top + rowH; y < c.Bottom - rowH; y += rowH, row++)
                for (double x = c.X + 48; x < c.Right - colW; x += colW)
                    Bar(dc, x + 10, y + 8, (colW - 30) * (0.3 + rnd.NextDouble() * 0.6), 10,
                        row == 0 ? app.Color : C("#B8BEC7"));
            // Chart
            var chart = new Rect(c.Right - 520, c.Y + 120, 460, 280);
            if (chart.X > c.X + 300)
            {
                dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(C("#D5D9E0")), 1), chart, 6, 6);
                for (int i = 0; i < 8; i++)
                {
                    double h = 40 + rnd.NextDouble() * 180;
                    Bar(dc, chart.X + 30 + i * 52, chart.Bottom - 24 - h, 30, h, i % 2 == 0 ? app.Color : C("#7FC8A0"));
                }
            }
        }

        private static void DrawSlides(DrawingContext dc, Rect c, App app)
        {
            dc.DrawRectangle(new SolidColorBrush(C("#E6E7EA")), null, c);
            double thumbW = 190;
            for (int i = 0; i < 6; i++)
            {
                var t = new Rect(c.X + 20, c.Y + 20 + i * 128, thumbW, thumbW * 9 / 16);
                dc.DrawRectangle(i == 2 ? new SolidColorBrush(app.Color) : Brushes.White,
                    new Pen(new SolidColorBrush(i == 2 ? C("#4C8DFF") : C("#CDD1D8")), i == 2 ? 3 : 1), t);
            }
            double sw = Math.Min(c.Width - thumbW - 100, (c.Height - 80) * 16 / 9), sh = sw * 9 / 16;
            var slide = new Rect(c.X + thumbW + 60 + (c.Width - thumbW - 100 - sw) / 2, c.Y + 40, sw, sh);
            dc.DrawRectangle(new LinearGradientBrush(app.Color, C("#7A2E7A"), 30), null, slide);
            Text(dc, "Sprint Review", sw / 14, Brushes.White, slide.X + sw * 0.08, slide.Y + sh * 0.34);
            Text(dc, "Week 42 · Highlights & next steps", sw / 34, new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                slide.X + sw * 0.08, slide.Y + sh * 0.52);
            Glow(dc, slide.Right - sw * 0.18, slide.Bottom - sh * 0.25, sh * 0.35, Color.FromArgb(90, 255, 255, 255));
        }

        private static void DrawMail(DrawingContext dc, Rect c, App app, Random rnd)
        {
            dc.DrawRectangle(Brushes.White, null, c);
            dc.DrawRectangle(new SolidColorBrush(C("#F1F4F8")), null, new Rect(c.X, c.Y, 200, c.Height));
            for (int i = 0; i < 7; i++) Bar(dc, c.X + 20, c.Y + 24 + i * 36, 120 - i * 8, 12, i == 0 ? app.Color : C("#B7C0CC"));
            double lx = c.X + 200, lw = Math.Min(380, c.Width * 0.38);
            for (int i = 0; i * 76 < c.Height; i++)
            {
                var item = new Rect(lx, c.Y + i * 76, lw, 76);
                if (i == 1) dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, app.Color.R, app.Color.G, app.Color.B)), null, item);
                dc.DrawEllipse(new SolidColorBrush(Palette(i)), null, new Point(item.X + 30, item.Y + 38), 16, 16);
                Bar(dc, item.X + 58, item.Y + 20, 120 + rnd.Next(80), 11, C("#4A5260"));
                Bar(dc, item.X + 58, item.Y + 42, lw - 90 - rnd.Next(60), 9, C("#B7C0CC"));
            }
            double rx = lx + lw + 30;
            Bar(dc, rx, c.Y + 30, 320, 18, C("#2A2F36"));
            dc.DrawEllipse(new SolidColorBrush(Palette(1)), null, new Point(rx + 18, c.Y + 88), 18, 18);
            Bar(dc, rx + 48, c.Y + 80, 160, 11, C("#4A5260"));
            for (int i = 0; i < 10; i++)
                Bar(dc, rx, c.Y + 140 + i * 24, (c.Right - rx - 40) * (0.6 + rnd.NextDouble() * 0.4), 9, C("#C3C8D0"));
        }

        private static void DrawTerminal(DrawingContext dc, Rect c, Random rnd)
        {
            dc.DrawRectangle(new SolidColorBrush(C("#0F1115")), null, c);
            for (double y = c.Y + 16; y < c.Bottom - 16; y += 22)
            {
                int kind = rnd.Next(5);
                Bar(dc, c.X + 16, y, 12, 10, kind == 0 ? C("#4C8DFF") : C("#38C172"));
                Bar(dc, c.X + 36, y, 80 + rnd.Next((int)Math.Max(100, c.Width - 140)), 10,
                    kind == 4 ? C("#E5B84C") : C("#8B93A3"));
            }
        }

        private static void DrawChat(DrawingContext dc, Rect c, App app, Random rnd)
        {
            dc.DrawRectangle(new SolidColorBrush(C("#1E1F24")), null, c);
            dc.DrawRectangle(new SolidColorBrush(C("#17181C")), null, new Rect(c.X, c.Y, 240, c.Height));
            for (int i = 0; i < 8; i++) Bar(dc, c.X + 20, c.Y + 24 + i * 34, 110 + rnd.Next(80), 11, i == 2 ? app.Color : C("#5B6070"));
            double y = c.Y + 30;
            for (int i = 0; y < c.Bottom - 80; i++)
            {
                bool mine = i % 3 == 2;
                double w = 220 + rnd.Next(260), h = 40 + rnd.Next(2) * 22;
                double x = mine ? c.Right - w - 30 : c.X + 300;
                if (!mine) dc.DrawEllipse(new SolidColorBrush(Palette(i)), null, new Point(c.X + 272, y + 18), 16, 16);
                dc.DrawRoundedRectangle(new SolidColorBrush(mine ? app.Color : C("#2C2E35")), null, new Rect(x, y, w, h), 12, 12);
                y += h + 18;
            }
        }

        private static void DrawMedia(DrawingContext dc, Rect c, App app)
        {
            dc.DrawRectangle(new LinearGradientBrush(C("#2A1418"), C("#121318"), 90), null, c);
            double s = Math.Min(c.Height * 0.5, 320);
            dc.DrawRoundedRectangle(new LinearGradientBrush(app.Color, C("#F2A65A"), 45), null, new Rect(c.X + 40, c.Y + 40, s, s), 10, 10);
            Bar(dc, c.X + 40, c.Y + 60 + s, s * 0.8, 16, C("#E6E9EF"));
            Bar(dc, c.X + 40, c.Y + 90 + s, s * 0.5, 11, C("#8B93A3"));
        }

        private static void DrawCanvas(DrawingContext dc, Rect c)
        {
            dc.DrawRectangle(new SolidColorBrush(C("#E4E6EB")), null, c);
            dc.DrawRectangle(Brushes.White, null, new Rect(c.X, c.Y, 56, c.Height));
            for (int i = 0; i < 8; i++) dc.DrawRoundedRectangle(new SolidColorBrush(C("#C9CED6")), null, new Rect(c.X + 16, c.Y + 20 + i * 44, 24, 24), 5, 5);
            var board = new Rect(c.X + 110, c.Y + 50, c.Width - 220, c.Height - 100);
            dc.DrawRectangle(Brushes.White, null, board);
            double u = Math.Min(board.Width, board.Height);
            dc.DrawEllipse(new SolidColorBrush(C("#E0457B")), null, new Point(board.X + board.Width * 0.3, board.Y + board.Height * 0.42), u * 0.22, u * 0.22);
            dc.DrawRoundedRectangle(new SolidColorBrush(C("#4C8DFF")), null,
                new Rect(board.X + board.Width * 0.45, board.Y + board.Height * 0.2, u * 0.38, u * 0.38), 18, 18);
            dc.DrawEllipse(new SolidColorBrush(C("#F2C14E")), null, new Point(board.X + board.Width * 0.62, board.Y + board.Height * 0.72), u * 0.12, u * 0.12);
            Text(dc, "Aa", u * 0.16, new SolidColorBrush(C("#1F2328")), board.X + board.Width * 0.12, board.Y + board.Height * 0.7);
        }

        private static void DrawBrowser(DrawingContext dc, Rect c, App app, Random rnd)
        {
            dc.DrawRectangle(Brushes.White, null, c);
            dc.DrawRoundedRectangle(new SolidColorBrush(C("#EEF0F3")), null, new Rect(c.X + 80, c.Y + 10, c.Width - 160, 30), 15, 15);
            var hero = new Rect(c.X, c.Y + 52, c.Width, Math.Min(260, c.Height * 0.35));
            dc.DrawRectangle(new LinearGradientBrush(app.Color, C("#2B6CE6"), 20), null, hero);
            Bar(dc, hero.X + 60, hero.Y + hero.Height * 0.4, hero.Width * 0.35, 24, Colors.White);
            double cw = (c.Width - 120 - 60) / 4;
            for (int i = 0; i < 4; i++)
            {
                var card = new Rect(c.X + 60 + i * (cw + 20), hero.Bottom + 30, cw, 180);
                dc.DrawRoundedRectangle(new SolidColorBrush(C("#F4F5F7")), null, card, 8, 8);
                Bar(dc, card.X + 16, card.Y + 20, cw * 0.6, 12, C("#4A5260"));
                Bar(dc, card.X + 16, card.Y + 44, cw * (0.5 + rnd.NextDouble() * 0.3), 9, C("#B7C0CC"));
            }
        }

        /// <summary>The control monitor shows ScreenDeck itself (maximized), drawn as a simplified mock-up.</summary>
        private static void DrawScreenDeck(DrawingContext dc, double W, double H)
        {
            dc.DrawRectangle(new SolidColorBrush(C("#16181D")), null, new Rect(0, 0, W, H));
            Text(dc, "ScreenDeck", 30, new SolidColorBrush(C("#E6E9EF")), 30, 40);
            var panel = new SolidColorBrush(C("#1F232B"));
            dc.DrawRoundedRectangle(panel, null, new Rect(30, 100, W - 60, H * 0.5), 14, 14);
            double tw = (W - 140) / 3;
            for (int i = 0; i < 3; i++)
                dc.DrawRectangle(new LinearGradientBrush(i == 0 ? C("#1B3A6B") : i == 1 ? C("#14323A") : C("#5A2340"), C("#2A2F6B"), 35),
                    new Pen(new SolidColorBrush(i == 0 ? C("#4C8DFF") : C("#343B48")), 4), new Rect(50 + i * (tw + 20), 140, tw, tw * 9 / 16));
            dc.DrawRoundedRectangle(panel, null, new Rect(30, 120 + H * 0.5, W - 60, H * 0.5 - 150), 14, 14);
            for (int i = 0; i < 5; i++)
                dc.DrawRoundedRectangle(new SolidColorBrush(C("#2A303B")), null, new Rect(56 + i * 400, 180 + H * 0.5, 380, 90), 10, 10);
        }

        // ───────────────────────── Helpers ─────────────────────────

        private static ImageSource MakeIcon(App app)
        {
            var group = new DrawingGroup();
            using (var dc = group.Open()) DrawAppGlyph(dc, app, new Rect(0, 0, 64, 64));
            group.Freeze();
            var img = new DrawingImage(group);
            img.Freeze();
            return img;
        }

        private static void DrawAppGlyph(DrawingContext dc, App app, Rect r)
        {
            dc.DrawRoundedRectangle(new LinearGradientBrush(Lighten(app.Color), app.Color, 90), null, r, r.Width * 0.22, r.Width * 0.22);
            var ft = Format(app.Letter, r.Height * (app.Letter.Length > 1 ? 0.42 : 0.56), Brushes.White, FontWeights.Bold);
            dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
        }

        private static void Text(DrawingContext dc, string s, double size, Brush brush, double x, double y) =>
            dc.DrawText(Format(s, size, brush, FontWeights.SemiBold), new Point(x, y));

        private static FormattedText Format(string s, double size, Brush brush, FontWeight weight) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), Math.Max(1, size), brush, 1.0);

        private static void Bar(DrawingContext dc, double x, double y, double w, double h, Color color) =>
            dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(x, y, Math.Max(0, w), h), h / 2, h / 2);

        private static void Glow(DrawingContext dc, double x, double y, double radius, Color color)
        {
            var brush = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B));
            dc.DrawEllipse(brush, null, new Point(x, y), radius, radius);
        }

        private static Color Palette(int i) => (i % 5) switch
        {
            0 => C("#4C8DFF"), 1 => C("#E0457B"), 2 => C("#38C172"), 3 => C("#F08C00"), _ => C("#7C5CFF"),
        };

        private static Color Lighten(Color c) =>
            Color.FromRgb((byte)(c.R + (255 - c.R) * 0.25), (byte)(c.G + (255 - c.G) * 0.25), (byte)(c.B + (255 - c.B) * 0.25));

        private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    }
}
