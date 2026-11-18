using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Simplemize
{
    /// <summary>An app that starts when you sign in (Run keys and Startup folders), like Task Manager's Startup tab.</summary>
    public sealed class StartupItem
    {
        public string Name;           // value name or file name
        public string Command;        // command line or shortcut target
        public string Where;          // short label for the UI
        public Hive ApprovedHive;
        public string ApprovedPath;   // ...\StartupApproved\Run | Run32 | StartupFolder
        public bool Enabled;
        public string Publisher;
        public bool NeedsAdmin => ApprovedHive == Hive.HKLM;

        public string DisplayName
        {
            get
            {
                var n = Name;
                if (n.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                return n;
            }
        }
    }

    public static class Startup
    {
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public static List<StartupItem> List(IRegistry reg)
        {
            var items = new List<StartupItem>();
            AddRun(reg, items, Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Run", Approved + "Run", "Your account");
            AddRun(reg, items, Hive.HKLM, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", Approved + "Run", "All accounts");
            AddRun(reg, items, Hive.HKLM, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", Approved + "Run32", "All accounts");
            AddFolder(reg, items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Hive.HKCU, "Startup folder");
            AddFolder(reg, items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Hive.HKLM, "Startup folder (all)");
            return items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static void AddRun(IRegistry reg, List<StartupItem> items, Hive hive, string path, string approved, string where)
        {
            foreach (var name in reg.ValueNames(hive, path))
            {
                if (string.IsNullOrEmpty(name)) continue;
                var cmd = reg.Get(hive, path, name)?.Data as string;
                if (string.IsNullOrWhiteSpace(cmd)) continue;
                var item = new StartupItem
                {
                    Name = name,
                    Command = cmd,
                    Where = where,
                    ApprovedHive = hive,
                    ApprovedPath = approved,
                };
                item.Enabled = IsEnabled(reg, item);
                item.Publisher = Publisher(ExtractExe(cmd));
                items.Add(item);
            }
        }

        static void AddFolder(IRegistry reg, List<StartupItem> items, string dir, Hive hive, string where)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir))
            {
                var file = Path.GetFileName(f);
                if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(f) ?? f : f;
                var item = new StartupItem
                {
                    Name = file,
                    Command = target,
                    Where = where,
                    ApprovedHive = hive,
                    ApprovedPath = Approved + "StartupFolder",
                };
                item.Enabled = IsEnabled(reg, item);
                item.Publisher = Publisher(ExtractExe(target));
                items.Add(item);
            }
        }

        static bool IsEnabled(IRegistry reg, StartupItem item)
        {
            var v = reg.Get(item.ApprovedHive, item.ApprovedPath, item.Name);
            // Task Manager writes 02 (on) or 03 (off) as the first byte. Odd = off.
            return !(v?.Data is byte[] b && b.Length > 0 && (b[0] & 1) == 1);
        }

        public static void SetEnabled(IRegistry reg, Log log, StartupItem item, bool enabled)
        {
            reg.Set(item.ApprovedHive, item.ApprovedPath, item.Name, RegistryValueKind.Binary, StartupOff.Blob(enabled));
            item.Enabled = enabled;
            log.Info("startup app \"" + item.Name + "\" -> " + (enabled ? "on" : "off"));
        }

        public static int CountEnabled(IRegistry reg)
        {
            try { return List(reg).Count(i => i.Enabled); } catch { return -1; }
        }

        public static string ExtractExe(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            var m = Regex.Match(cmd, "^\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;
            int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? cmd.Substring(0, i + 4) : cmd.Split(' ')[0];
        }

        static string Publisher(string exe)
        {
            try
            {
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
                var vi = FileVersionInfo.GetVersionInfo(exe);
                return string.IsNullOrWhiteSpace(vi.CompanyName) ? null : vi.CompanyName.Trim();
            }
            catch { return null; }
        }

        static string ShortcutTarget(string lnk)
        {
            object shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic sh = shell;
                dynamic sc = sh.CreateShortcut(lnk);
                string t = sc.TargetPath;
                string a = sc.Arguments;
                if (string.IsNullOrEmpty(t)) return null;
                return string.IsNullOrEmpty(a) ? t : "\"" + t + "\" " + a;
            }
            catch { return null; }
            finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
        }
    }
}
