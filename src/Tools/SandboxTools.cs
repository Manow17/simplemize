using System;
using System.Collections.Generic;
using System.Linq;

namespace Simplemize
{
    public static class SandboxTools
    {
        /// <summary>
        /// Inside the sandbox only: puts every value a tweak touches back to its Windows default
        /// (value removed, services Automatic, tasks enabled, hibernation on), so the app behaves
        /// as it would on a freshly installed PC. Returns how many values were reset.
        /// </summary>
        public static int SeedFresh(Env env, IList<Tweak> catalog, SandboxRegistry sandbox)
        {
            if (!env.Sandbox) throw new InvalidOperationException("SeedFresh only runs in the sandbox.");
            int seeded = 0;
            foreach (var t in catalog.Where(x => x.Revertible))
                foreach (var a in t.Actions)
                {
                    bool? applied;
                    try { applied = a.IsApplied(env); } catch { continue; }
                    if (applied != true) continue;
                    seeded++;
                    switch (a)
                    {
                        case RegSet r: sandbox.DeleteValue(r.Hive, r.Path, r.Name); break;
                        case ServiceStart s: env.Sys.SetServiceStart(s.Name, s.Start == 2 ? 3 : 2, false); break;
                        case TaskOff k: env.Sys.SetTask(k.Path, true); break;
                        case HibernateOff _: env.Sys.SetHibernation(true); break;
                        case PowerAC p: p.SimulateDefault(env); break;
                        case NvSetting n: n.SimulateDefault(env); break;
                        case DisplayMaxRefresh d: d.SimulateDefault(env); break;
                    }
                }
            return seeded;
        }

        /// <summary>Generic hardware for public screenshots, so nothing about this PC ends up on the website.</summary>
        public static void ApplyDemoIdentity(Env env)
        {
            env.Demo = true;
            env.Sys.Demo = true;
            env.IsAdmin = true;
            var i = env.Info;
            i.OsName = "Windows 11 Pro";
            i.OsRelease = "24H2";
            i.Build = 26100;
            i.Ubr = 2605;
            i.Cpu = "AMD Ryzen 7 7800X3D 8-Core Processor";
            i.Gpu = "NVIDIA GeForce RTX 4070";
            i.Gpus = new List<string> { "NVIDIA GeForce RTX 4070" };
            if (Environment.GetCommandLineArgs().Contains("--demo-amd"))
            {
                i.Cpu = "AMD Ryzen 7 7700X 8-Core Processor";
                i.CpuVendor = "AuthenticAMD";
                i.Gpu = "AMD Radeon RX 7800 XT";
                i.Gpus = new List<string> { "AMD Radeon RX 7800 XT" };
                env.Sys.NvOverride = false;
            }
            i.RamBytes = 34_359_738_368L;
            i.IsLaptop = false;
            i.SystemOnHdd = false;
            env.Apps = new HashSet<string>(new[]
            {
                "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingSearch", "Microsoft.GetHelp", "Microsoft.Getstarted",
                "Microsoft.MicrosoftSolitaireCollection", "Microsoft.WindowsFeedbackHub", "Microsoft.MicrosoftOfficeHub",
                "Clipchamp.Clipchamp", "Microsoft.PowerAutomateDesktop", "Microsoft.Copilot", "Microsoft.Todos",
                "Microsoft.YourPhone", "Microsoft.OutlookForWindows", "MSTeams", "SpotifyAB.SpotifyMusic", "king.com.CandyCrushSaga",
            }, StringComparer.OrdinalIgnoreCase);
        }
    }
}
