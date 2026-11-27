using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ScreenDeck
{
    public sealed class Settings
    {
        public string? ControlMonitorDevice { get; set; }   // MonitorInfo.SettingsKey (older versions: DeviceName)
        public bool MaximizeOnMove { get; set; } = true;
        public bool FadeOnMove { get; set; } = true;
        public int FadeDurationMs { get; set; } = FadeOverlay.DefaultDurationMs;
        public bool GpuCapture { get; set; }   // live view: DXGI Desktop Duplication instead of GDI (opt-in for now)
        // MonitorInfo.SettingsKey → custom name. Older versions keyed by DeviceName; such entries are moved to
        // the stable key the first time their monitor is seen.
        public Dictionary<string, string> MonitorNames { get; set; } = new();

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenDeck", "settings.json");

        public static Settings Load()
        {
            if (DemoData.Enabled) return new Settings();   // demo never reads or writes the real settings
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                    s.MonitorNames ??= new();   // "MonitorNames": null in a hand-edited file
                    s.FadeDurationMs = Math.Clamp(s.FadeDurationMs, FadeOverlay.MinDurationMs, FadeOverlay.MaxDurationMs);
                    return s;
                }
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            if (DemoData.Enabled) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                // Write a temp file and swap it in, so a crash mid-write can't leave a truncated settings file.
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { }
        }
    }
}
