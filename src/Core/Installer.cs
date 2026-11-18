using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Simplemize
{
    /// <summary>
    /// Per-user install, no administrator rights needed: copies Simplemize.exe to
    /// %LOCALAPPDATA%\Programs\Simplemize, adds a Start menu shortcut (so Start search finds it) and an entry
    /// in Settings › Apps (so it can be uninstalled like any app). Nothing else: no service, task or startup entry.
    /// </summary>
    public static class Installer
    {
        public sealed class Paths
        {
            public string InstallDir, Exe, StartMenuLink, DesktopLink;
            /// <summary>HKCU uninstall key name, or null to skip the Apps entry (self-test).</summary>
            public string UninstallKey;

            public static Paths Default
            {
                get
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Simplemize");
                    return new Paths
                    {
                        InstallDir = dir,
                        Exe = Path.Combine(dir, "Simplemize.exe"),
                        StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Simplemize.lnk"),
                        DesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Simplemize.lnk"),
                        UninstallKey = "Simplemize",
                    };
                }
            }
        }

        const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

        public static string RunningExe => Process.GetCurrentProcess().MainModule.FileName;
        public static Version RunningVersion => Assembly.GetExecutingAssembly().GetName().Version;

        public static bool IsInstalled(Paths p) => File.Exists(p.Exe) && File.Exists(p.StartMenuLink);

        public static bool RunningFromInstall(Paths p) =>
            string.Equals(Path.GetFullPath(RunningExe), Path.GetFullPath(p.Exe), StringComparison.OrdinalIgnoreCase);

        public static Version InstalledVersion(Paths p)
        {
            try { return File.Exists(p.Exe) ? new Version(FileVersionInfo.GetVersionInfo(p.Exe).FileVersion) : null; }
            catch { return null; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFile(string path);

        /// <summary>Copies the running exe into place (unless it already runs from there), then adds the shortcuts and the Apps entry.</summary>
        public static void Install(Paths p, bool desktopShortcut, Log log, string sourceExe = null)
        {
            sourceExe = sourceExe ?? RunningExe;
            Directory.CreateDirectory(p.InstallDir);
            if (!string.Equals(Path.GetFullPath(sourceExe), Path.GetFullPath(p.Exe), StringComparison.OrdinalIgnoreCase))
            {
                var tmp = p.Exe + ".new";
                File.Copy(sourceExe, tmp, true);
                if (File.Exists(p.Exe)) File.Delete(p.Exe);
                File.Move(tmp, p.Exe);
            }
            // The copy carries the downloaded file's "from the internet" mark; the person already chose to run it.
            DeleteFile(p.Exe + ":Zone.Identifier");

            Shortcut(p.StartMenuLink, p.Exe);
            if (desktopShortcut) Shortcut(p.DesktopLink, p.Exe);
            else if (File.Exists(p.DesktopLink)) File.Delete(p.DesktopLink);

            if (p.UninstallKey != null)
            {
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallRoot + p.UninstallKey))
                {
                    var v = FileVersionInfo.GetVersionInfo(p.Exe);
                    k.SetValue("DisplayName", "Simplemize");
                    k.SetValue("DisplayVersion", v.ProductVersion ?? v.FileVersion ?? "");
                    k.SetValue("Publisher", "Simplemize (open source)");
                    k.SetValue("DisplayIcon", p.Exe + ",0");
                    k.SetValue("InstallLocation", p.InstallDir);
                    k.SetValue("UninstallString", "\"" + p.Exe + "\" --uninstall");
                    k.SetValue("QuietUninstallString", "\"" + p.Exe + "\" --uninstall --quiet");
                    k.SetValue("URLInfoAbout", "https://manow17.github.io/simplemize/");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(p.Exe).Length / 1024), RegistryValueKind.DWord);
                    k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                }
            }
            log?.Info("installed to " + p.InstallDir + (desktopShortcut ? " (with desktop shortcut)" : ""));
        }

        /// <summary>
        /// Removes the shortcuts, the Apps entry and the program file. The backup in C:\ProgramData\Simplemize stays,
        /// so changes can still be undone after reinstalling. Returns true if the file deletion had to be scheduled
        /// (because it is the running exe).
        /// </summary>
        public static bool Uninstall(Paths p, Log log)
        {
            foreach (var link in new[] { p.StartMenuLink, p.DesktopLink })
                try { if (File.Exists(link)) File.Delete(link); } catch { }
            if (p.UninstallKey != null)
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallRoot + p.UninstallKey, false); } catch { }

            bool scheduled = false;
            if (File.Exists(p.Exe))
            {
                if (string.Equals(Path.GetFullPath(RunningExe), Path.GetFullPath(p.Exe), StringComparison.OrdinalIgnoreCase))
                {
                    // A running program can't delete itself: let a hidden command prompt do it two seconds after we exit.
                    var cmd = "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + p.Exe + "\" & rmdir \"" + p.InstallDir + "\"";
                    Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), cmd)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    });
                    scheduled = true;
                }
                else
                {
                    File.Delete(p.Exe);
                    try { Directory.Delete(p.InstallDir, false); } catch { }
                }
            }
            log?.Info("uninstalled from " + p.InstallDir);
            return scheduled;
        }

        static void Shortcut(string linkPath, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
            object shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic sh = shell;
                dynamic lnk = sh.CreateShortcut(linkPath);
                lnk.TargetPath = target;
                lnk.WorkingDirectory = Path.GetDirectoryName(target);
                lnk.IconLocation = target + ",0";
                lnk.Description = "Simplemize: switch off the parts of Windows you never asked for";
                lnk.Save();
            }
            finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
        }

        /// <summary>Reads where a shortcut points (used by the self-test).</summary>
        public static string ShortcutTarget(string linkPath)
        {
            object shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic sh = shell;
                dynamic lnk = sh.CreateShortcut(linkPath);
                return lnk.TargetPath;
            }
            catch { return null; }
            finally { if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); }
        }
    }
}
