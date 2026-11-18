using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace Simplemize
{
    /// <summary>
    /// --selftest: applies and undoes every tweak against the sandbox registry and simulated system.
    /// Pass 1 runs against this PC's current state. Pass 2 first simulates a fresh Windows install
    /// (every value reset, services back to Automatic, tasks enabled) so every tweak goes through a real
    /// Off -> Active -> Off transition. Checks: each tweak is Active after applying, undo restores the exact
    /// previous state, the backup file survives a reload, no leftovers, and the real registry is never written.
    /// </summary>
    public static class SelfTest
    {
        public static int Run(Env env, Engine engine, IList<Tweak> catalog, string outFile)
        {
            var sb = new StringBuilder();
            void Line(string s) => sb.AppendLine(s);

            Line("Simplemize self-test  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Line(env.Info.OsName + " " + env.Info.OsRelease + " build " + env.Info.Build + "  admin=" + env.IsAdmin + "  sandbox=" + env.Sandbox);
            if (!env.Sandbox || !(env.Reg is SandboxRegistry sandbox))
            {
                Line("REFUSING: self-test must run in the sandbox.");
                File.WriteAllText(outFile, sb.ToString());
                return 99;
            }

            env.Apps = env.Sys.ListApps();
            Line("installed removable apps found: " + env.Apps.Count);

            var real = new RealRegistry();
            var regActions = catalog.SelectMany(t => t.Actions.OfType<RegSet>()).ToList();
            var realBefore = regActions.Select(a => real.Get(a.Hive, a.Path, a.Name)?.Encoded).ToList();

            int fails = 0, passes = 0, skips = 0;

            // ---------------- pass 1: this PC as it is ----------------
            Line("");
            Line("================ PASS 1: this PC as it is ================");
            var baseline = sandbox.Describe().OrderBy(x => x).ToList();
            Phases(env, engine, catalog, Line, ref fails, ref passes, ref skips);
            Compare(sandbox, baseline, Line, ref fails);

            // ---------------- pass 2: simulated fresh Windows ----------------
            Line("");
            Line("================ PASS 2: simulated fresh Windows ================");
            int seeded = SandboxTools.SeedFresh(env, catalog, sandbox);
            Line("reset " + seeded + " values to their Windows defaults (inside the sandbox)");
            int stillActive = catalog.Count(t => t.Revertible && t.Unavailable?.Invoke(env) == null && t.Evaluate(env) == TState.Active);
            Line("tweaks still active after reset (value missing = already the default): " + stillActive);
            var fresh = sandbox.Describe().OrderBy(x => x).ToList();
            Phases(env, engine, catalog, Line, ref fails, ref passes, ref skips);
            Compare(sandbox, fresh, Line, ref fails);

            // ---------------- scenarios from the code review ----------------
            Line("");
            Line("================ SCENARIOS ================");
            Scenarios(env, engine, catalog, sandbox, Line, ref fails, ref passes);

            // ---------------- safety guards and read-only probes ----------------
            Line("");
            Line("================ GUARDS & PROBES ================");
            var guardCases = new[]
            {
                (Path.GetTempPath(), true), (Environment.ExpandEnvironmentVariables(@"%WINDIR%\Temp"), true),
                (Environment.ExpandEnvironmentVariables(@"%WINDIR%\SoftwareDistribution\Download"), true),
                (@"C:\", false), (@"C:\Temp", false), (@"C:\Windows", false), (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), false),
                (@"C:\Users\Someone\Documents", false), (@"C:\Program Files", false), ("", false),
            };
            foreach (var (dir, expect) in guardCases)
            {
                bool got = SystemOps.IsCleanable(dir);
                if (got == expect) { passes++; Line("  PASS  cleanable(" + (dir == "" ? "<empty>" : dir) + ") = " + got); }
                else { fails++; Line("  FAIL  cleanable(" + dir + ") = " + got + ", expected " + expect); }
            }
            try { env.Sys.Clean(@"C:\", TimeSpan.Zero); fails++; Line("  FAIL  Clean(C:\\) was not refused"); }
            catch (InvalidOperationException) { passes++; Line("  PASS  Clean(C:\\) refused"); }

            var scheme = new SystemOps(new RealRegistry(), env.Log, true, env.IsAdmin).GetActiveScheme();
            Line((scheme != null ? "  PASS" : "  FAIL") + "  active power plan readable: " + (scheme ?? "null"));
            if (scheme == null) fails++; else passes++;
            var svc = env.Sys.GetService("Dnscache");
            Line((svc != null ? "  PASS" : "  FAIL") + "  service config readable (Dnscache: " + (svc?.Mode ?? "null") + ")");
            if (svc == null) fails++; else passes++;
            var task = env.Sys.GetTask(@"\Microsoft\Windows\Autochk\Proxy");
            Line("  INFO  task state readable without admin: " + task);
            Line("  INFO  restore points visible: " + env.Sys.ListRestorePoints().Count + (env.IsAdmin ? "" : " (needs admin)"));

            // ---------------- the real registry ----------------
            Line("");
            var realAfter = regActions.Select(a => real.Get(a.Hive, a.Path, a.Name)?.Encoded).ToList();
            int changedReal = realBefore.Where((v, i) => v != realAfter[i]).Count();
            if (changedReal == 0) Line("PASS  real registry untouched (" + regActions.Count + " values checked)");
            else { fails++; Line("FAIL  " + changedReal + " real registry values differ!"); }

            Line("");
            Line("RESULT: " + passes + " passed, " + fails + " failed, " + skips + " skipped (not available on this PC)");
            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
            return fails == 0 ? 0 : 1;
        }

        static void Scenarios(Env env, Engine engine, IList<Tweak> catalog, SandboxRegistry sb, Action<string> Line, ref int fails, ref int passes)
        {
            int f = 0, p = 0;
            void Check(bool ok, string what) { if (ok) { p++; Line("  PASS  " + what); } else { f++; Line("  FAIL  " + what); } }
            Tweak T(string id) => catalog.First(t => t.Id == id);
            string Str(Hive h, string path, string name) => sb.Get(h, path, name)?.Data?.ToString();

            // 1. cleanup age rules, on a real scratch folder this test creates and owns
            var root = Path.Combine(Path.GetTempPath(), "simplemize-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var dir = Path.Combine(root, "Temp");
            try
            {
                Directory.CreateDirectory(dir);
                var old = Path.Combine(dir, "old.bin"); File.WriteAllBytes(old, new byte[100]);
                File.SetCreationTime(old, DateTime.Now.AddDays(-3)); File.SetLastWriteTime(old, DateTime.Now.AddDays(-3));
                var copied = Path.Combine(dir, "extracted.msi"); File.WriteAllBytes(copied, new byte[200]);
                File.SetLastWriteTime(copied, DateTime.Now.AddDays(-30));   // old "modified", fresh "created"
                var fresh = Path.Combine(dir, "fresh.tmp"); File.WriteAllBytes(fresh, new byte[300]);
                Check(SystemOps.Measure(dir, TimeSpan.FromHours(24)) == 100, "cleanup counts only files old by both dates (100 bytes)");
                var realOps = new SystemOps(new RealRegistry(), env.Log, false, env.IsAdmin);
                long freed = realOps.Clean(dir, TimeSpan.FromHours(24));
                Check(freed == 100 && !File.Exists(old) && File.Exists(copied) && File.Exists(fresh), "real cleanup deletes the old file, keeps the freshly extracted and the new one");

                var link = Path.Combine(root, "junction", "Temp");
                Directory.CreateDirectory(Path.GetDirectoryName(link));
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + dir + "\"") { UseShellExecute = false, CreateNoWindow = true };
                using (var pr = System.Diagnostics.Process.Start(psi)) pr.WaitForExit(10000);
                if (Directory.Exists(link)) Check(!SystemOps.IsCleanable(link), "a Temp folder that is a junction is refused");
                Check(!SystemOps.IsCleanable("Temp") && !SystemOps.IsCleanable(@"..\Temp"), "relative paths are refused");
                Check(!SystemOps.IsCleanable(@"\\server\share\Temp"), "network paths are refused");
                try { Directory.Delete(link); } catch { }
            }
            catch (Exception ex) { Check(false, "cleanup scenario crashed: " + ex.Message); }
            finally { try { Directory.Delete(root, true); } catch { } }

            // 2. a planted backup entry that writes somewhere the tweak never touches is refused
            const string ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe";
            engine.Backup.Entries.Add(new BackupEntry
            {
                TweakId = "game.dvr", Title = "planted", AppliedAt = "x",
                Changes = { new Change { Type = "reg", Hive = "HKLM", Path = ifeo, Name = "Debugger", Existed = true, Kind = "String", Value = "cmd.exe" } }
            });
            engine.Backup.Entries.Add(new BackupEntry
            {
                TweakId = "no.such.tweak", Title = "planted 2", AppliedAt = "x",
                Changes = { new Change { Type = "svc", Name = "WinDefend", OldStart = 4 } }
            });
            var u = engine.Undo(new[] { "game.dvr", "no.such.tweak" });
            Check(sb.Get(Hive.HKLM, ifeo, "Debugger") == null, "planted registry entry was not written");
            Check(env.Sys.GetService("WinDefend") == null || env.Sys.GetService("WinDefend").Start != 4, "planted service entry was not applied");
            Check(u.Failed == 2, "both planted entries reported as refused");
            engine.Backup.Entries.RemoveAll(e => e.Title.StartsWith("planted"));
            engine.TrySave(out _);

            // 3. another account's per-user value is left for that account
            engine.Backup.Entries.Add(new BackupEntry
            {
                TweakId = "game.mode", Title = "other user", AppliedAt = "x",
                Changes = { new Change { Type = "reg", Hive = "HKCU", Path = @"Software\Microsoft\GameBar", Name = "AutoGameModeEnabled", Existed = true, Kind = "DWord", Value = "0", User = "S-1-5-21-1-2-3-1001" } }
            });
            Check(!engine.HasUndoFor("game.mode"), "another account's entry isn't offered for undo");
            var before = Str(Hive.HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
            engine.Undo(new[] { "game.mode" });
            Check(Str(Hive.HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") == before && engine.EntryFor("game.mode") != null, "another account's value is kept, not written here");
            engine.Backup.Entries.RemoveAll(e => e.Title == "other user");
            engine.TrySave(out _);

            // 4. Sticky Keys: only the shortcut bit changes; undo puts back only that bit
            const string sk = @"Control Panel\Accessibility\StickyKeys";
            var skBefore = sb.Get(Hive.HKCU, sk, "Flags");
            sb.Set(Hive.HKCU, sk, "Flags", RegistryValueKind.String, "511");      // Sticky Keys itself switched ON
            engine.Apply(new[] { T("input.stickykeys") }, s => { }, m => true);
            Check(Str(Hive.HKCU, sk, "Flags") == "507", "Sticky Keys stays on, only the shortcut bit is cleared (511 -> 507)");
            sb.Set(Hive.HKCU, sk, "Flags", RegistryValueKind.String, "491");      // the user changes another option afterwards
            engine.Undo(new[] { "input.stickykeys" });
            Check(Str(Hive.HKCU, sk, "Flags") == "495", "undo restores only the shortcut bit and keeps the later change (491 -> 495)");
            if (skBefore == null) sb.DeleteValue(Hive.HKCU, sk, "Flags"); else sb.Set(Hive.HKCU, sk, "Flags", skBefore.Kind, skBefore.Data);

            // 5. DirectX pair: undo removes only our pair and keeps settings added later
            const string dx = @"Software\Microsoft\DirectX\UserGpuPreferences";
            var dxBefore = sb.Get(Hive.HKCU, dx, "DirectXUserGlobalSettings");
            if (env.Info.IsWin11)
            {
                sb.Set(Hive.HKCU, dx, "DirectXUserGlobalSettings", RegistryValueKind.String, "VRROptimizeEnable=0;");
                engine.Apply(new[] { T("gpu.windowed") }, s => { }, m => true);
                var mid = Str(Hive.HKCU, dx, "DirectXUserGlobalSettings") ?? "";
                Check(mid.Contains("VRROptimizeEnable=0;") && mid.Contains("SwapEffectUpgradeEnable=1;"), "apply keeps the existing graphics settings");
                sb.Set(Hive.HKCU, dx, "DirectXUserGlobalSettings", RegistryValueKind.String, mid + "AutoHDREnable=1;");
                engine.Undo(new[] { "gpu.windowed" });
                var after = Str(Hive.HKCU, dx, "DirectXUserGlobalSettings") ?? "";
                Check(after.Contains("AutoHDREnable=1;") && after.Contains("VRROptimizeEnable=0;") && !after.Contains("SwapEffectUpgradeEnable"), "undo removes only its own pair (\"" + after + "\")");
                if (dxBefore == null) sb.DeleteValue(Hive.HKCU, dx, "DirectXUserGlobalSettings"); else sb.Set(Hive.HKCU, dx, "DirectXUserGlobalSettings", dxBefore.Kind, dxBefore.Data);
            }

            // 6. a service the user disabled is never raised to Manual
            var eu = env.Sys.GetService("edgeupdate") ?? env.Sys.GetService("MapsBroker");
            if (eu != null)
            {
                env.Sys.SetServiceStart(eu.Name, 4, false);
                engine.Apply(new[] { T("svc.manual") }, s => { }, m => true);
                Check(env.Sys.GetService(eu.Name).Start == 4, eu.Name + " stays Disabled after \"services to Manual\"");
                engine.Undo(new[] { "svc.manual" });
                env.Sys.SetServiceStart(eu.Name, eu.Start, eu.Delayed);
            }

            // 7. two tweaks sharing a key that didn't exist: whichever is undone last removes it (both orders)
            const string sharedRoot = @"Software\Simplemize-SelfTest";
            const string shared = sharedRoot + @"\Shared";
            foreach (var order in new[] { new[] { "test.a", "test.b" }, new[] { "test.b", "test.a" } })
                foreach (bool oneCall in new[] { false, true })
                {
                    var tA = new Tweak { Id = "test.a", Title = "test a", Section = Section.Extras, Group = "t", Actions = { new RegSet(Hive.HKCU, shared, "A", RegistryValueKind.DWord, 1) } };
                    var tB = new Tweak { Id = "test.b", Title = "test b", Section = Section.Extras, Group = "t", Actions = { new RegSet(Hive.HKCU, shared, "B", RegistryValueKind.DWord, 1) } };
                    var envT = new Env { Reg = env.Reg, Sys = env.Sys, Log = env.Log, Info = env.Info, Sandbox = true, IsAdmin = env.IsAdmin, UserSid = env.UserSid,
                                         DataDir = Path.Combine(Path.GetTempPath(), "simplemize-shared-" + Guid.NewGuid().ToString("N").Substring(0, 8)) };
                    var engT = new Engine(envT, new List<Tweak> { tA, tB });
                    engT.Apply(new[] { tA }, s => { }, m => true);
                    engT.Apply(new[] { tB }, s => { }, m => true);
                    if (oneCall) engT.Undo(order);
                    else { engT.Undo(new[] { order[0] }); new Engine(envT, new List<Tweak> { tA, tB }).Undo(new[] { order[1] }); }
                    Check(!sb.KeyExists(Hive.HKCU, sharedRoot), "shared new key removed after undoing " + string.Join(" then ", order) + (oneCall ? " together" : " separately"));
                    try { Directory.Delete(envT.DataDir, true); } catch { }
                }

            // 8. install, reinstall and uninstall, into a scratch folder (never the real Start menu)
            var iroot = Path.Combine(Path.GetTempPath(), "simplemize-install-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var ip = new Installer.Paths
            {
                InstallDir = Path.Combine(iroot, "Programs", "Simplemize"),
                StartMenuLink = Path.Combine(iroot, "Start", "Simplemize.lnk"),
                DesktopLink = Path.Combine(iroot, "Desktop", "Simplemize.lnk"),
                UninstallKey = null,
            };
            ip.Exe = Path.Combine(ip.InstallDir, "Simplemize.exe");
            try
            {
                Installer.Install(ip, true, env.Log);
                Check(File.Exists(ip.Exe) && new FileInfo(ip.Exe).Length == new FileInfo(Installer.RunningExe).Length, "install copies the program");
                Check(string.Equals(Installer.ShortcutTarget(ip.StartMenuLink), ip.Exe, StringComparison.OrdinalIgnoreCase), "Start menu shortcut points at the installed copy");
                Check(File.Exists(ip.DesktopLink), "desktop shortcut is added when asked");
                Check(Installer.IsInstalled(ip) && Installer.InstalledVersion(ip) == Installer.RunningVersion, "installed version is detected (" + Installer.InstalledVersion(ip) + ")");
                Installer.Install(ip, false, env.Log);
                Check(!File.Exists(ip.DesktopLink) && File.Exists(ip.StartMenuLink), "reinstalling without the desktop option removes only that shortcut");
                bool scheduled = Installer.Uninstall(ip, env.Log);
                Check(!scheduled && !File.Exists(ip.Exe) && !File.Exists(ip.StartMenuLink), "uninstall removes the program and its shortcuts");
            }
            catch (Exception ex) { Check(false, "install scenario crashed: " + ex.Message); }
            finally { try { Directory.Delete(iroot, true); } catch { } }

            fails += f;
            passes += p;
        }

        static void Compare(SandboxRegistry sandbox, List<string> expected, Action<string> Line, ref int fails)
        {
            var now = sandbox.Describe().OrderBy(x => x).ToList();
            var extra = now.Except(expected).ToList();
            var missing = expected.Except(now).ToList();
            if (extra.Count == 0 && missing.Count == 0) Line("PASS  sandbox registry is exactly as it was before this pass");
            else
            {
                fails++;
                Line("FAIL  sandbox registry differs from before this pass:");
                foreach (var e in extra) Line("        + " + e);
                foreach (var m in missing) Line("        - " + m);
            }
        }

        static void Phases(Env env, Engine engine, IList<Tweak> catalog, Action<string> Line, ref int fails, ref int passes, ref int skips)
        {
            var initial = catalog.ToDictionary(t => t.Id, t => t.Evaluate(env));

            Line("-- phase 1: apply + undo, one at a time");
            foreach (var t in catalog)
            {
                string un = null;
                try { un = t.Unavailable?.Invoke(env); } catch (Exception ex) { un = "error: " + ex.Message; }
                if (un != null) { skips++; continue; }
                if (!t.Revertible && !t.OneShot) continue;  // removals: covered once is enough, they can't come back in the sandbox

                var before = t.Evaluate(env);
                var steps = new List<Step>();
                var res = engine.Apply(new[] { t }, s => steps.Add(s), m => true);
                var after = t.Evaluate(env);
                var problems = new List<string>();
                if (res.Failed > 0) problems.Add("apply failed: " + string.Join(" | ", steps.Where(s => s.Kind == StepKind.Fail || s.Kind == StepKind.Partial).Select(s => s.Detail)));
                if (!t.OneShot && after != TState.Active && after != TState.NotApplicable) problems.Add("state after apply = " + after);

                TState back = after;
                if (t.Revertible)
                {
                    if (engine.EntryFor(t.Id) == null && before != TState.Active && before != TState.NotApplicable) problems.Add("no backup entry recorded");
                    var u = engine.Undo(new[] { t.Id });
                    if (u.Failed > 0) problems.Add("undo failed: " + string.Join(" | ", u.Errors));
                    back = t.Evaluate(env);
                    if (back != before) problems.Add("state after undo = " + back + ", expected " + before);
                    if (engine.EntryFor(t.Id) != null) problems.Add("backup entry left behind");
                }
                string flow = t.OneShot ? "one-shot ran" : before + " -> " + after + " -> " + back;
                if (problems.Count == 0) { passes++; Line("  PASS  " + t.Id.PadRight(36) + flow); }
                else { fails++; Line("  FAIL  " + t.Id.PadRight(36) + flow + "  :: " + string.Join("; ", problems)); }
            }

            Line("-- phase 2: batch apply of every reversible tweak, undo-all through a fresh engine (reloads backup.json)");
            var batch = catalog.Where(t => t.Revertible && t.Unavailable?.Invoke(env) == null).ToList();
            var startStates = batch.ToDictionary(t => t.Id, t => t.Evaluate(env));
            var r2 = engine.Apply(batch, s => { }, m => true);
            Line("  applied " + r2.Applied + ", failed " + r2.Failed + ", skipped " + r2.Skipped);
            if (r2.Failed > 0) { fails++; Line("  FAIL  batch apply had failures"); }
            var notActive = batch.Where(t => { var s = t.Evaluate(env); return s != TState.Active && s != TState.NotApplicable; }).ToList();
            if (notActive.Count > 0) { fails++; Line("  FAIL  not active after batch: " + string.Join(", ", notActive.Select(t => t.Id))); }
            else { passes++; Line("  PASS  all " + batch.Count + " active after batch apply"); }

            var reloaded = new Engine(env, catalog);
            Line("  backup.json reloaded: " + reloaded.Backup.Entries.Count + " entries, " + reloaded.Backup.Entries.Sum(e => e.Changes.Count) + " saved values");
            var u2 = reloaded.Undo(reloaded.Backup.Entries.Select(e => e.TweakId).ToList());
            Line("  undone " + u2.Undone + ", failed " + u2.Failed);
            if (u2.Failed > 0) { fails++; Line("  FAIL  undo-all: " + string.Join(" | ", u2.Errors)); }
            var wrong = batch.Where(t => t.Evaluate(env) != startStates[t.Id]).ToList();
            if (wrong.Count > 0) { fails++; Line("  FAIL  not back to start after undo-all: " + string.Join(", ", wrong.Select(t => t.Id))); }
            else { passes++; Line("  PASS  every tweak back to its starting state after undo-all"); }
            // the original engine must see the same (now empty) backup
            engine.ReloadForTests();
        }
    }
}
