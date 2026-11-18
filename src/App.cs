using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using Simplemize.Ui;

[assembly: AssemblyTitle("Simplemize")]
[assembly: AssemblyDescription("A small, open-source Windows optimizer with a restore point before every change.")]
[assembly: AssemblyProduct("Simplemize")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]
[assembly: AssemblyInformationalVersion("1.0.2")]
// Compiled with csc directly, so state the target explicitly: it switches on .NET 4.8 behaviour (per-monitor DPI and so on).
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace Simplemize
{
    public static class Program
    {
        static string[] args = new string[0];

        static bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        static string Value(string flag, string fallback = null)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return fallback;
        }

        public static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        [STAThread]
        public static int Main(string[] argv)
        {
            args = argv ?? new string[0];
            bool selftest = Has("--selftest"), render = Has("--render"), export = Has("--export");
            // Tooling modes never touch the PC: they always run in the sandbox.
            bool sandbox = Has("--sandbox") || selftest || render || export;
            bool admin = IsAdmin();

            if (!admin && !sandbox && !Has("--no-elevate"))
            {
                if (TryElevate(args)) return 0;
                // The user said no to the UAC prompt: carry on read-only.
            }

            string dataDir = selftest ? Path.Combine(Path.GetTempPath(), "Simplemize-selftest")
                           : render ? Path.Combine(Path.GetTempPath(), "Simplemize-render")
                           : sandbox ? Path.Combine(Path.GetTempPath(), "Simplemize-sandbox")
                           : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Simplemize");
            if ((selftest || render || Has("--reset-sandbox")) && Directory.Exists(dataDir))
                try { Directory.Delete(dataDir, true); } catch { }

            // Read-only runs log to the user's own folder, so they never create the admin-only data folder.
            var log = new Log(!sandbox && !admin
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Simplemize", "simplemize.log")
                : Path.Combine(dataDir, "simplemize.log"));
            var realReg = new RealRegistry();
            IRegistry reg = sandbox
                ? new SandboxRegistry(realReg, selftest || render || export ? null : Path.Combine(dataDir, "sandbox-registry.json"))
                : (IRegistry)realReg;

            // One Simplemize at a time: two windows would each keep their own copy of the backup file.
            System.Threading.Mutex single = null;
            if (!sandbox)
            {
                bool got;
                try { single = new System.Threading.Mutex(false, @"Global\Simplemize.Instance"); }
                catch { single = new System.Threading.Mutex(false, @"Local\Simplemize.Instance"); }
                try { got = single.WaitOne(0); }
                catch (System.Threading.AbandonedMutexException) { got = true; }
                if (!got)
                {
                    MessageBox.Show("Simplemize is already open.", "Simplemize", MessageBoxButton.OK, MessageBoxImage.Information);
                    return 0;
                }
            }

            var env = new Env
            {
                Reg = reg,
                Log = log,
                Sandbox = sandbox,
                IsAdmin = admin,
                DataDir = dataDir,
                Info = SysInfo.Read(),
                UserSid = Security.CurrentUserSid(),
            };
            env.Sys = new SystemOps(reg, log, sandbox, admin);
            if (!sandbox)
            {
                env.ForeignUser = Security.ShellRunsAsAnotherUser();
                // The backup is replayed with admin rights on undo, so its folder must be admin-only.
                if (admin) env.BlockedReason = Security.SecureDataDir(dataDir, log);
                if (env.BlockedReason != null) log.Error(env.BlockedReason);
            }
            var catalog = Catalog.Build(env);
            var engine = new Engine(env, catalog);
            if (render && Has("--demo"))
            {
                SandboxTools.ApplyDemoIdentity(env);
                SandboxTools.SeedFresh(env, catalog, (SandboxRegistry)reg);
            }

            if (export) return Exporter.Export(catalog, Value("--export", "tweaks.json"));
            if (selftest) return SelfTest.Run(env, engine, catalog, Value("--selftest", "selftest.txt"));

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            Theme.Load(app);
            Theme.Apply(Has("--dark") || (!Has("--light") && Theme.Preferred()));

            if (render)
            {
                double scale = double.TryParse(Value("--scale", "1"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 1;
                return Renderer.Run(app, env, engine, catalog, Value("--render", "shots"), scale, Value("--only"));
            }

            log.Info("Simplemize " + MainWindow.Version + " started" + (admin ? " as administrator" : " read-only") + (sandbox ? " [sandbox]" : "")
                     + " on " + env.Info.OsName + " " + env.Info.OsRelease + " (" + env.Info.Build + "." + env.Info.Ubr + ")");
            app.DispatcherUnhandledException += (o, e) =>
            {
                log.Error("unhandled: " + e.Exception);
                MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + "\n\nDetails were written to " + log.FilePath,
                    "Simplemize", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            };
            var window = new MainWindow(env, engine, catalog);
            int code = app.Run(window);
            GC.KeepAlive(single);
            return code;
        }

        static bool TryElevate(string[] argv)
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = string.Join(" ", argv.Select(a => "\"" + a.Replace("\"", "") + "\"")) + " --no-elevate",
                };
                Process.Start(psi);
                return true;
            }
            catch (Win32Exception) { return false; }   // 1223: the user cancelled the prompt
        }

        public static void RelaunchElevated(Window owner)
        {
            if (TryElevate(new string[0])) Application.Current.Shutdown();
        }
    }
}
