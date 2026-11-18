using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Simplemize
{
    public enum TaskStatus2 { Missing, Enabled, Disabled, Unknown }

    public sealed class ServiceInfo
    {
        public string Name;
        public int Start;      // 2 auto, 3 manual, 4 disabled
        public bool Delayed;

        public string Mode => ModeName(Start, Delayed);

        public static string ModeName(int start, bool delayed)
        {
            switch (start)
            {
                case 0: return "Boot";
                case 1: return "System";
                case 2: return delayed ? "Automatic (delayed)" : "Automatic";
                case 3: return "Manual";
                case 4: return "Disabled";
                default: return "Start=" + start;
            }
        }
    }

    public sealed class RestorePointInfo
    {
        public uint Sequence;
        public string Description;
        public DateTime Created;
    }

    public sealed class RestorePointResult
    {
        public bool Ok;
        public string Message;
        public RestorePointInfo Point;
        public bool TurnedOnSystemRestore;
        /// <summary>Sandbox: nothing was created in Windows.</summary>
        public bool Simulated;
        /// <summary>Things Simplemize had to fix so restore points work (shown to the user and logged).</summary>
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Everything that touches Windows outside the registry: services, scheduled tasks, power plans,
    /// restore points, app packages, files. In sandbox mode nothing is executed; state is simulated.
    /// </summary>
    public sealed class SystemOps
    {
        public readonly bool Sandbox;
        public readonly bool IsAdmin;
        /// <summary>Screenshot mode: simulated names without the sandbox marker.</summary>
        public bool Demo;
        readonly IRegistry reg;
        readonly Log log;

        static readonly string Sys32 = Environment.SystemDirectory;
        static string Exe(string name) => Path.Combine(Sys32, name);
        public static string PowerShellPath => Path.Combine(Sys32, @"WindowsPowerShell\v1.0\powershell.exe");

        // simulated state for sandbox mode
        readonly Dictionary<string, bool> sbTasks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> sbSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> sbDeletedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> sbRemovedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<RestorePointInfo> sbRestorePoints = new List<RestorePointInfo>();
        string sbActiveScheme;
        bool sbOneDriveRemoved;

        public SystemOps(IRegistry reg, Log log, bool sandbox, bool isAdmin)
        {
            this.reg = reg;
            this.log = log;
            Sandbox = sandbox;
            IsAdmin = isAdmin;
        }

        // ---- processes ------------------------------------------------------------------

        public int RunRead(string exe, string args, out string output, int timeoutMs = 60000)
            => Exec(exe, args, out output, timeoutMs);

        public int RunChange(string exe, string args, out string output, int timeoutMs = 180000)
        {
            if (Sandbox)
            {
                log.Info("[sandbox] skipped: " + Path.GetFileName(exe) + " " + args);
                output = "";
                return 0;
            }
            log.Info("run: " + Path.GetFileName(exe) + " " + args);
            int rc = Exec(exe, args, out output, timeoutMs);
            if (rc != 0) log.Warn(Path.GetFileName(exe) + " exited with " + rc + ": " + Squash(output));
            return rc;
        }

        static string Squash(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

        static int Exec(string exe, string args, out string output, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (exe.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
            {
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
            }
            try
            {
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync();
                    var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        output = "timed out after " + (timeoutMs / 1000) + " s";
                        return -1;
                    }
                    p.WaitForExit();
                    output = o.Result + e.Result;
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -2;
            }
        }

        // ---- services -------------------------------------------------------------------

        const string SvcRoot = @"SYSTEM\CurrentControlSet\Services\";

        public ServiceInfo GetService(string name)
        {
            var start = reg.Get(Hive.HKLM, SvcRoot + name, "Start");
            if (start == null || start.Kind != RegistryValueKind.DWord) return null;
            if (reg.Get(Hive.HKLM, SvcRoot + name, "ImagePath") == null) return null;
            var delayed = reg.Get(Hive.HKLM, SvcRoot + name, "DelayedAutostart");
            return new ServiceInfo
            {
                Name = name,
                Start = (int)start.Data,
                Delayed = delayed != null && delayed.Kind == RegistryValueKind.DWord && (int)delayed.Data == 1
            };
        }

        public void SetServiceStart(string name, int start, bool delayed)
        {
            if (Sandbox)
            {
                reg.Set(Hive.HKLM, SvcRoot + name, "Start", RegistryValueKind.DWord, start);
                if (start == 2 && (delayed || reg.Get(Hive.HKLM, SvcRoot + name, "DelayedAutostart") != null)) reg.Set(Hive.HKLM, SvcRoot + name, "DelayedAutostart", RegistryValueKind.DWord, delayed ? 1 : 0);
                log.Info("[sandbox] service " + name + " -> " + ServiceInfo.ModeName(start, delayed));
                return;
            }
            string mode;
            switch (start)
            {
                case 2: mode = delayed ? "delayed-auto" : "auto"; break;
                case 3: mode = "demand"; break;
                case 4: mode = "disabled"; break;
                default: throw new InvalidOperationException("Unsupported start type " + start);
            }
            int rc = RunChange(Exe("sc.exe"), "config \"" + name + "\" start= " + mode, out _);
            if (rc == 5) throw new InvalidOperationException("Windows protects this service and refused the change.");
            if (rc != 0) throw new InvalidOperationException("sc config " + name + " failed (exit " + rc + ").");
        }

        public void StopService(string name)
        {
            if (Sandbox) { log.Info("[sandbox] would stop service " + name); return; }
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped) return;
                    if (!sc.CanStop) { log.Info(name + " can't be stopped right now; it stays off after the next restart."); return; }
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                    log.Info("stopped service " + name);
                }
            }
            catch (Exception ex) { log.Warn("could not stop " + name + " now (" + ex.Message + "); it stays off after a restart."); }
        }

        public void StartService(string name)
        {
            if (Sandbox) { log.Info("[sandbox] would start service " + name); return; }
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending) return;
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                    log.Info("started service " + name);
                }
            }
            catch (Exception ex) { log.Warn("could not start " + name + " now (" + ex.Message + "); it starts with Windows next time."); }
        }

        public bool IsServiceRunning(string name)
        {
            try { using (var sc = new ServiceController(name)) return sc.Status == ServiceControllerStatus.Running; }
            catch { return false; }
        }

        // ---- scheduled tasks ------------------------------------------------------------

        public TaskStatus2 GetTask(string path)
        {
            if (Sandbox && sbTasks.TryGetValue(path, out var en)) return en ? TaskStatus2.Enabled : TaskStatus2.Disabled;
            object svcObj = null;
            try
            {
                var t = Type.GetTypeFromProgID("Schedule.Service");
                svcObj = Activator.CreateInstance(t);
                dynamic svc = svcObj;
                svc.Connect();
                int i = path.LastIndexOf('\\');
                string folder = i <= 0 ? "\\" : path.Substring(0, i);
                string name = path.Substring(i + 1);
                dynamic f = svc.GetFolder(folder);
                dynamic task = f.GetTask(name);
                bool enabled = task.Enabled;
                return enabled ? TaskStatus2.Enabled : TaskStatus2.Disabled;
            }
            catch (Exception ex)
            {
                int hr = ex.HResult;
                if (hr == unchecked((int)0x80070002) || hr == unchecked((int)0x80070003)) return TaskStatus2.Missing;
                return TaskStatus2.Unknown;
            }
            finally
            {
                if (svcObj != null && Marshal.IsComObject(svcObj)) Marshal.ReleaseComObject(svcObj);
            }
        }

        public void SetTask(string path, bool enabled)
        {
            if (Sandbox)
            {
                sbTasks[path] = enabled;
                log.Info("[sandbox] task " + path + " -> " + (enabled ? "enabled" : "disabled"));
                return;
            }
            int rc = RunChange(Exe("schtasks.exe"), "/Change /TN \"" + path + "\" " + (enabled ? "/ENABLE" : "/DISABLE"), out var o);
            if (rc != 0) throw new InvalidOperationException("Task Scheduler refused (" + Squash(o) + ").");
        }

        // ---- power plans ----------------------------------------------------------------

        static readonly Regex GuidRx = new Regex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

        public string GetActiveScheme()
        {
            if (Sandbox && sbActiveScheme != null) return sbActiveScheme;
            RunRead(Exe("powercfg.exe"), "/getactivescheme", out var o);
            var m = GuidRx.Match(o ?? "");
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        public bool SchemeExists(string guid)
        {
            if (Sandbox)
            {
                if (sbDeletedSchemes.Contains(guid)) return false;
                if (sbSchemes.Contains(guid)) return true;
            }
            RunRead(Exe("powercfg.exe"), "/list", out var o);
            return (o ?? "").IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public bool DuplicateScheme(string source, string dest)
        {
            if (Sandbox)
            {
                sbSchemes.Add(dest);
                sbDeletedSchemes.Remove(dest);
                log.Info("[sandbox] duplicated power plan " + source + " -> " + dest);
                return true;
            }
            int rc = RunChange(Exe("powercfg.exe"), "/duplicatescheme " + source + " " + dest, out _);
            return rc == 0 && SchemeExists(dest);
        }

        public void RenameScheme(string guid, string name, string description)
        {
            RunChange(Exe("powercfg.exe"), "/changename " + guid + " \"" + name + "\" \"" + description + "\"", out _);
        }

        public void SetActiveScheme(string guid)
        {
            if (Sandbox)
            {
                sbActiveScheme = guid;
                log.Info("[sandbox] active power plan -> " + guid);
                return;
            }
            int rc = RunChange(Exe("powercfg.exe"), "/setactive " + guid, out var o);
            if (rc != 0) throw new InvalidOperationException("powercfg could not switch plans (" + Squash(o) + ").");
        }

        public void DeleteScheme(string guid)
        {
            if (Sandbox)
            {
                sbSchemes.Remove(guid);
                sbDeletedSchemes.Add(guid);
                log.Info("[sandbox] deleted power plan " + guid);
                return;
            }
            RunChange(Exe("powercfg.exe"), "/delete " + guid, out _);
        }

        // ---- hibernation ----------------------------------------------------------------

        const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";

        static string HiberFile => Path.Combine((Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\", "hiberfil.sys");

        /// <summary>
        /// Is hibernation (and with it Fast Startup) on? The explicit HibernateEnabled value wins; without it
        /// the hibernation file tells the truth. (Missing is NOT "on": VMs and some laptops have it off by default.)
        /// </summary>
        public bool HibernationOn()
        {
            var v = reg.Get(Hive.HKLM, PowerKey, "HibernateEnabled");
            if (v != null && v.Kind == RegistryValueKind.DWord) return (int)v.Data != 0;
            try { return File.Exists(HiberFile); } catch { return false; }
        }

        public void SetHibernation(bool on)
        {
            if (Sandbox)
            {
                // powercfg writes an explicit value. When the real PC has no value and is in the requested
                // state anyway, model "back to how it was" as removing ours.
                bool realOn = File.Exists(HiberFile);
                if (new RealRegistry().Get(Hive.HKLM, PowerKey, "HibernateEnabled") == null && realOn == on)
                    reg.DeleteValue(Hive.HKLM, PowerKey, "HibernateEnabled");
                else
                    reg.Set(Hive.HKLM, PowerKey, "HibernateEnabled", RegistryValueKind.DWord, on ? 1 : 0);
                log.Info("[sandbox] hibernation -> " + (on ? "on" : "off"));
                return;
            }
            int rc = RunChange(Exe("powercfg.exe"), "/hibernate " + (on ? "on" : "off"), out var o);
            if (rc != 0) throw new InvalidOperationException("powercfg /hibernate failed (" + Squash(o) + ").");
        }

        // ---- hardware: power plan values, NVIDIA driver settings, displays -----------------

        readonly Dictionary<string, uint> sbPower = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<uint, uint?> sbNv = new Dictionary<uint, uint?>();
        readonly Dictionary<string, int> sbHz = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public Guid? ActivePowerScheme()
        {
            try { return PowerSettings.ActiveScheme(); } catch { return null; }
        }

        public uint? ReadPowerAC(Guid scheme, Guid sub, Guid setting)
        {
            if (Sandbox && sbPower.TryGetValue(scheme + "|" + sub + "|" + setting, out var v)) return v;
            try { return PowerSettings.ReadAC(scheme, sub, setting); } catch { return null; }
        }

        public void WritePowerAC(Guid scheme, Guid sub, Guid setting, uint value)
        {
            if (Sandbox)
            {
                sbPower[scheme + "|" + sub + "|" + setting] = value;
                log.Info("[sandbox] power setting " + setting + " -> " + value);
                return;
            }
            PowerSettings.WriteAC(scheme, sub, setting, value);
            log.Info("power setting " + setting + " (plugged in) -> " + value + " on plan " + scheme);
        }

        /// <summary>Screenshot mode only: pretend there is (or isn't) an NVIDIA driver.</summary>
        public bool? NvOverride;

        public bool NvAvailable
        {
            get { if (NvOverride.HasValue) return NvOverride.Value; try { return NvDrs.Available; } catch { return false; } }
        }

        public NvDrs.Value NvRead(uint id)
        {
            if (Sandbox && sbNv.TryGetValue(id, out var v))
                return v == null ? new NvDrs.Value { Status = NvDrs.SETTING_NOT_FOUND } : new NvDrs.Value { Status = NvDrs.OK, Current = v.Value, UserSet = true };
            return NvDrs.Read(id);
        }

        public void NvWrite(uint id, uint value)
        {
            if (Sandbox) { sbNv[id] = value; log.Info("[sandbox] NVIDIA setting 0x" + id.ToString("X8") + " -> " + value); return; }
            NvDrs.Write(id, value);
            log.Info("NVIDIA setting 0x" + id.ToString("X8") + " -> " + value);
        }

        public void NvReset(uint id)
        {
            if (Sandbox) { sbNv[id] = null; log.Info("[sandbox] NVIDIA setting 0x" + id.ToString("X8") + " -> driver default"); return; }
            NvDrs.Reset(id);
            log.Info("NVIDIA setting 0x" + id.ToString("X8") + " -> driver default");
        }

        public List<DisplayInfo> ListDisplays()
        {
            List<DisplayInfo> list;
            try { list = Displays.List(); } catch { return new List<DisplayInfo>(); }
            if (Sandbox)
                foreach (var d in list)
                    if (sbHz.TryGetValue(d.Device, out var hz)) d.Hz = hz;
            return list;
        }

        /// <summary>0 on success, otherwise the Windows DISP_CHANGE code.</summary>
        public int SetRefresh(string device, int hz)
        {
            if (Sandbox)
            {
                sbHz[device] = hz;
                log.Info("[sandbox] " + device + " -> " + hz + " Hz");
                return 0;
            }
            int rc = Displays.SetRefresh(device, hz, false);
            log.Info(device + " -> " + hz + " Hz: " + (rc == 0 ? "ok" : "refused (" + rc + ")"));
            return rc;
        }

        // ---- restore points -------------------------------------------------------------

        static ManagementScope SrScope() => new ManagementScope(@"\\.\root\default");

        public List<RestorePointInfo> ListRestorePoints()
        {
            var list = new List<RestorePointInfo>();
            if (IsAdmin)
            {
                try
                {
                    using (var s = new ManagementObjectSearcher(SrScope(), new ObjectQuery("SELECT * FROM SystemRestore")))
                        foreach (ManagementObject o in s.Get())
                            using (o)
                                list.Add(new RestorePointInfo
                                {
                                    Sequence = Convert.ToUInt32(o["SequenceNumber"]),
                                    Description = Convert.ToString(o["Description"]),
                                    Created = ManagementDateTimeConverter.ToDateTime(Convert.ToString(o["CreationTime"]))
                                });
                }
                catch (Exception ex) { log.Warn("could not list restore points: " + ex.Message); }
            }
            if (Sandbox) list.AddRange(sbRestorePoints);
            return list.OrderByDescending(r => r.Created).ToList();
        }

        public RestorePointResult CreateRestorePoint(string description)
        {
            var res = new RestorePointResult();
            if (Sandbox)
            {
                Thread.Sleep(900);
                var fake = new RestorePointInfo { Sequence = (uint)(900 + sbRestorePoints.Count), Description = Demo ? description : description + " (sandbox)", Created = DateTime.Now };
                sbRestorePoints.Add(fake);
                log.Info("[sandbox] pretended to create restore point \"" + description + "\" (nothing was created in Windows)");
                res.Ok = true;
                res.Simulated = !Demo;
                res.Point = fake;
                return res;
            }

            string drive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + "\\";

            // 1. A policy (from an organisation or another tuning tool) can switch System Restore off. We don't override policies.
            const string policy = @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";
            if (reg.Get(Hive.HKLM, policy, "DisableSR")?.Data is int dsr && dsr == 1)
            {
                res.Message = "System Restore is switched off by a policy on this PC (\"Turn off System Restore\", set by an organisation or another tuning tool). " +
                              "Simplemize doesn't override policies. To allow restore points, delete the value DisableSR in HKLM\\" + policy + ", or set that policy to Not configured in gpedit.msc.";
                log.Error("restore point blocked by policy DisableSR=1");
                return res;
            }

            // 2. Restore points need the Volume Shadow Copy services. Other "optimizers" often disable them.
            foreach (var svc in new[] { "VSS", "swprv" })
            {
                var s = GetService(svc);
                if (s != null && s.Start == 4)
                {
                    try
                    {
                        SetServiceStart(svc, 3, false);
                        res.Notes.Add("The " + (svc == "VSS" ? "Volume Shadow Copy" : "Microsoft Software Shadow Copy Provider") + " service was disabled. Simplemize set it back to Manual (the Windows default), because restore points can't be made without it.");
                    }
                    catch (Exception ex) { log.Warn("could not re-enable " + svc + ": " + ex.Message); }
                }
            }

            const string srKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
            const string freqName = "SystemRestorePointCreationFrequency";
            var oldFreq = reg.Get(Hive.HKLM, srKey, freqName);
            try
            {
                // 3. Windows silently skips restore points if one was made in the last 24 h. Lift that limit for this one call.
                reg.Set(Hive.HKLM, srKey, freqName, RegistryValueKind.DWord, 0);
                uint before = ListRestorePoints().Select(p => p.Sequence).DefaultIfEmpty(0u).Max();

                uint rc = InvokeCreate(description);
                log.Info("CreateRestorePoint returned " + Code(rc));
                if (rc != 0)
                {
                    // 4. Most often System Restore is simply off for the system drive (or has no space). Fix that and try again.
                    log.Info("trying to turn on System Restore for " + drive);
                    if (InvokeEnable(drive) == 0)
                    {
                        res.TurnedOnSystemRestore = true;
                        log.Info("System Restore is on for " + drive);
                    }
                    var grew = EnsureShadowStorage(drive);
                    if (grew != null) res.Notes.Add(grew);
                    Thread.Sleep(1500);
                    rc = InvokeCreate(description);
                    log.Info("second attempt returned " + Code(rc));
                }
                if (rc != 0)
                {
                    res.Message = Explain(rc);
                    return res;
                }

                // 5. Windows can take a moment to list it.
                RestorePointInfo made = null;
                for (int i = 0; i < 6 && made == null; i++)
                {
                    if (i > 0) Thread.Sleep(1500);
                    made = ListRestorePoints().Where(p => p.Sequence > before).OrderByDescending(p => p.Sequence).FirstOrDefault();
                }
                if (made == null)
                {
                    res.Message = "Windows said the restore point was created, but it doesn't show up in the list. " +
                                  "This usually means System Restore is limited by a policy or has no disk space reserved. " +
                                  "Open System Properties › System Protection, select your system drive, click Configure and make sure protection is on with some space reserved.";
                    return res;
                }
                res.Ok = true;
                res.Point = made;
                log.Info("restore point created: #" + made.Sequence + " \"" + made.Description + "\"");
                return res;
            }
            catch (Exception ex)
            {
                res.Message = ex.Message;
                return res;
            }
            finally
            {
                try
                {
                    if (oldFreq == null) reg.DeleteValue(Hive.HKLM, srKey, freqName);
                    else reg.Set(Hive.HKLM, srKey, freqName, oldFreq.Kind, oldFreq.Data);
                }
                catch { }
            }
        }

        static string Code(uint rc) => rc + " (0x" + rc.ToString("X8") + ")";

        /// <summary>Turns a CreateRestorePoint result into something a person can act on.</summary>
        static string Explain(uint rc)
        {
            switch (rc)
            {
                case 1058:
                case 0x80070422:
                    return "Windows couldn't make a restore point because a service it needs is disabled (code " + Code(rc) + "). " +
                           "Open services.msc and set \"Volume Shadow Copy\" and \"Microsoft Software Shadow Copy Provider\" to Manual.";
                case 112:
                case 0x80070070:
                    return "There isn't enough free space on your system drive for a restore point (code " + Code(rc) + "). Free some space and try again.";
                case 1460:
                case 0x800705B4:
                    return "Windows took too long to make the restore point (code " + Code(rc) + "). Try again in a minute.";
                default:
                    if ((rc & 0xFFFF0000) == 0x80040000)
                        return "The Volume Shadow Copy service reported a problem (code " + Code(rc) + "). " +
                               "Restarting the PC usually clears it. If it keeps happening, another backup or optimizer tool may be holding it.";
                    return "Windows refused to create a restore point (code " + Code(rc) + "). " +
                           "Check System Properties › System Protection: your system drive should have protection turned on with some disk space reserved.";
            }
        }

        /// <summary>
        /// If System Restore has (almost) no disk space reserved on the system drive, give it 5 %. Never shrinks
        /// an existing reservation (shrinking would delete older restore points). Returns a note, or null.
        /// </summary>
        string EnsureShadowStorage(string drive)
        {
            try
            {
                string letter = drive.TrimEnd('\\');
                string volumeId = null;
                long size = 0;
                using (var s = new ManagementObjectSearcher("SELECT DeviceID, Capacity FROM Win32_Volume WHERE DriveLetter = '" + letter + "'"))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            volumeId = Convert.ToString(o["DeviceID"]);
                            size = Convert.ToInt64(o["Capacity"]);
                        }
                if (volumeId == null) return null;
                var guid = Regex.Match(volumeId, @"Volume\{[0-9a-fA-F-]+\}").Value;
                ulong max = 0;
                bool found = false;
                using (var s = new ManagementObjectSearcher("SELECT Volume, MaxSpace FROM Win32_ShadowStorage"))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            if (guid.Length == 0 || Convert.ToString(o["Volume"]).IndexOf(guid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            found = true;
                            max = Convert.ToUInt64(o["MaxSpace"]);
                        }
                const ulong oneGb = 1UL << 30;
                if (!found || max >= oneGb || size <= 0) return null;
                if ((ulong)(size / 20) <= max) return null;   // 5 % would not be more than now
                int rc = RunChange(Exe("vssadmin.exe"), "Resize ShadowStorage /For=" + letter + " /On=" + letter + " /MaxSize=5%", out _);
                return rc == 0 ? "System Restore had almost no disk space reserved on " + letter + ". Simplemize reserved 5% so restore points fit." : null;
            }
            catch (Exception ex)
            {
                log.Warn("could not check System Restore disk space: " + ex.Message);
                return null;
            }
        }

        static uint InvokeCreate(string description)
        {
            using (var mc = new ManagementClass(SrScope(), new ManagementPath("SystemRestore"), null))
            using (var inp = mc.GetMethodParameters("CreateRestorePoint"))
            {
                inp["Description"] = description;
                inp["RestorePointType"] = 12u;   // MODIFY_SETTINGS
                inp["EventType"] = 100u;         // BEGIN_SYSTEM_CHANGE
                using (var outp = mc.InvokeMethod("CreateRestorePoint", inp, null))
                    return Convert.ToUInt32(outp["ReturnValue"]);
            }
        }

        static uint InvokeEnable(string drive)
        {
            try
            {
                using (var mc = new ManagementClass(SrScope(), new ManagementPath("SystemRestore"), null))
                using (var inp = mc.GetMethodParameters("Enable"))
                {
                    inp["Drive"] = drive;
                    using (var outp = mc.InvokeMethod("Enable", inp, null))
                        return Convert.ToUInt32(outp["ReturnValue"]);
                }
            }
            catch { return 1; }
        }

        // ---- app packages ---------------------------------------------------------------

        static readonly Regex SafePattern = new Regex(@"^[A-Za-z0-9._*\-]+$");

        /// <summary>Names of installed, removable app packages.</summary>
        public HashSet<string> ListApps()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string ps = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-AppxPackage " + (IsAdmin ? "-AllUsers " : "") +
                        "| Where-Object { -not $_.IsFramework -and -not $_.NonRemovable } | Select-Object -ExpandProperty Name -Unique | ConvertTo-Json -Compress";
            int rc = RunRead(PowerShellPath, "-NoProfile -NonInteractive -Command \"" + ps + "\"", out var o, 90000);
            if (rc != 0 || string.IsNullOrWhiteSpace(o)) { log.Warn("could not list installed apps"); return set; }
            try
            {
                var parsed = new JavaScriptSerializer().DeserializeObject(o.Trim());
                if (parsed is string one) set.Add(one);
                else if (parsed is object[] arr) foreach (var a in arr) if (a is string s) set.Add(s);
            }
            catch (Exception ex) { log.Warn("could not read app list: " + ex.Message); }
            if (Sandbox) set.RemoveWhere(n => sbRemovedApps.Any(p => Wild(p, n)));
            return set;
        }

        public static bool Wild(string pattern, string name)
        {
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase);
        }

        public bool RemoveApp(string pattern, out string detail)
        {
            if (!SafePattern.IsMatch(pattern)) throw new ArgumentException("bad package name");
            if (Sandbox)
            {
                sbRemovedApps.Add(pattern);
                log.Info("[sandbox] would remove app " + pattern);
                detail = "";
                return true;
            }
            string ps =
                "$ErrorActionPreference='SilentlyContinue'; $n='" + pattern + "'; " +
                "Get-AppxPackage -AllUsers -Name $n | ForEach-Object { try { Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction Stop } catch { Remove-AppxPackage -Package $_.PackageFullName } }; " +
                "Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -like $n } | ForEach-Object { Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName | Out-Null }; " +
                "if (Get-AppxPackage -Name $n) { exit 1 } else { exit 0 }";
            int rc = RunChange(PowerShellPath, "-NoProfile -NonInteractive -Command \"" + ps + "\"", out detail, 600000);
            return rc == 0;
        }

        // ---- OneDrive -------------------------------------------------------------------

        public string OneDriveUninstallCommand()
        {
            if (Sandbox && sbOneDriveRemoved) return null;
            foreach (var hive in new[] { Hive.HKCU, Hive.HKLM })
            {
                var v = reg.Get(hive, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\OneDriveSetup.exe", "UninstallString");
                if (v != null && !string.IsNullOrWhiteSpace(v.Data as string)) return Environment.ExpandEnvironmentVariables((string)v.Data);
            }
            return null;
        }

        public bool UninstallOneDrive(out string detail)
        {
            var cmd = OneDriveUninstallCommand();
            detail = "";
            if (cmd == null) return true;
            if (Sandbox)
            {
                sbOneDriveRemoved = true;
                log.Info("[sandbox] would run: " + cmd);
                return true;
            }
            foreach (var p in Process.GetProcessesByName("OneDrive"))
                try { p.Kill(); p.WaitForExit(5000); } catch { }

            // UninstallString looks like: "C:\...\OneDriveSetup.exe"  /uninstall [/allusers]
            string exe, args;
            var m = Regex.Match(cmd, "^\\s*\"([^\"]+)\"\\s*(.*)$");
            if (m.Success) { exe = m.Groups[1].Value; args = m.Groups[2].Value; }
            else
            {
                int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (i < 0) { detail = "unexpected uninstall command"; return false; }
                exe = cmd.Substring(0, i + 4);
                args = cmd.Substring(i + 4);
            }
            if (args.IndexOf("/uninstall", StringComparison.OrdinalIgnoreCase) < 0) args += " /uninstall";
            int rc = RunChange(exe, args.Trim(), out detail, 300000);
            Thread.Sleep(1500);
            return rc == 0;
        }

        // ---- files ----------------------------------------------------------------------

        /// <summary>Size of files under <paramref name="dir"/> older than <paramref name="minAge"/>. Never follows links.</summary>
        public static long Measure(string dir, TimeSpan minAge) => Walk(dir, DateTime.Now - minAge, false);

        // Cleanup only ever runs inside these folders. Anything else (a drive root, a profile folder,
        // a mis-set TEMP variable) is refused, no matter where the path came from.
        static readonly string[] CleanableEndings =
        {
            @"\Temp",
            @"\SoftwareDistribution\Download",
            @"\WER\ReportArchive",
            @"\WER\ReportQueue",
            @"\CrashDumps",
            @"\DeliveryOptimization\Cache",
            @"\SoftwareDistribution\DeliveryOptimization",
        };

        public static bool IsCleanable(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            // Only absolute local paths: a relative path would resolve against the working folder,
            // and a network path isn't ours to clean.
            if (!Path.IsPathRooted(dir) || dir.StartsWith(@"\\", StringComparison.Ordinal) || dir.Length < 3 || dir[1] != ':') return false;
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\'); }
            catch { return false; }
            var root = Path.GetPathRoot(full)?.TrimEnd('\\') ?? "";
            if (full.Length <= root.Length + 1) return false;                       // a drive root
            if (full.Split('\\').Length < 3) return false;                           // C:\Something is too shallow
            if (!CleanableEndings.Any(e => full.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return false;
            // Refuse if the folder or any folder above it is a junction or symlink: the files would
            // really live somewhere else.
            try
            {
                for (var d = new DirectoryInfo(full); d != null && d.FullName.TrimEnd('\\').Length > root.Length; d = d.Parent)
                    if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch { return false; }
            return true;
        }

        public long Clean(string dir, TimeSpan minAge)
        {
            if (!IsCleanable(dir))
            {
                log.Error("refused to clean unexpected folder: " + dir);
                throw new InvalidOperationException("Refused to clean \"" + dir + "\": it isn't one of the folders Simplemize is allowed to clean.");
            }
            if (Sandbox)
            {
                long size = Measure(dir, minAge);
                log.Info("[sandbox] would delete " + Fmt.Bytes(size) + " from " + dir);
                return size;
            }
            long freed = Walk(dir, DateTime.Now - minAge, true);
            log.Info("cleaned " + Fmt.Bytes(freed) + " from " + dir);
            return freed;
        }

        static long Walk(string path, DateTime cutoff, bool delete)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            DirectoryInfo dir;
            try
            {
                dir = new DirectoryInfo(path);
                if (!dir.Exists) return 0;
            }
            catch { return 0; }
            return WalkDir(dir, cutoff, delete);
        }

        static long WalkDir(DirectoryInfo dir, DateTime cutoff, bool delete)
        {
            long total = 0;
            FileSystemInfo[] entries;
            try { entries = dir.GetFileSystemInfos(); }
            catch { return 0; }
            foreach (var e in entries)
            {
                try
                {
                    // Never follow junctions or symlinks out of the folder being cleaned.
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (e is DirectoryInfo d)
                    {
                        total += WalkDir(d, cutoff, delete);
                        if (delete && d.CreationTime < cutoff)
                            try { d.Delete(false); } catch { /* not empty or in use */ }
                    }
                    else if (e is FileInfo f)
                    {
                        // Skip anything touched recently by either clock: extracted installer files keep an old
                        // "modified" date but get a fresh "created" date, and must survive.
                        if (f.LastWriteTime > cutoff || f.CreationTime > cutoff) continue;
                        long len = f.Length;
                        if (delete)
                        {
                            if (f.IsReadOnly) f.IsReadOnly = false;
                            f.Delete();
                        }
                        total += len;
                    }
                }
                catch { /* in use or protected: leave it */ }
            }
            return total;
        }

        // ---- recycle bin ----------------------------------------------------------------

        public static long RecycleBinSize()
        {
            if (!Environment.Is64BitProcess) return -1;
            var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
            return Native.SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : -1;
        }

        public void EmptyRecycleBin()
        {
            if (Sandbox) { log.Info("[sandbox] would empty the Recycle Bin"); return; }
            Native.SHEmptyRecycleBin(IntPtr.Zero, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
            log.Info("emptied the Recycle Bin");
        }

        // ---- misc -----------------------------------------------------------------------

        public void BroadcastSettingsChange()
        {
            if (Sandbox) return;
            foreach (var area in new[] { "Policy", "WindowsThemeElement", "TraySettings", null })
                Native.SendMessageTimeout(Native.HWND_BROADCAST, Native.WM_SETTINGCHANGE, IntPtr.Zero, area, Native.SMTO_ABORTIFHUNG, 1500, out _);
        }

        /// <summary>Applies the mouse and menu values from the registry to the running session (no sign-out needed).</summary>
        public void RefreshMouseAndMenus()
        {
            if (Sandbox) return;
            try
            {
                int Read(string path, string name, int fallback)
                {
                    var v = reg.Get(Hive.HKCU, path, name);
                    return v != null && int.TryParse(Convert.ToString(v.Data), out var n) ? n : fallback;
                }
                var mouse = new[]
                {
                    Read(@"Control Panel\Mouse", "MouseThreshold1", 6),
                    Read(@"Control Panel\Mouse", "MouseThreshold2", 10),
                    Read(@"Control Panel\Mouse", "MouseSpeed", 1)
                };
                Native.SystemParametersInfo(Native.SPI_SETMOUSE, 0, mouse, Native.SPIF_SENDCHANGE);
                Native.SystemParametersInfo(Native.SPI_SETMENUSHOWDELAY, (uint)Read(@"Control Panel\Desktop", "MenuShowDelay", 400), IntPtr.Zero, Native.SPIF_SENDCHANGE);
            }
            catch (Exception ex) { log.Warn("could not refresh mouse settings: " + ex.Message); }
        }

        public void RestartExplorer()
        {
            if (Sandbox) { log.Info("[sandbox] would restart Explorer"); return; }
            foreach (var p in Process.GetProcessesByName("explorer"))
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            // Windows restarts the shell by itself (AutoRestartShell). Only start it if it did not come back.
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(250);
                if (Process.GetProcessesByName("explorer").Length > 0) return;
            }
            try { Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")); } catch { }
        }
    }

    public static class Fmt
    {
        public static string Bytes(long b)
        {
            if (b < 0) return "–";
            if (b < 1024) return b + " B";
            double v = b;
            string[] u = { "KB", "MB", "GB", "TB" };
            int i = -1;
            do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
            return v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", System.Globalization.CultureInfo.InvariantCulture) + " " + u[i];
        }
    }
}
