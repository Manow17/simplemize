using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Simplemize
{
    /// <summary>--probe-hw: read-only report of what the Hardware page can see on this PC. Changes nothing.</summary>
    public static class Probe
    {
        public static int Run(Env env, string outFile)
        {
            var sb = new StringBuilder();
            void Line(string s) => sb.AppendLine(s);
            Line("Simplemize hardware probe (read-only)  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Line("CPU: " + env.Info.Cpu + "  vendor=" + env.Info.CpuVendor);
            Line("GPUs: " + string.Join(" | ", env.Info.Gpus));
            Line("");

            Line("NVIDIA driver settings:");
            try
            {
                Line("  available: " + NvDrs.Available);
                if (NvDrs.Available)
                    foreach (var id in new uint[] { HwIds.NvPowerMode, HwIds.NvPrerender })
                    {
                        var v = NvDrs.Read(id);
                        Line("  0x" + id.ToString("X8") + "  name=\"" + NvDrs.NameOf(id) + "\"  status=" + v.Status + "  value=" + v.Current + "  location=" + v.Location + "  userSet=" + v.UserSet);
                    }
            }
            catch (Exception ex) { Line("  error: " + ex.Message); }

            // --write-same: writes the CURRENT value back (changes nothing) to prove the write path works.
            if (Environment.GetCommandLineArgs().Contains("--write-same") && NvDrs.Available)
            {
                try
                {
                    var before = NvDrs.Read(HwIds.NvPowerMode);
                    if (before.Status == NvDrs.OK)
                    {
                        NvDrs.Write(HwIds.NvPowerMode, before.Current);
                        var after = NvDrs.Read(HwIds.NvPowerMode);
                        Line("  write-same test: wrote " + before.Current + ", read back " + after.Current + " -> " + (after.Current == before.Current ? "OK" : "MISMATCH"));
                    }
                }
                catch (Exception ex) { Line("  write-same test: " + ex.Message); }
            }
            Line("");

            Line("Power plan (plugged-in values):");
            var scheme = PowerSettings.ActiveScheme();
            Line("  active scheme: " + scheme);
            if (scheme != null)
                foreach (var s in HwIds.PowerAll)
                    Line("  " + s.Item1.PadRight(26) + (PowerSettings.ReadAC(scheme.Value, s.Item2, s.Item3)?.ToString() ?? "(not on this PC)"));
            Line("");

            Line("Displays:");
            foreach (var d in Displays.List())
                Line("  " + d.Device + "  \"" + d.Name + "\"  " + d.Width + "x" + d.Height + "  " + d.Hz + " Hz  (best offered: " + d.BestHz + " Hz)" + (d.Primary ? "  primary" : ""));

            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
            return 0;
        }
    }

    /// <summary>Identifiers for the Hardware page, all confirmed against NVIDIA's NVAPI headers and powercfg.</summary>
    public static class HwIds
    {
        // NVIDIA (NvApiDriverSettings.h)
        public const uint NvPowerMode = 0x1057EB71;   // PREFERRED_PSTATE_ID: 0 adaptive, 1 prefer max, 5 optimal power (default)
        public const uint NvPrerender = 0x007BA09E;   // PRERENDERLIMIT_ID ("Low Latency Mode" On = 1, default 0 app-controlled)

        // Power settings
        public static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        public static readonly Guid BoostMode = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        public static readonly Guid CoreParkingMin = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
        public static readonly Guid Epp = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        public static readonly Guid MinState = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
        public static readonly Guid SubUsb = new Guid("2a737441-1930-4402-8d77-b2bebba308a3");
        public static readonly Guid UsbSuspend = new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        public static readonly Guid SubPcie = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
        public static readonly Guid Aspm = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");

        public static readonly Tuple<string, Guid, Guid>[] PowerAll =
        {
            Tuple.Create("boost mode", SubProcessor, BoostMode),
            Tuple.Create("core parking min cores %", SubProcessor, CoreParkingMin),
            Tuple.Create("energy perf preference", SubProcessor, Epp),
            Tuple.Create("minimum processor state %", SubProcessor, MinState),
            Tuple.Create("USB selective suspend", SubUsb, UsbSuspend),
            Tuple.Create("PCIe link power mgmt", SubPcie, Aspm),
        };
    }
}
