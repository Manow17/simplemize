using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Simplemize
{
    public sealed class SysInfo
    {
        public string OsName = "Windows";
        public string OsRelease = "";
        public int Build;
        public int Ubr;
        public string Cpu = "Unknown processor";
        public string Gpu = "Unknown graphics";
        public long RamBytes;
        public bool IsLaptop;
        /// <summary>true = Windows is on a spinning hard drive, false = SSD, null = unknown (filled in later).</summary>
        public bool? SystemOnHdd;

        public bool IsWin11 => Build >= 22000;

        public static SysInfo Read()
        {
            var reg = new RealRegistry();
            var s = new SysInfo();
            const string cv = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            try
            {
                int.TryParse(reg.Get(Hive.HKLM, cv, "CurrentBuildNumber")?.Data as string, out s.Build);
                var ubr = reg.Get(Hive.HKLM, cv, "UBR");
                if (ubr != null && ubr.Data is int u) s.Ubr = u;
                var name = reg.Get(Hive.HKLM, cv, "ProductName")?.Data as string ?? "Windows";
                // Windows 11 still reports "Windows 10" in ProductName.
                if (s.Build >= 22000) name = name.Replace("Windows 10", "Windows 11");
                s.OsName = name;
                s.OsRelease = reg.Get(Hive.HKLM, cv, "DisplayVersion")?.Data as string ?? reg.Get(Hive.HKLM, cv, "ReleaseId")?.Data as string ?? "";
            }
            catch { }

            try
            {
                var cpu = reg.Get(Hive.HKLM, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString")?.Data as string;
                if (!string.IsNullOrWhiteSpace(cpu)) s.Cpu = System.Text.RegularExpressions.Regex.Replace(cpu.Trim(), @"\s+", " ").Replace("(R)", "").Replace("(TM)", "").Replace(" CPU", "");
            }
            catch { }

            s.Gpu = ReadGpu(reg) ?? s.Gpu;

            var mem = new Native.MEMORYSTATUSEX();
            if (Native.GlobalMemoryStatusEx(mem)) s.RamBytes = (long)mem.ullTotalPhys;

            if (Native.GetSystemPowerStatus(out var ps))
                s.IsLaptop = ps.BatteryFlag != 128 && ps.BatteryFlag != 255;

            return s;
        }

        static string ReadGpu(IRegistry reg)
        {
            const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            string fallback = null;
            try
            {
                foreach (var sub in reg.SubKeyNames(Hive.HKLM, cls).Where(n => n.Length == 4 && n.All(char.IsDigit)).OrderBy(n => n))
                {
                    var desc = reg.Get(Hive.HKLM, cls + "\\" + sub, "DriverDesc")?.Data as string;
                    if (string.IsNullOrWhiteSpace(desc)) continue;
                    var d = desc.ToLowerInvariant();
                    if (d.Contains("basic") || d.Contains("remote") || d.Contains("virtual") || d.Contains("parsec") || d.Contains("hyper-v")) { fallback = fallback ?? desc; continue; }
                    // Prefer a discrete card over integrated graphics.
                    if (d.Contains("nvidia") || d.Contains("radeon rx") || d.Contains("arc")) return desc;
                    fallback = fallback == null || fallback.ToLowerInvariant().Contains("basic") ? desc : fallback;
                }
            }
            catch { }
            if (fallback != null) return fallback;
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                    foreach (ManagementObject o in s.Get())
                        using (o) return Convert.ToString(o["Name"]);
            }
            catch { }
            return null;
        }

        /// <summary>Asks Windows' storage provider whether the system drive is an HDD. Slow-ish (WMI), call off the UI thread.</summary>
        public static bool? DetectSystemOnHdd()
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                char letter = char.ToUpperInvariant((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0]);
                long disk = -1;
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DriveLetter, DiskNumber FROM MSFT_Partition")))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            var dl = o["DriveLetter"];
                            if (dl == null) continue;
                            char c = char.ToUpperInvariant(Convert.ToChar(dl, CultureInfo.InvariantCulture));
                            if (c == letter) disk = Convert.ToInt64(o["DiskNumber"], CultureInfo.InvariantCulture);
                        }
                if (disk < 0) return null;
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk")))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            if (Convert.ToString(o["DeviceId"], CultureInfo.InvariantCulture) != disk.ToString(CultureInfo.InvariantCulture)) continue;
                            int mt = Convert.ToInt32(o["MediaType"], CultureInfo.InvariantCulture);
                            if (mt == 3) return true;
                            if (mt == 4) return false;
                        }
            }
            catch { }
            return null;
        }
    }

    /// <summary>Live numbers for the overview page.</summary>
    public sealed class Snapshot
    {
        public int Processes { get; set; }
        public int Services { get; set; }
        public long RamUsed { get; set; }
        public long RamTotal { get; set; }
        public string At { get; set; }

        public static Snapshot Take()
        {
            var s = new Snapshot { At = DateTime.Now.ToString("o", CultureInfo.InvariantCulture) };
            try
            {
                var ps = Process.GetProcesses();
                // "Idle" (pid 0) is not a real process.
                s.Processes = ps.Count(p => p.Id != 0);
                foreach (var p in ps) p.Dispose();
            }
            catch { }
            try
            {
                var svcs = ServiceController.GetServices();
                s.Services = svcs.Count(x => { try { return x.Status == ServiceControllerStatus.Running; } catch { return false; } });
                foreach (var x in svcs) x.Dispose();
            }
            catch { }
            var mem = new Native.MEMORYSTATUSEX();
            if (Native.GlobalMemoryStatusEx(mem))
            {
                s.RamTotal = (long)mem.ullTotalPhys;
                s.RamUsed = (long)(mem.ullTotalPhys - mem.ullAvailPhys);
            }
            return s;
        }
    }
}
