using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ScreenDeck
{
    public sealed class Settings
    {
        public string? ControlMonitorDevice { get; set; }
        public bool MaximizeOnMove { get; set; } = true;
        public Dictionary<string, string> MonitorNames { get; set; } = new();   // DeviceName → custom name

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenDeck", "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
            }
            catch { }
        }
    }
}
