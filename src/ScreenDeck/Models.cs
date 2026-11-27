using System;
using System.Windows;
using System.Windows.Media;

namespace ScreenDeck
{
    public sealed class MonitorInfo
    {
        public IntPtr Handle { get; init; }
        public int Index { get; init; }              // 1-based, ordered left-to-right
        public string DeviceName { get; init; } = "";   // \\.\DISPLAYn: can change after a reboot or reconnect
        public string StableId { get; init; } = "";     // monitor device interface path (EDID-based); "" if unknown
        public Int32Rect Bounds { get; init; }       // physical pixels, virtual-screen coords
        public Int32Rect WorkArea { get; init; }
        public bool IsPrimary { get; init; }

        public string? CustomName { get; set; }      // user-assigned, persisted in Settings.MonitorNames

        /// <summary>Key the monitor is remembered by in the settings: the stable ID when known.</summary>
        public string SettingsKey => string.IsNullOrEmpty(StableId) ? DeviceName : StableId;

        public string DefaultName => $"Monitor {Index}";
        public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? DefaultName : CustomName;
        public string Description => $"{Bounds.Width}×{Bounds.Height}" + (IsPrimary ? " · Primary" : "");
        public override string ToString() => $"{DisplayName}  ({Description})";
    }

    public sealed class WindowInfo
    {
        public IntPtr Handle { get; init; }
        public string Title { get; set; } = "";
        public string ProcessName { get; init; } = "";
        public string? ProcessPath { get; init; }
        public uint ProcessId { get; init; }
        public IntPtr MonitorHandle { get; set; }
        public Int32Rect Bounds { get; set; }
        public bool IsMinimized { get; set; }
        public bool IsMaximized { get; set; }
        public ImageSource? Icon { get; set; }

        public string StateText => IsMinimized ? "Minimized" : IsMaximized ? "Maximized" : "";
    }

    public enum MoveResult { Ok, AccessDenied, Gone, Failed }
}
