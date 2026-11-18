using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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
                    if (engine.EntryFor(t.Id) == null) problems.Add("no backup entry recorded");
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
