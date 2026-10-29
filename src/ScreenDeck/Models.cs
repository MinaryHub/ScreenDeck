using System;
using System.Windows;
using System.Windows.Media;

namespace ScreenDeck
{
    public sealed class MonitorInfo
    {
        public IntPtr Handle { get; init; }
        public int Index { get; init; }              // 1-based, ordered left-to-right
        public string DeviceName { get; init; } = "";
        public Int32Rect Bounds { get; init; }       // physical pixels, virtual-screen coords
        public Int32Rect WorkArea { get; init; }
        public bool IsPrimary { get; init; }

        public string? CustomName { get; set; }      // user-assigned, persisted in Settings.MonitorNames

        public string DefaultName => $"모니터 {Index}";
        public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? DefaultName : CustomName;
        public string Description => $"{Bounds.Width}×{Bounds.Height}" + (IsPrimary ? " · 주 모니터" : "");
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

        public string StateText => IsMinimized ? "최소화됨" : IsMaximized ? "최대화" : "";
    }

    public enum MoveResult { Ok, AccessDenied, Gone, Failed }
}
