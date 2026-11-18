using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Simplemize
{
    public enum Level { Minor, Heavy }
    public enum Section { Performance, Debloat, Processes, Cleanup, Extras }
    public enum TState { Off, Partial, Active, NotApplicable, Unknown }

    [Flags] public enum After { None = 0, Restart = 1, SignOut = 2, Explorer = 4 }
    [Flags] public enum Preset { None = 0, Light = 1, Gamer = 2, Stripped = 4 }

    /// <summary>Everything an action needs to do its job.</summary>
    public sealed class Env
    {
        public IRegistry Reg;
        public SystemOps Sys;
        public Log Log;
        public SysInfo Info;
        public bool Sandbox;
        public bool IsAdmin;
        /// <summary>--render --demo: generic hardware and a fresh-Windows state for public screenshots.</summary>
        public bool Demo;
        public string DataDir;
        /// <summary>Installed app package names. Null until the (slow) package scan has finished.</summary>
        public HashSet<string> Apps;
        /// <summary>SID of the account Simplemize runs as. Per-user (HKCU) backups are tagged with it.</summary>
        public string UserSid;
        /// <summary>True when Simplemize was elevated with a different account than the one signed in:
        /// then HKCU is that other account's, so per-user changes are blocked.</summary>
        public bool ForeignUser;
        /// <summary>When set, Simplemize may look but not change anything (e.g. its data folder isn't safe).</summary>
        public string BlockedReason;
    }

    public sealed class ActionLine
    {
        public string Where;
        public string What;
        public ActionLine(string where, string what) { Where = where; What = what; }
    }

    /// <summary>One concrete change. Every action knows how to describe itself, check itself and apply itself.</summary>
    public abstract class TweakAction
    {
        public abstract ActionLine Describe();
        /// <summary>true = already in the target state, false = not, null = does not apply to this PC.</summary>
        public abstract bool? IsApplied(Env env);
        /// <summary>Applies the change and returns what is needed to undo it (null if nothing was changed).</summary>
        public abstract Change Apply(Env env);
        public virtual bool Revertible => true;
        /// <summary>Could this action have produced <paramref name="c"/>? Undo only touches what the catalog can explain.</summary>
        public virtual bool Owns(Change c) => false;
    }

    public sealed class Tweak
    {
        public string Id;
        public string Title;
        public string Summary;     // one plain sentence: what it does
        public string Gain;        // what you get
        public string TradeOff;    // what you give up
        public string UndoNote;    // override for the undo line
        public Section Section;
        public string Group;
        public Level Level;
        public string[] Affects = new string[0];
        public List<TweakAction> Actions = new List<TweakAction>();
        public After After;
        public Preset Presets;
        public bool OneShot;        // cleanup job: runs once, nothing to undo
        public bool Security;       // lowers a security protection
        public bool Slow;           // takes minutes
        public string StoreQuery;   // for removed apps: what to search for in the Store
        public string Requires;     // shown on the website, e.g. "Windows 11"
        public Func<Env, string> Unavailable;  // reason it can't be used on this PC, or null
        public Func<Env, string> Warning;      // situational heads-up (laptop, hard drive), or null
        public Func<Env, long> Measure;        // bytes a cleanup would free
        public Func<Change, bool> AlsoOwns;    // extra undo matches (actions built per PC, e.g. network adapters)

        public bool Revertible => !OneShot && Actions.All(a => a.Revertible);

        public bool TouchesUserHive => Actions.OfType<RegSet>().Any(a => a.Hive == Hive.HKCU);

        /// <summary>Why this tweak can't be used right now, or null.</summary>
        public string Reason(Env env)
        {
            string r;
            try { r = Unavailable?.Invoke(env); }
            catch (Exception ex) { r = ex.Message; }
            if (r == null && env.ForeignUser && TouchesUserHive)
                r = "Simplemize is running as a different Windows account than the one signed in, so this per-user setting would change the wrong account.";
            return r;
        }

        public bool Owns(Change c) => Actions.Any(a => a.Owns(c)) || (AlsoOwns != null && AlsoOwns(c));

        public string UndoText
        {
            get
            {
                if (UndoNote != null) return UndoNote;
                if (OneShot) return "Nothing to undo: this is a one-time cleanup.";
                if (!Revertible) return "Simplemize can't undo this one. The restore point covers settings, not removed apps.";
                return "Fully reversible. Simplemize saves every original value and puts it back from the Undo page.";
            }
        }

        public TState Evaluate(Env env)
        {
            if (OneShot) return TState.NotApplicable;
            int yes = 0, no = 0;
            foreach (var a in Actions)
            {
                bool? r;
                try { r = a.IsApplied(env); }
                catch { return TState.Unknown; }
                if (r == true) yes++;
                else if (r == false) no++;
            }
            if (yes + no == 0) return TState.NotApplicable;
            if (no == 0) return TState.Active;
            if (yes == 0) return TState.Off;
            return TState.Partial;
        }
    }

    /// <summary>A record of what something looked like before Simplemize changed it. Stored in backup.json.</summary>
    public sealed class Change
    {
        public string Type { get; set; }        // reg | regpair | regbits | svc | task | power | hiber | app | onedrive
        public string Hive { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public bool Existed { get; set; }
        public string Kind { get; set; }
        public string Value { get; set; }
        public string CreatedKey { get; set; }
        public int OldStart { get; set; }
        public bool OldDelayed { get; set; }
        public bool OldEnabled { get; set; }
        public string OldScheme { get; set; }
        public string NewScheme { get; set; }
        /// <summary>For HKCU values: the account (SID) they belong to.</summary>
        public string User { get; set; }
        /// <summary>regpair: the key inside the string. regbits: the bit mask.</summary>
        public string Part { get; set; }
        /// <summary>regpair: the old value of that key (null = wasn't there). regbits: "1" if the bits were set.</summary>
        public string PartOld { get; set; }
        /// <summary>regbits: the value Windows uses when the registry value is missing.</summary>
        public string PartDefault { get; set; }

        [System.Web.Script.Serialization.ScriptIgnore]
        public string TargetKey => (Type + "|" + Hive + "|" + Path + "|" + Name + "|" + User + "|" + Part).ToLowerInvariant();

        public static Change ForReg(Env env, Hive hive, string path, string name, RegValue before, string createdKey) => new Change
        {
            Type = "reg",
            Hive = hive.ToString(),
            Path = path,
            Name = name,
            Existed = before != null,
            Kind = before?.Kind.ToString(),
            Value = before?.Encoded,
            CreatedKey = createdKey,
            User = hive == Simplemize.Hive.HKCU ? env.UserSid : null,
        };

        public string Describe()
        {
            switch (Type)
            {
                case "reg": return Hive + "\\" + Path + " › " + (Name == "" ? "(Default)" : Name) + (Existed ? " ← " + Value : " ← (remove)");
                case "regpair": return Hive + "\\" + Path + " › " + Name + " › " + Part + (PartOld != null ? " ← " + PartOld : " ← (remove)");
                case "regbits": return Hive + "\\" + Path + " › " + Name + " bits " + Part + " ← " + (PartOld == "1" ? "set" : "clear");
                case "svc": return "service " + Name + " ← " + ServiceInfo.ModeName(OldStart, OldDelayed);
                case "task": return "task " + Path + " ← " + (OldEnabled ? "enabled" : "disabled");
                case "power": return "power plan ← " + OldScheme;
                case "hiber": return "hibernation ← " + (OldEnabled ? "on" : "off");
                default: return Type + " " + Name;
            }
        }
    }

    public static class Reverter
    {
        /// <summary>Removes a key Simplemize created, if it is still empty. Only keys on the value's own path qualify.</summary>
        static void CleanCreatedKey(Env env, Hive hive, Change c)
        {
            if (string.IsNullOrEmpty(c.CreatedKey)) return;
            var path = c.Path.Trim('\\');
            var created = c.CreatedKey.Trim('\\');
            if (!(path.Equals(created, StringComparison.OrdinalIgnoreCase) || path.StartsWith(created + "\\", StringComparison.OrdinalIgnoreCase))) return;
            if (created.IndexOf('\\') < 0) return;   // never a top-level key such as "SOFTWARE"
            RegUtil.DeleteTreeIfEmpty(env.Reg, hive, created);
        }

        public static void Revert(Env env, Change c)
        {
            if (c.User != null && !string.Equals(c.User, env.UserSid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This setting belongs to another Windows account. Sign in as that account to undo it.");

            switch (c.Type)
            {
                case "reg":
                {
                    var hive = (Hive)Enum.Parse(typeof(Hive), c.Hive);
                    if (c.Existed)
                    {
                        var kind = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), c.Kind);
                        env.Reg.Set(hive, c.Path, c.Name, kind, RegCodec.Decode(kind, c.Value));
                    }
                    else env.Reg.DeleteValue(hive, c.Path, c.Name);
                    CleanCreatedKey(env, hive, c);
                    break;
                }
                case "regpair":
                {
                    // Put back only our key inside the string; anything Windows or the user changed since stays.
                    var hive = (Hive)Enum.Parse(typeof(Hive), c.Hive);
                    var pairs = RegPair.ParseText(env.Reg.Get(hive, c.Path, c.Name)?.Data as string)
                        .Where(kv => !string.Equals(kv.Key, c.Part, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (c.PartOld != null) pairs.Add(new KeyValuePair<string, string>(c.Part, c.PartOld));
                    if (pairs.Count == 0 && !c.Existed) env.Reg.DeleteValue(hive, c.Path, c.Name);
                    else env.Reg.Set(hive, c.Path, c.Name, RegistryValueKind.String, RegPair.Join(pairs));
                    CleanCreatedKey(env, hive, c);
                    break;
                }
                case "regbits":
                {
                    // Restore only the bits we cleared; the rest of the flags stay as they are now.
                    var hive = (Hive)Enum.Parse(typeof(Hive), c.Hive);
                    int mask = int.Parse(c.Part, System.Globalization.CultureInfo.InvariantCulture);
                    int def = int.Parse(c.PartDefault, System.Globalization.CultureInfo.InvariantCulture);
                    var cur = env.Reg.Get(hive, c.Path, c.Name);
                    int v = RegBitsClear.ReadInt(cur, def);
                    v = c.PartOld == "1" ? (v | mask) : (v & ~mask);
                    if (!c.Existed && v == def) env.Reg.DeleteValue(hive, c.Path, c.Name);
                    else RegBitsClear.WriteInt(env, hive, c.Path, c.Name, cur, v);
                    CleanCreatedKey(env, hive, c);
                    break;
                }
                case "svc":
                    if (env.Sys.GetService(c.Name) == null) return; // uninstalled since: nothing to restore
                    env.Sys.SetServiceStart(c.Name, c.OldStart, c.OldDelayed);
                    if (c.OldStart == 2) env.Sys.StartService(c.Name);
                    break;
                case "task":
                    if (c.OldEnabled && env.Sys.GetTask(c.Path) != TaskStatus2.Missing) env.Sys.SetTask(c.Path, true);
                    break;
                case "power":
                {
                    var target = c.OldScheme;
                    if (string.IsNullOrEmpty(target) || !env.Sys.SchemeExists(target)) target = PowerPlanAction.Balanced;
                    env.Sys.SetActiveScheme(target);
                    if (!string.IsNullOrEmpty(c.NewScheme) && !string.Equals(target, c.NewScheme, StringComparison.OrdinalIgnoreCase))
                        env.Sys.DeleteScheme(c.NewScheme);
                    break;
                }
                case "hiber":
                    if (c.OldEnabled) env.Sys.SetHibernation(true);
                    break;
                default:
                    throw new InvalidOperationException("This change can't be undone automatically.");
            }
        }
    }

    // ===================================================================================
    // Actions
    // ===================================================================================

    /// <summary>Sets one registry value.</summary>
    public class RegSet : TweakAction
    {
        public readonly Hive Hive;
        public readonly string Path, Name;
        public readonly RegistryValueKind Kind;
        public readonly object Data;
        public readonly string Default;
        /// <summary>If set: when the value is missing, Windows behaves as if it had this value.</summary>
        public object MissingMeans;

        public RegSet(Hive hive, string path, string name, RegistryValueKind kind, object data, string windowsDefault = null)
        {
            Hive = hive; Path = path; Name = name; Kind = kind; Data = data; Default = windowsDefault;
        }

        public override ActionLine Describe() => new ActionLine(
            RegUtil.HiveName(Hive) + "\\" + Path,
            (Name == "" ? "(Default)" : Name) + " = " + RegCodec.Pretty(Kind, Data) + (Default != null ? "    (Windows default: " + Default + ")" : ""));

        protected virtual object NewData(RegValue current) => Data;

        public virtual bool Matches(RegValue cur)
        {
            if (cur == null)
                return MissingMeans != null && RegCodec.Encode(Kind, MissingMeans) == RegCodec.Encode(Kind, Data);
            return RegCodec.SameData(cur, Kind, Data);
        }

        public override bool? IsApplied(Env env) => Matches(env.Reg.Get(Hive, Path, Name));

        public override Change Apply(Env env)
        {
            var cur = env.Reg.Get(Hive, Path, Name);
            var ch = Change.ForReg(env, Hive, Path, Name, cur, RegUtil.TopmostMissingKey(env.Reg, Hive, Path));
            env.Reg.Set(Hive, Path, Name, Kind, NewData(cur));
            return ch;
        }

        public override bool Owns(Change c) =>
            (c.Type == "reg" || c.Type == "regpair" || c.Type == "regbits")
            && string.Equals(c.Hive, Hive.ToString(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Path, Path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Name, Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Clears some bits in a numeric flags value (stored as text, like the accessibility "Flags"), leaving
    /// every other bit as it is. Undo puts back only those bits.
    /// </summary>
    public sealed class RegBitsClear : RegSet
    {
        readonly int mask, windowsDefault;
        readonly string what;

        public RegBitsClear(Hive hive, string path, string name, int mask, int windowsDefault, string what)
            : base(hive, path, name, RegistryValueKind.String, null, windowsDefault.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            this.mask = mask;
            this.windowsDefault = windowsDefault;
            this.what = what;
        }

        public static int ReadInt(RegValue v, int fallback)
        {
            if (v == null) return fallback;
            if (v.Data is int i) return i;
            return int.TryParse(Convert.ToString(v.Data, System.Globalization.CultureInfo.InvariantCulture), out var n) ? n : fallback;
        }

        public static void WriteInt(Env env, Hive hive, string path, string name, RegValue current, int value)
        {
            if (current != null && current.Kind == RegistryValueKind.DWord) env.Reg.Set(hive, path, name, RegistryValueKind.DWord, value);
            else env.Reg.Set(hive, path, name, RegistryValueKind.String, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public override bool Matches(RegValue cur) => (ReadInt(cur, windowsDefault) & mask) == 0;

        public override Change Apply(Env env)
        {
            var cur = env.Reg.Get(Hive, Path, Name);
            int v = ReadInt(cur, windowsDefault);
            var ch = Change.ForReg(env, Hive, Path, Name, cur, RegUtil.TopmostMissingKey(env.Reg, Hive, Path));
            ch.Type = "regbits";
            ch.Part = mask.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ch.PartOld = (v & mask) != 0 ? "1" : "0";
            ch.PartDefault = windowsDefault.ToString(System.Globalization.CultureInfo.InvariantCulture);
            WriteInt(env, Hive, Path, Name, cur, v & ~mask);
            return ch;
        }

        public override ActionLine Describe() => new ActionLine(
            RegUtil.HiveName(Hive) + "\\" + Path,
            Name + ": clear bit 0x" + mask.ToString("X") + " (" + what + "), every other setting kept    (Windows default: " + windowsDefault + ")");
    }

    /// <summary>Sets one "key=value;" pair inside a string value, keeping the other pairs (DirectX user settings).</summary>
    public sealed class RegPair : RegSet
    {
        readonly string key, value;

        public RegPair(Hive hive, string path, string name, string key, string value)
            : base(hive, path, name, RegistryValueKind.String, key + "=" + value + ";")
        {
            this.key = key;
            this.value = value;
        }

        static List<KeyValuePair<string, string>> Parse(RegValue v) => ParseText(v?.Data as string);

        public static string Join(IEnumerable<KeyValuePair<string, string>> pairs) => string.Concat(pairs.Select(kv => kv.Key + "=" + kv.Value + ";"));

        public override Change Apply(Env env)
        {
            var cur = env.Reg.Get(Hive, Path, Name);
            var old = Parse(cur).Where(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).FirstOrDefault();
            var ch = Change.ForReg(env, Hive, Path, Name, cur, RegUtil.TopmostMissingKey(env.Reg, Hive, Path));
            ch.Type = "regpair";
            ch.Part = key;
            ch.PartOld = old;
            env.Reg.Set(Hive, Path, Name, RegistryValueKind.String, NewData(cur));
            return ch;
        }

        public static List<KeyValuePair<string, string>> ParseText(string s)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (var part in s.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) list.Add(new KeyValuePair<string, string>(part.Substring(0, eq).Trim(), part.Substring(eq + 1).Trim()));
            }
            return list;
        }

        protected override object NewData(RegValue current)
        {
            var list = Parse(current).Where(kv => !string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
            list.Add(new KeyValuePair<string, string>(key, value));
            return Join(list);
        }

        public override bool Matches(RegValue cur) =>
            Parse(cur).Any(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && kv.Value == value);

        public override ActionLine Describe() => new ActionLine(
            RegUtil.HiveName(Hive) + "\\" + Path,
            Name + " gets " + key + "=" + value + "    (your other graphics settings in this value are kept)");
    }

    /// <summary>Turns a startup entry off the same way Task Manager does (StartupApproved).</summary>
    public sealed class StartupOff : RegSet
    {
        readonly string label;

        public StartupOff(Hive hive, string approvedPath, string name, string label)
            : base(hive, approvedPath, name, RegistryValueKind.Binary, Blob(false))
        {
            this.label = label;
        }

        public static byte[] Blob(bool enabled)
        {
            var b = new byte[12];
            b[0] = (byte)(enabled ? 2 : 3);
            if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTimeUtc()).CopyTo(b, 4);
            return b;
        }

        protected override object NewData(RegValue current) => Blob(false);

        public override bool Matches(RegValue cur) => cur?.Data is byte[] b && b.Length > 0 && (b[0] & 1) == 1;

        public override ActionLine Describe() => new ActionLine(
            RegUtil.HiveName(Hive) + "\\" + Path,
            Name + " = disabled    (the same switch as Task Manager › Startup apps)");
    }

    public sealed class ServiceStart : TweakAction
    {
        public readonly string Name, Label;
        public readonly int Start;
        public readonly bool Delayed;
        public bool StopNow;
        /// <summary>Only ever make the service start less (Automatic → Manual), never raise a Disabled one.</summary>
        public bool LowerOnly;

        public ServiceStart(string name, string label, int start, bool delayed = false, bool stopNow = false, bool lowerOnly = false)
        {
            Name = name; Label = label; Start = start; Delayed = delayed; StopNow = stopNow; LowerOnly = lowerOnly;
        }

        public override ActionLine Describe() => new ActionLine(
            "Service · " + Name + (Label != null ? " (" + Label + ")" : ""),
            "startup type → " + ServiceInfo.ModeName(Start, Delayed) + (StopNow && Start == 4 ? ", stopped now" : "")
            + (LowerOnly ? "    (left alone if you already disabled it)" : ""));

        bool Done(ServiceInfo s) => LowerOnly ? s.Start >= Start : s.Start == Start && (Start != 2 || s.Delayed == Delayed);

        public override bool? IsApplied(Env env)
        {
            var s = env.Sys.GetService(Name);
            if (s == null) return null;
            return Done(s);
        }

        public override bool Owns(Change c) => c.Type == "svc" && string.Equals(c.Name, Name, StringComparison.OrdinalIgnoreCase);

        public override Change Apply(Env env)
        {
            var s = env.Sys.GetService(Name);
            if (s == null) return null;
            if (LowerOnly && Done(s)) return null;
            var ch = new Change { Type = "svc", Name = Name, OldStart = s.Start, OldDelayed = s.Delayed };
            if (!(s.Start == Start && (Start != 2 || s.Delayed == Delayed)))
                env.Sys.SetServiceStart(Name, Start, Delayed);
            if (StopNow && Start == 4) env.Sys.StopService(Name);
            return ch;
        }
    }

    public sealed class TaskOff : TweakAction
    {
        public readonly string Path;
        public TaskOff(string path) { Path = path; }

        public override ActionLine Describe() => new ActionLine("Scheduled task", Path + " → disabled");

        public override bool? IsApplied(Env env)
        {
            switch (env.Sys.GetTask(Path))
            {
                case TaskStatus2.Disabled: return true;
                case TaskStatus2.Enabled: return false;
                default: return null;
            }
        }

        public override Change Apply(Env env)
        {
            var st = env.Sys.GetTask(Path);
            if (st == TaskStatus2.Missing) return null;
            // If we can't see the current state we can't record it faithfully, so we don't touch it.
            if (st == TaskStatus2.Unknown) throw new InvalidOperationException("Couldn't read this task's current state, so it was left alone.");
            if (st == TaskStatus2.Disabled) return null;
            var ch = new Change { Type = "task", Path = Path, OldEnabled = true };
            env.Sys.SetTask(Path, false);
            return ch;
        }

        public override bool Owns(Change c) => c.Type == "task" && string.Equals(c.Path, Path, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class PowerPlanAction : TweakAction
    {
        public const string Ours = "3d1a5e70-6b4f-4c2a-8e19-5f0c2b7d9e41";
        public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        public const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

        public override ActionLine Describe() => new ActionLine(
            "Power plan",
            "adds “Simplemize Ultimate” (a copy of Windows' hidden Ultimate Performance plan) and makes it active");

        public override bool? IsApplied(Env env)
        {
            var active = env.Sys.GetActiveScheme();
            if (active == null) return null;
            return string.Equals(active, Ours, StringComparison.OrdinalIgnoreCase);
        }

        public override Change Apply(Env env)
        {
            var old = env.Sys.GetActiveScheme();
            if (!env.Sys.SchemeExists(Ours))
            {
                string basedOn = "Ultimate Performance";
                bool ok = env.Sys.DuplicateScheme(Ultimate, Ours);
                if (!ok) { basedOn = "High performance"; ok = env.Sys.DuplicateScheme(High, Ours); }
                if (!ok) throw new InvalidOperationException("This PC doesn't offer a high-performance power plan (common on laptops that use Modern Standby).");
                env.Sys.RenameScheme(Ours, "Simplemize Ultimate", "Copy of " + basedOn + " made by Simplemize. Safe to delete.");
            }
            env.Sys.SetActiveScheme(Ours);
            return new Change { Type = "power", OldScheme = old ?? Balanced, NewScheme = Ours };
        }

        static readonly System.Text.RegularExpressions.Regex Guid =
            new System.Text.RegularExpressions.Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");

        public override bool Owns(Change c) =>
            c.Type == "power" && string.Equals(c.NewScheme, Ours, StringComparison.OrdinalIgnoreCase)
            && (c.OldScheme == null || Guid.IsMatch(c.OldScheme));
    }

    public sealed class HibernateOff : TweakAction
    {
        public override ActionLine Describe() => new ActionLine("powercfg", "/hibernate off    (deletes hiberfil.sys, turns off Hibernate and Fast Startup)");
        public override bool? IsApplied(Env env) => !env.Sys.HibernationOn();

        public override Change Apply(Env env)
        {
            if (!env.Sys.HibernationOn()) return null;   // already off: nothing to change or remember
            env.Sys.SetHibernation(false);
            return new Change { Type = "hiber", OldEnabled = true };
        }

        public override bool Owns(Change c) => c.Type == "hiber";
    }

    public sealed class AppRemove : TweakAction
    {
        public readonly string Pattern;
        public AppRemove(string pattern) { Pattern = pattern; }

        public override bool Revertible => false;

        public override ActionLine Describe() => new ActionLine(
            "App package · " + Pattern,
            "uninstalled for every account, and removed from the image Windows uses for new accounts");

        public bool Installed(Env env) => env.Apps != null && env.Apps.Any(n => SystemOps.Wild(Pattern, n));

        public override bool? IsApplied(Env env) => env.Apps == null ? (bool?)null : !Installed(env);

        public override Change Apply(Env env)
        {
            if (!Installed(env)) return null;
            if (!env.Sys.RemoveApp(Pattern, out var detail))
                throw new InvalidOperationException("Windows did not remove it" + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail.Trim()));
            env.Apps.RemoveWhere(n => SystemOps.Wild(Pattern, n));
            return new Change { Type = "app", Name = Pattern };
        }
    }

    public sealed class OneDriveRemove : TweakAction
    {
        public override bool Revertible => false;
        public override ActionLine Describe() => new ActionLine("OneDrive", "runs OneDrive's own uninstaller (OneDriveSetup.exe /uninstall)");
        public override bool? IsApplied(Env env) => env.Sys.OneDriveUninstallCommand() == null;

        public override Change Apply(Env env)
        {
            if (env.Sys.OneDriveUninstallCommand() == null) return null;
            if (!env.Sys.UninstallOneDrive(out var detail))
                throw new InvalidOperationException("The OneDrive uninstaller reported a problem" + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail.Trim()));
            return new Change { Type = "onedrive", Name = "OneDrive" };
        }
    }

    /// <summary>A one-time job (cleanup). Has no state and nothing to undo.</summary>
    public sealed class Job : TweakAction
    {
        readonly string where, what;
        readonly Func<Env, string> run;
        public string LastResult;

        public Job(string where, string what, Func<Env, string> run)
        {
            this.where = where; this.what = what; this.run = run;
        }

        public override bool Revertible => false;
        public override ActionLine Describe() => new ActionLine(where, what);
        public override bool? IsApplied(Env env) => null;

        public override Change Apply(Env env)
        {
            LastResult = run(env);
            return null;
        }
    }
}
