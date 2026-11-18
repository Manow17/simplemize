using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace Simplemize.Ui
{
    /// <summary>Two palettes: "paper" (light) and "carbon" (dark). One accent: signal orange.</summary>
    public static class Theme
    {
        public static bool Dark { get; private set; }
        public static event Action Changed;

        static readonly Dictionary<string, string> Paper = new Dictionary<string, string>
        {
            ["Bg"] = "#F1EFEA",
            ["Rail"] = "#E8E5DE",
            ["Panel"] = "#FAF9F6",
            ["Ink"] = "#161513",
            ["Ink2"] = "#5C5850",
            ["Ink3"] = "#8F8A80",
            ["Line"] = "#DAD6CC",
            ["Line2"] = "#C6C1B5",
            ["Hover"] = "#E9E6DF",
            ["Accent"] = "#FF5A1F",
            ["AccentHover"] = "#FF7440",
            ["AccentInk"] = "#161513",
            ["AccentSoft"] = "#FBE3D7",
            ["Good"] = "#2E7A4A",
            ["Warn"] = "#A8630A",
            ["Bad"] = "#C2381B",
            ["Scrim"] = "#8C161513",
        };

        static readonly Dictionary<string, string> Carbon = new Dictionary<string, string>
        {
            ["Bg"] = "#141413",
            ["Rail"] = "#1A1A18",
            ["Panel"] = "#1E1D1B",
            ["Ink"] = "#ECE9E2",
            ["Ink2"] = "#A6A196",
            ["Ink3"] = "#6E6A62",
            ["Line"] = "#2A2926",
            ["Line2"] = "#3B3A36",
            ["Hover"] = "#211F1D",
            ["Accent"] = "#FF6A2B",
            ["AccentHover"] = "#FF8250",
            ["AccentInk"] = "#141413",
            ["AccentSoft"] = "#3A2218",
            ["Good"] = "#62C287",
            ["Warn"] = "#E6A845",
            ["Bad"] = "#F0715A",
            ["Scrim"] = "#B3080807",
        };

        public static void Load(Application app)
        {
            using (var s = typeof(Theme).Assembly.GetManifestResourceStream("Simplemize.Theme.xaml"))
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(s));
        }

        public static void Apply(bool dark)
        {
            Dark = dark;
            foreach (var kv in dark ? Carbon : Paper)
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value));
                b.Freeze();
                Application.Current.Resources[kv.Key] = b;
            }
            Changed?.Invoke();
        }

        public static Color C(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;
        public static Brush B(string key) => (Brush)Application.Current.Resources[key];

        static string SettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Simplemize", "theme.txt");

        public static bool Preferred()
        {
            try
            {
                if (File.Exists(SettingsFile)) return File.ReadAllText(SettingsFile).Trim() == "dark";
            }
            catch { }
            try
            {
                var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return v is int i && i == 0;
            }
            catch { return false; }
        }

        public static void Remember(bool dark)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllText(SettingsFile, dark ? "dark" : "light");
            }
            catch { }
        }
    }
}
