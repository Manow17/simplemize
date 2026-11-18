using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Simplemize
{
    // ---- backup file ----------------------------------------------------------------------

    public sealed class BackupEntry
    {
        public string TweakId { get; set; }
        public string Title { get; set; }
        public string AppliedAt { get; set; }
        public List<Change> Changes { get; set; } = new List<Change>();
    }

    public sealed class RemovedRecord
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string At { get; set; }
        public string StoreQuery { get; set; }
    }

    public sealed class RestoreRecord
    {
        public string At { get; set; }
        public uint Sequence { get; set; }
        public string Description { get; set; }
    }

    public sealed class BackupFile
    {
        public int Version { get; set; } = 1;
        public List<BackupEntry> Entries { get; set; } = new List<BackupEntry>();
        public List<RemovedRecord> Removed { get; set; } = new List<RemovedRecord>();
        public List<RestoreRecord> RestorePoints { get; set; } = new List<RestoreRecord>();
        public Snapshot Baseline { get; set; }
        /// <summary>Set when Simplemize had to switch System Restore on to make a restore point.</summary>
        public string SystemRestoreTurnedOn { get; set; }
    }

    // ---- progress events ------------------------------------------------------------------

    public enum StepKind { Running, Ok, Partial, Fail, Skip, Info }

    public sealed class Step
    {
        public StepKind Kind;
        public string Title;
        public string Detail;
        public int Index;   // 0-based position in the batch, -1 for restore point/info lines
        public int Total;
    }

    public sealed class BatchResult
    {
        public int Applied, Failed, Skipped;
        public bool Cancelled;
        public After After;
        public RestorePointInfo RestorePoint;
        public bool RestorePointFailed;
    }

    /// <summary>Applies and undoes tweaks, keeps the backup file, creates the restore point.</summary>
    public sealed class Engine
    {
        public const string RestorePointName = "Simplemize: before changes";
        public readonly Env Env;
        public readonly IList<Tweak> Catalog;
        public BackupFile Backup { get; private set; }
        public RestorePointInfo SessionRestorePoint { get; private set; }
        readonly string file;
        readonly object gate = new object();

        public Engine(Env env, IList<Tweak> catalog)
        {
            Env = env;
            Catalog = catalog;
            file = Path.Combine(env.DataDir, "backup.json");
            Load();
        }

        public string BackupPath => file;

        public BackupEntry EntryFor(string id) => Backup.Entries.FirstOrDefault(e => e.TweakId == id);
        public Tweak Find(string id) => Catalog.FirstOrDefault(t => t.Id == id);

        bool Mine(Change c) => c.User == null || string.Equals(c.User, Env.UserSid, StringComparison.OrdinalIgnoreCase);

        /// <summary>Is there anything this account can undo for this tweak?</summary>
        public bool HasUndoFor(string id)
        {
            var e = EntryFor(id);
            return e != null && e.Changes.Any(Mine);
        }

        public IEnumerable<BackupEntry> UndoableEntries => Backup.Entries.Where(e => e.Changes.Any(Mine));

        void Load()
        {
            Backup = new BackupFile();
            if (!File.Exists(file)) return;
            try
            {
                var b = Json().Deserialize<BackupFile>(File.ReadAllText(file, Encoding.UTF8));
                if (b != null)
                {
                    b.Entries = b.Entries ?? new List<BackupEntry>();
                    b.Removed = b.Removed ?? new List<RemovedRecord>();
                    b.RestorePoints = b.RestorePoints ?? new List<RestoreRecord>();
                    foreach (var e in b.Entries) e.Changes = e.Changes ?? new List<Change>();
                    Backup = b;
                }
            }
            catch (Exception ex)
            {
                // Never overwrite a backup we could not read: keep a copy aside first.
                var aside = file + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(file, aside); } catch { }
                Env.Log.Error("backup.json could not be read (" + ex.Message + "). A copy was kept at " + aside);
            }
        }

        /// <summary>Re-reads backup.json (used by the self-test).</summary>
        public void ReloadForTests() => Load();

        static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public void Save()
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var tmp = file + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);   // never reuse a leftover (or planted) temp file
                File.WriteAllText(tmp, Pretty(Json().Serialize(Backup)), Encoding.UTF8);
                if (File.Exists(file)) File.Replace(tmp, file, null);
                else File.Move(tmp, file);
            }
        }

        /// <summary>Save with a few retries (antivirus or the indexer can hold the file for a moment).</summary>
        public bool TrySave(out string error)
        {
            error = null;
            for (int i = 0; i < 4; i++)
            {
                try { Save(); return true; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    error = ex.Message;
                    System.Threading.Thread.Sleep(250 * (i + 1));
                }
            }
            Env.Log.Error("could not save backup.json: " + error);
            return false;
        }

        /// <summary>Indents JSON so the backup is readable by humans who open it.</summary>
        static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inStr = false, esc = false;
            foreach (char c in json)
            {
                if (inStr)
                {
                    sb.Append(c);
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; sb.Append(c); continue; }
                switch (c)
                {
                    case '{':
                    case '[':
                        sb.Append(c).Append('\n').Append(' ', ++indent * 2);
                        break;
                    case '}':
                    case ']':
                        sb.Append('\n').Append(' ', --indent * 2).Append(c);
                        break;
                    case ',':
                        sb.Append(c).Append('\n').Append(' ', indent * 2);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        public void CaptureBaseline()
        {
            if (Backup.Baseline != null) return;
            Backup.Baseline = Snapshot.Take();
            Save();
        }

        /// <summary>
        /// Creates a restore point and records it (plus anything Simplemize had to fix to make it possible).
        /// Used before applying, and by the "Create a restore point now" button.
        /// </summary>
        public RestorePointResult MakeRestorePoint(Action<Step> report)
        {
            var rp = Env.Sys.CreateRestorePoint(RestorePointName);
            if (rp.TurnedOnSystemRestore)
            {
                Backup.SystemRestoreTurnedOn = Now() + " on " + (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");
                report?.Invoke(new Step { Kind = StepKind.Info, Index = -1, Title = "System Restore was switched off for your system drive", Detail = "Simplemize turned it on so a restore point could be made. You can switch it off again in System Properties › System Protection." });
            }
            foreach (var note in rp.Notes)
            {
                Env.Log.Info(note);
                report?.Invoke(new Step { Kind = StepKind.Info, Index = -1, Title = "Fixed so restore points work", Detail = note });
            }
            if (rp.Ok)
            {
                SessionRestorePoint = rp.Point;
                if (!rp.Simulated)
                    Backup.RestorePoints.Add(new RestoreRecord { At = Now(), Sequence = rp.Point.Sequence, Description = rp.Point.Description });
            }
            TrySave(out _);
            return rp;
        }

        static void Merge(BackupEntry entry, Change ch)
        {
            // Keep the very first original value: re-applying must never overwrite the real "before" state.
            if (!entry.Changes.Any(c => c.TargetKey == ch.TargetKey)) entry.Changes.Add(ch);
        }

        public BatchResult Apply(IList<Tweak> tweaks, Action<Step> progress, Func<string, bool> continueWithoutRestorePoint)
        {
            var r = new BatchResult();
            Env.Log.Info("=== apply: " + string.Join(", ", tweaks.Select(t => t.Id)) + (Env.Sandbox ? " [sandbox]" : ""));

            // 0 — make sure the backup file can be written before anything changes
            if (Backup.Baseline == null) Backup.Baseline = Snapshot.Take();
            if (!TrySave(out var saveError))
            {
                r.Cancelled = true;
                progress(new Step { Kind = StepKind.Fail, Index = -1, Title = "Couldn't write the backup file", Detail = saveError + " Nothing was changed." });
                return r;
            }

            // 1 — restore point (once per session)
            if (SessionRestorePoint == null)
            {
                progress(new Step { Kind = StepKind.Running, Index = -1, Title = "Creating a restore point", Detail = "Windows can take up to a minute for this." });
                var rp = MakeRestorePoint(s => progress(s));
                if (rp.Ok)
                {
                    r.RestorePoint = rp.Point;
                    if (rp.Simulated)
                        progress(new Step { Kind = StepKind.Ok, Index = -1, Title = "Restore point simulated (sandbox)", Detail = "Nothing was created in Windows, so it won't appear in System Restore. Outside the sandbox this is a real restore point." });
                    else
                        progress(new Step { Kind = StepKind.Ok, Index = -1, Title = "Restore point created", Detail = "“" + rp.Point.Description + "” · " + rp.Point.Created.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) });
                }
                else
                {
                    r.RestorePointFailed = true;
                    progress(new Step { Kind = StepKind.Fail, Index = -1, Title = "Couldn't create a restore point", Detail = rp.Message });
                    Env.Log.Error("restore point failed: " + rp.Message);
                    if (!continueWithoutRestorePoint(rp.Message))
                    {
                        r.Cancelled = true;
                        Env.Log.Info("cancelled by user (no restore point)");
                        return r;
                    }
                }
            }
            else
            {
                r.RestorePoint = SessionRestorePoint;
                progress(new Step { Kind = StepKind.Ok, Index = -1, Title = "Restore point already made this session", Detail = "“" + SessionRestorePoint.Description + "” · " + SessionRestorePoint.Created.ToString("HH:mm", CultureInfo.InvariantCulture) });
            }

            // 2 — the tweaks themselves
            bool aborted = false;
            for (int i = 0; i < tweaks.Count; i++)
            {
                var t = tweaks[i];
                if (aborted)
                {
                    r.Skipped++;
                    progress(new Step { Kind = StepKind.Skip, Index = i, Total = tweaks.Count, Title = t.Title, Detail = "Not applied: stopped because the backup file couldn't be saved." });
                    continue;
                }
                progress(new Step { Kind = StepKind.Running, Index = i, Total = tweaks.Count, Title = t.Title, Detail = t.Slow ? "This one takes a while." : null });

                var reason = t.Reason(Env);
                if (reason != null)
                {
                    r.Skipped++;
                    progress(new Step { Kind = StepKind.Skip, Index = i, Total = tweaks.Count, Title = t.Title, Detail = reason });
                    Env.Log.Info(t.Id + ": skipped (" + reason + ")");
                    continue;
                }

                var entry = EntryFor(t.Id);
                bool isNew = entry == null;
                if (isNew) entry = new BackupEntry { TweakId = t.Id, Title = t.Title, AppliedAt = Now() };

                var errors = new List<string>();
                string result = null;
                int changed = 0;
                foreach (var a in t.Actions)
                {
                    try
                    {
                        var ch = a.Apply(Env);
                        if (ch != null)
                        {
                            changed++;
                            if (t.Revertible)
                            {
                                bool added = !entry.Changes.Any(c => c.TargetKey == ch.TargetKey);
                                Merge(entry, ch);
                                if (isNew && !Backup.Entries.Contains(entry)) Backup.Entries.Add(entry);
                                // Persist after every single change. If the original can't be stored, the
                                // change is rolled back on the spot and the batch stops.
                                if (!TrySave(out var err))
                                {
                                    try { Reverter.Revert(Env, ch); } catch (Exception rex) { Env.Log.Error("rollback failed: " + rex.Message); }
                                    if (added) entry.Changes.Remove(ch);
                                    if (entry.Changes.Count == 0) Backup.Entries.Remove(entry);
                                    errors.Add("Couldn't save the backup file (" + err + "), so this change was rolled back.");
                                    aborted = true;
                                    break;
                                }
                            }
                        }
                        if (a is Job j && !string.IsNullOrEmpty(j.LastResult)) result = j.LastResult;
                    }
                    catch (Exception ex)
                    {
                        var d = a.Describe();
                        errors.Add(ex.Message);
                        Env.Log.Error(t.Id + ": " + d.Where + " / " + d.What + ": " + ex.Message);
                    }
                }

                if (!t.Revertible && !t.OneShot && changed > 0)
                {
                    Backup.Removed.Add(new RemovedRecord { Id = t.Id, Title = t.Title, At = Now(), StoreQuery = t.StoreQuery });
                    TrySave(out _);
                }

                if (errors.Count == 0)
                {
                    r.Applied++;
                    r.After |= t.After;
                    progress(new Step { Kind = StepKind.Ok, Index = i, Total = tweaks.Count, Title = t.Title, Detail = result });
                    Env.Log.Info(t.Id + ": ok" + (result != null ? " (" + result + ")" : ""));
                }
                else if (errors.Count < t.Actions.Count)
                {
                    r.Failed++;
                    r.After |= t.After;
                    progress(new Step { Kind = StepKind.Partial, Index = i, Total = tweaks.Count, Title = t.Title, Detail = "Partly applied. " + errors[0] });
                }
                else
                {
                    r.Failed++;
                    progress(new Step { Kind = StepKind.Fail, Index = i, Total = tweaks.Count, Title = t.Title, Detail = errors[0] });
                }
            }

            // 3 — tell running apps about the new settings
            Env.Sys.RefreshMouseAndMenus();
            Env.Sys.BroadcastSettingsChange();
            Env.Log.Info("=== done: " + r.Applied + " applied, " + r.Failed + " failed, " + r.Skipped + " skipped");
            return r;
        }

        public sealed class UndoResult
        {
            public int Undone, Failed;
            public After After;
            public List<string> Errors = new List<string>();
        }

        public UndoResult Undo(IEnumerable<string> ids)
        {
            var res = new UndoResult();
            foreach (var id in ids.ToList())
            {
                var e = EntryFor(id);
                if (e == null) continue;
                var t = Find(id);
                Env.Log.Info("=== undo: " + id + (Env.Sandbox ? " [sandbox]" : ""));
                var keep = new List<Change>();
                int failedHere = 0;
                for (int i = e.Changes.Count - 1; i >= 0; i--)
                {
                    var c = e.Changes[i];
                    if (!Mine(c)) { keep.Insert(0, c); continue; }   // another account's value: theirs to undo
                    // Only restore what this tweak can actually have changed. A backup entry that doesn't
                    // match the catalog (edited or planted file) is refused.
                    if (t == null || !t.Owns(c))
                    {
                        keep.Insert(0, c);
                        failedHere++;
                        res.Errors.Add(e.Title + ": a saved value doesn't match what this change touches, so it was skipped for safety.");
                        Env.Log.Error("undo " + id + ": refused unexpected entry " + c.Describe());
                        continue;
                    }
                    try { Reverter.Revert(Env, c); Env.Log.Info("restored " + c.Describe()); }
                    catch (Exception ex)
                    {
                        keep.Insert(0, c);
                        failedHere++;
                        res.Errors.Add(e.Title + ": " + ex.Message);
                        Env.Log.Error("undo " + id + ": " + c.Describe() + ": " + ex.Message);
                    }
                }
                if (keep.Count == 0) Backup.Entries.Remove(e);
                else e.Changes = keep;
                if (failedHere == 0) res.Undone++; else res.Failed++;
                if (t != null) res.After |= t.After;
                TrySave(out _);
            }
            Env.Sys.RefreshMouseAndMenus();
            Env.Sys.BroadcastSettingsChange();
            return res;
        }
    }
}
