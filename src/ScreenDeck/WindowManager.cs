using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using static ScreenDeck.Native;

namespace ScreenDeck
{
    internal static class WindowManager
    {
        private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
            "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow",
        };

        public static List<MonitorInfo> GetMonitors()
        {
            var raw = new List<(IntPtr h, MONITORINFOEX mi)>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(h, ref mi)) raw.Add((h, mi));
                return true;
            }, IntPtr.Zero);

            return raw
                .OrderBy(m => m.mi.rcMonitor.Left).ThenBy(m => m.mi.rcMonitor.Top)
                .Select((m, i) => new MonitorInfo
                {
                    Handle = m.h,
                    Index = i + 1,
                    DeviceName = m.mi.szDevice,
                    StableId = GetMonitorDeviceId(m.mi.szDevice),
                    Bounds = ToRect(m.mi.rcMonitor),
                    WorkArea = ToRect(m.mi.rcWork),
                    IsPrimary = (m.mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                })
                .ToList();
        }

        /// <summary>
        /// Device interface path of the monitor shown on a display output, e.g.
        /// <c>\\?\DISPLAY#DEL4100#5&amp;...#{e6f07b5f-...}</c>. Unlike <c>\\.\DISPLAYn</c> it names the physical monitor
        /// (from its EDID) on its connector, so it survives reboots and other monitors being plugged in or out.
        /// </summary>
        private static string GetMonitorDeviceId(string outputDevice)
        {
            try
            {
                for (uint i = 0; ; i++)
                {
                    var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    if (!EnumDisplayDevices(outputDevice, i, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)) break;
                    if ((dd.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0 && !string.IsNullOrEmpty(dd.DeviceID))
                        return dd.DeviceID;
                }
            }
            catch { }
            return "";
        }

        /// <summary>Top-level, user-visible application windows in Z-order (topmost first).</summary>
        // Process paths by PID, kept between refreshes. Entries of PIDs that no longer own a listed window are
        // dropped on every refresh, so a reused PID can't inherit a stale path. Used from the UI thread only.
        private static readonly Dictionary<uint, string?> PathCache = new();

        public static List<WindowInfo> GetWindows(uint excludePid)
        {
            var list = new List<WindowInfo>();
            var seenPids = new HashSet<uint>();

            EnumWindows((hWnd, _) =>
            {
                if (!IsAppWindow(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == excludePid) return true;

                seenPids.Add(pid);
                if (!PathCache.TryGetValue(pid, out string? path))
                    PathCache[pid] = path = NativeProcess.GetProcessPath(pid);

                list.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Title = GetText(hWnd),
                    ProcessId = pid,
                    ProcessPath = path,
                    ProcessName = path != null ? Path.GetFileNameWithoutExtension(path) : $"PID {pid}",
                    MonitorHandle = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST),
                    Bounds = GetVisualBounds(hWnd),
                    IsMinimized = IsIconic(hWnd),
                    IsMaximized = IsZoomed(hWnd),
                });
                return true;
            }, IntPtr.Zero);

            foreach (uint gone in PathCache.Keys.Where(p => !seenPids.Contains(p)).ToList()) PathCache.Remove(gone);
            return list;
        }

        private static bool IsAppWindow(IntPtr hWnd)
        {
            if (!IsWindowVisible(hWnd)) return false;
            if (GetWindowTextLength(hWnd) == 0) return false;

            long style = GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();
            long ex = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
            if ((style & WS_CHILD) != 0) return false;

            bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
            if (!appWindow)
            {
                if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
                if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return false;
                if ((ex & WS_EX_NOACTIVATE) != 0) return false;
            }

            if (IsCloaked(hWnd)) return false; // other virtual desktops, suspended UWP, etc.
            if (IgnoredClasses.Contains(GetClass(hWnd))) return false;
            return true;
        }

        /// <summary>Window bounds without the invisible resize borders of Windows 10/11.</summary>
        public static Int32Rect GetVisualBounds(IntPtr hWnd)
        {
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hWnd, out r);
            return ToRect(r);
        }

        /// <summary>
        /// Moves a window to the target monitor. When <paramref name="maximize"/> is set the window is
        /// maximized there; borderless full-screen windows (video players, games) are stretched to fill
        /// the target monitor. Otherwise the window keeps its relative position and size.
        /// </summary>
        public static MoveResult MoveToMonitor(IntPtr hWnd, MonitorInfo source, MonitorInfo target, bool maximize) =>
            MoveToMonitor(hWnd, source, target, maximize, out _);

        /// <param name="maximized">Whether the window ends up maximized (fixed-size windows are not).</param>
        public static MoveResult MoveToMonitor(IntPtr hWnd, MonitorInfo source, MonitorInfo target, bool maximize,
            out bool maximized)
        {
            maximized = false;
            if (!IsWindow(hWnd)) return MoveResult.Gone;

            bool wasMinimized = IsIconic(hWnd);
            bool wasMaximized = IsZoomed(hWnd);
            long style = GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();

            // Borderless full-screen: no title bar, and the window rect covers the whole source monitor without
            // being "maximized". (A normal window just dragged larger than the monitor still has its caption.)
            GetWindowRect(hWnd, out RECT cur);
            bool borderlessFullscreen = !wasMinimized && !wasMaximized && (style & WS_CAPTION) != WS_CAPTION &&
                cur.Left <= source.Bounds.X && cur.Top <= source.Bounds.Y &&
                cur.Right >= source.Bounds.X + source.Bounds.Width &&
                cur.Bottom >= source.Bounds.Y + source.Bounds.Height;

            // A window minimized from the maximized state restores to maximized, so restore twice if needed.
            for (int i = 0; i < 2 && (IsIconic(hWnd) || IsZoomed(hWnd)); i++)
                ShowWindow(hWnd, SW_RESTORE);

            bool ok;
            if (borderlessFullscreen)
            {
                ok = SetWindowPos(hWnd, IntPtr.Zero, target.Bounds.X, target.Bounds.Y,
                    target.Bounds.Width, target.Bounds.Height, SWP_NOACTIVATE | SWP_FRAMECHANGED);
                return Result(ok, hWnd);
            }

            // Restored rect (wp.rcNormalPosition is in workspace coords; GetWindowRect after restore is reliable).
            GetWindowRect(hWnd, out RECT restored);
            int w = restored.Width, h = restored.Height;

            var sw = source.WorkArea;
            var tw = target.WorkArea;
            w = Math.Min(w, tw.Width);
            h = Math.Min(h, tw.Height);

            // Keep relative position inside the work area.
            double rx = sw.Width > restored.Width ? (restored.Left - sw.X) / (double)(sw.Width - restored.Width) : 0.5;
            double ry = sw.Height > restored.Height ? (restored.Top - sw.Y) / (double)(sw.Height - restored.Height) : 0.5;
            rx = Math.Clamp(double.IsFinite(rx) ? rx : 0.5, 0, 1);
            ry = Math.Clamp(double.IsFinite(ry) ? ry : 0.5, 0, 1);
            int x = tw.X + (int)Math.Round((tw.Width - w) * rx);
            int y = tw.Y + (int)Math.Round((tw.Height - h) * ry);

            ok = SetWindowPos(hWnd, IntPtr.Zero /* HWND_TOP */, x, y, w, h, SWP_NOACTIVATE);
            if (!ok) return Result(false, hWnd);

            // A per-monitor DPI aware program rescales itself (WM_DPICHANGED) when it lands on a monitor with a
            // different DPI, which shifts it away from the spot computed above and can push it past the work area.
            // Keep the size it chose (capped to the work area) and place that at the same relative position.
            if (GetWindowRect(hWnd, out RECT placed) && (placed.Width != w || placed.Height != h))
            {
                int w2 = Math.Min(placed.Width, tw.Width), h2 = Math.Min(placed.Height, tw.Height);
                int x2 = tw.X + (int)Math.Round((tw.Width - w2) * rx);
                int y2 = tw.Y + (int)Math.Round((tw.Height - h2) * ry);
                uint flags = SWP_NOACTIVATE | SWP_NOZORDER;
                if (w2 == placed.Width && h2 == placed.Height) flags |= SWP_NOSIZE;
                SetWindowPos(hWnd, IntPtr.Zero, x2, y2, w2, h2, flags);
            }

            // Fixed-size windows (dialogs, tool windows) have no maximize box: keep their size instead of stretching them.
            bool canMaximize = (style & WS_MAXIMIZEBOX) != 0;
            maximized = wasMaximized || (maximize && canMaximize);
            if (maximized)
                ShowWindow(hWnd, SW_MAXIMIZE);
            else if (wasMinimized && !maximize)
                ShowWindow(hWnd, SW_SHOWMINIMIZED);

            return MoveResult.Ok;
        }

        private static MoveResult Result(bool ok, IntPtr hWnd)
        {
            if (ok) return MoveResult.Ok;
            int err = Marshal.GetLastWin32Error();
            if (!IsWindow(hWnd)) return MoveResult.Gone;
            return err == 5 ? MoveResult.AccessDenied : MoveResult.Failed;
        }

        public static void Activate(IntPtr hWnd)
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }

        private static Int32Rect ToRect(RECT r) => new(r.Left, r.Top, r.Width, r.Height);
    }
}
