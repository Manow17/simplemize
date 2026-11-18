using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Simplemize
{
    public enum Hive { HKLM, HKCU }

    /// <summary>A registry value together with its type.</summary>
    public sealed class RegValue
    {
        public readonly RegistryValueKind Kind;
        public readonly object Data;

        public RegValue(RegistryValueKind kind, object data)
        {
            Kind = kind;
            Data = data;
        }

        public string Encoded => RegCodec.Encode(Kind, Data);
    }

    /// <summary>Turns registry data into strings (for the backup file) and back.</summary>
    public static class RegCodec
    {
        public static string Encode(RegistryValueKind kind, object data)
        {
            if (data == null) return null;
            switch (kind)
            {
                case RegistryValueKind.DWord:
                    return unchecked((uint)ToInt(data)).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.QWord:
                    return unchecked((ulong)Convert.ToInt64(data, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.Binary:
                    return BitConverter.ToString((byte[])data).Replace("-", "");
                case RegistryValueKind.MultiString:
                    return string.Join("\u0000", (string[])data);
                default:
                    return Convert.ToString(data, CultureInfo.InvariantCulture);
            }
        }

        public static object Decode(RegistryValueKind kind, string text)
        {
            switch (kind)
            {
                case RegistryValueKind.DWord:
                    return unchecked((int)uint.Parse(text, CultureInfo.InvariantCulture));
                case RegistryValueKind.QWord:
                    return unchecked((long)ulong.Parse(text, CultureInfo.InvariantCulture));
                case RegistryValueKind.Binary:
                    var bytes = new byte[text.Length / 2];
                    for (int i = 0; i < bytes.Length; i++)
                        bytes[i] = byte.Parse(text.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    return bytes;
                case RegistryValueKind.MultiString:
                    return text.Length == 0 ? new string[0] : text.Split('\u0000');
                default:
                    return text;
            }
        }

        static int ToInt(object data)
        {
            if (data is int i) return i;
            if (data is uint u) return unchecked((int)u);
            if (data is long l) return unchecked((int)l);
            return Convert.ToInt32(data, CultureInfo.InvariantCulture);
        }

        /// <summary>Human readable form used in the "what it changes" lists.</summary>
        public static string Pretty(RegistryValueKind kind, object data)
        {
            switch (kind)
            {
                case RegistryValueKind.DWord:
                    uint v = unchecked((uint)ToInt(data));
                    return v > 0xFFFF ? "0x" + v.ToString("X8", CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString:
                    return "\"" + data + "\"";
                case RegistryValueKind.Binary:
                    var b = (byte[])data;
                    return "hex:" + BitConverter.ToString(b, 0, Math.Min(b.Length, 4)).Replace("-", " ").ToLowerInvariant() + (b.Length > 4 ? " …" : "");
                default:
                    return Encode(kind, data);
            }
        }

        public static bool SameData(RegValue cur, RegistryValueKind kind, object data)
        {
            if (cur == null) return false;
            bool curIsNum = cur.Kind == RegistryValueKind.DWord || cur.Kind == RegistryValueKind.QWord;
            bool tgtIsNum = kind == RegistryValueKind.DWord || kind == RegistryValueKind.QWord;
            if (curIsNum != tgtIsNum) return false;
            return string.Equals(Encode(cur.Kind, cur.Data), Encode(kind, data), StringComparison.OrdinalIgnoreCase);
        }
    }

    public interface IRegistry
    {
        RegValue Get(Hive hive, string path, string name);
        void Set(Hive hive, string path, string name, RegistryValueKind kind, object data);
        void DeleteValue(Hive hive, string path, string name);
        bool KeyExists(Hive hive, string path);
        string[] ValueNames(Hive hive, string path);
        string[] SubKeyNames(Hive hive, string path);
        void DeleteKeyTree(Hive hive, string path);
    }

    /// <summary>The real Windows registry (always the 64-bit view).</summary>
    public sealed class RealRegistry : IRegistry
    {
        static RegistryKey Base(Hive h) =>
            RegistryKey.OpenBaseKey(h == Hive.HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

        static RegistryKey Open(Hive h, string path, bool write)
        {
            using (var b = Base(h))
            {
                try { return b.OpenSubKey(path, write); }
                catch (System.Security.SecurityException) { if (write) throw; return null; }
                catch (UnauthorizedAccessException) { if (write) throw; return null; }
            }
        }

        public RegValue Get(Hive hive, string path, string name)
        {
            using (var k = Open(hive, path, false))
            {
                if (k == null) return null;
                var data = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (data == null) return null;
                return new RegValue(k.GetValueKind(name), data);
            }
        }

        public void Set(Hive hive, string path, string name, RegistryValueKind kind, object data)
        {
            using (var b = Base(hive))
            using (var k = b.CreateSubKey(path, true))
            {
                if (k == null) throw new IOException("Could not open or create " + hive + "\\" + path);
                k.SetValue(name, data, kind);
            }
        }

        public void DeleteValue(Hive hive, string path, string name)
        {
            using (var k = Open(hive, path, true))
                k?.DeleteValue(name, false);
        }

        public bool KeyExists(Hive hive, string path)
        {
            using (var k = Open(hive, path, false)) return k != null;
        }

        public string[] ValueNames(Hive hive, string path)
        {
            using (var k = Open(hive, path, false)) return k == null ? new string[0] : k.GetValueNames();
        }

        public string[] SubKeyNames(Hive hive, string path)
        {
            using (var k = Open(hive, path, false)) return k == null ? new string[0] : k.GetSubKeyNames();
        }

        public void DeleteKeyTree(Hive hive, string path)
        {
            using (var b = Base(hive)) b.DeleteSubKeyTree(path, false);
        }
    }

    /// <summary>
    /// Sandbox registry: reads fall through to the real registry, but every write and delete
    /// is kept in memory (and optionally in a JSON file). The real registry is never written.
    /// Used by --sandbox and --selftest.
    /// </summary>
    public sealed class SandboxRegistry : IRegistry
    {
        readonly IRegistry real;
        readonly string file;
        // value key -> value (null = deleted)
        readonly Dictionary<string, RegValue> values = new Dictionary<string, RegValue>();
        readonly HashSet<string> created = new HashSet<string>();
        readonly HashSet<string> deleted = new HashSet<string>();
        readonly object gate = new object();

        public SandboxRegistry(IRegistry real, string file)
        {
            this.real = real;
            this.file = file;
            Load();
        }

        static string K(Hive h, string path) => (h + "\\" + (path ?? "").Trim('\\')).ToLowerInvariant();
        static string V(Hive h, string path, string name) => K(h, path) + "|" + (name ?? "").ToLowerInvariant();

        bool IsDeleted(string key)
        {
            foreach (var d in deleted)
                if ((key == d || key.StartsWith(d + "\\", StringComparison.Ordinal)) && !created.Contains(key))
                    return true;
            return false;
        }

        public RegValue Get(Hive hive, string path, string name)
        {
            lock (gate)
            {
                if (values.TryGetValue(V(hive, path, name), out var v)) return v;
                if (IsDeleted(K(hive, path))) return null;
            }
            return real.Get(hive, path, name);
        }

        public void Set(Hive hive, string path, string name, RegistryValueKind kind, object data)
        {
            lock (gate)
            {
                values[V(hive, path, name)] = new RegValue(kind, data);
                var parts = (path ?? "").Trim('\\').Split('\\');
                for (int i = 1; i <= parts.Length; i++)
                {
                    var k = K(hive, string.Join("\\", parts, 0, i));
                    deleted.Remove(k);
                    if (!real.KeyExists(hive, string.Join("\\", parts, 0, i))) created.Add(k);
                }
                Save();
            }
        }

        public void DeleteValue(Hive hive, string path, string name)
        {
            lock (gate)
            {
                values[V(hive, path, name)] = null;
                Save();
            }
        }

        public bool KeyExists(Hive hive, string path)
        {
            var k = K(hive, path);
            lock (gate)
            {
                if (IsDeleted(k)) return false;
                if (created.Contains(k)) return true;
            }
            return real.KeyExists(hive, path);
        }

        public string[] ValueNames(Hive hive, string path)
        {
            var k = K(hive, path);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lock (gate)
            {
                if (!IsDeleted(k))
                    foreach (var n in real.ValueNames(hive, path)) names[n] = n;
                foreach (var kv in values)
                {
                    int bar = kv.Key.LastIndexOf('|');
                    if (kv.Key.Substring(0, bar) != k) continue;
                    var n = kv.Key.Substring(bar + 1);
                    if (kv.Value == null) names.Remove(n); else names[n] = n;
                }
            }
            return names.Values.ToArray();
        }

        public string[] SubKeyNames(Hive hive, string path)
        {
            var k = K(hive, path);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lock (gate)
            {
                if (!IsDeleted(k))
                    foreach (var n in real.SubKeyNames(hive, path))
                        if (!IsDeleted(k + "\\" + n.ToLowerInvariant())) names[n] = n;
                foreach (var c in created)
                    if (c.StartsWith(k + "\\", StringComparison.Ordinal))
                    {
                        var rest = c.Substring(k.Length + 1);
                        var child = rest.Split('\\')[0];
                        if (!IsDeleted(k + "\\" + child)) names[child] = child;
                    }
            }
            return names.Values.ToArray();
        }

        public void DeleteKeyTree(Hive hive, string path)
        {
            var k = K(hive, path);
            lock (gate)
            {
                foreach (var key in values.Keys.Where(x => x.StartsWith(k + "|", StringComparison.Ordinal) || x.StartsWith(k + "\\", StringComparison.Ordinal)).ToList())
                    values.Remove(key);
                created.RemoveWhere(c => c == k || c.StartsWith(k + "\\", StringComparison.Ordinal));
                deleted.Add(k);
                Save();
            }
        }

        /// <summary>True if the sandbox holds no changes at all (used by the self-test).</summary>
        public int PendingChanges
        {
            get
            {
                lock (gate)
                {
                    int n = 0;
                    foreach (var kv in values)
                    {
                        int bar = kv.Key.LastIndexOf('|');
                        var hk = kv.Key.Substring(0, bar);
                        var parts = hk.Split(new[] { '\\' }, 2);
                        var hive = parts[0] == "hklm" ? Hive.HKLM : Hive.HKCU;
                        var realVal = real.Get(hive, parts.Length > 1 ? parts[1] : "", kv.Key.Substring(bar + 1));
                        if (kv.Value == null && realVal == null) continue;
                        if (kv.Value != null && realVal != null && RegCodec.SameData(realVal, kv.Value.Kind, kv.Value.Data)) continue;
                        n++;
                    }
                    return n + created.Count(c => !IsDeleted(c));
                }
            }
        }

        /// <summary>Every place where the sandbox's view differs from the real registry (used by the self-test).</summary>
        public IEnumerable<string> Describe()
        {
            lock (gate)
            {
                var list = new List<string>();
                foreach (var kv in values)
                {
                    int bar = kv.Key.LastIndexOf('|');
                    var parts = kv.Key.Substring(0, bar).Split(new[] { '\\' }, 2);
                    var hive = parts[0] == "hklm" ? Hive.HKLM : Hive.HKCU;
                    var realVal = real.Get(hive, parts.Length > 1 ? parts[1] : "", kv.Key.Substring(bar + 1));
                    bool same = kv.Value == null ? realVal == null
                              : realVal != null && realVal.Kind == kv.Value.Kind && RegCodec.SameData(realVal, kv.Value.Kind, kv.Value.Data);
                    if (!same) list.Add(kv.Key + " = " + (kv.Value == null ? "<deleted>" : kv.Value.Encoded));
                }
                list.AddRange(created.Where(c => !IsDeleted(c)).Select(c => "created " + c));
                return list;
            }
        }

        // ---- persistence -------------------------------------------------------------
        sealed class Dto
        {
            public List<string[]> Values { get; set; }
            public List<string> Created { get; set; }
            public List<string> Deleted { get; set; }
        }

        void Save()
        {
            if (file == null) return;
            try
            {
                var dto = new Dto
                {
                    Values = values.Select(kv => kv.Value == null
                        ? new[] { kv.Key, "", "" }
                        : new[] { kv.Key, kv.Value.Kind.ToString(), kv.Value.Encoded }).ToList(),
                    Created = created.ToList(),
                    Deleted = deleted.ToList()
                };
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, new JavaScriptSerializer().Serialize(dto), Encoding.UTF8);
            }
            catch { /* sandbox persistence is best effort */ }
        }

        void Load()
        {
            if (file == null || !File.Exists(file)) return;
            try
            {
                var dto = new JavaScriptSerializer().Deserialize<Dto>(File.ReadAllText(file, Encoding.UTF8));
                foreach (var v in dto.Values ?? new List<string[]>())
                {
                    if (v[1] == "") { values[v[0]] = null; continue; }
                    var kind = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), v[1]);
                    values[v[0]] = new RegValue(kind, RegCodec.Decode(kind, v[2]));
                }
                foreach (var c in dto.Created ?? new List<string>()) created.Add(c);
                foreach (var d in dto.Deleted ?? new List<string>()) deleted.Add(d);
            }
            catch { /* start clean */ }
        }
    }

    public static class RegUtil
    {
        /// <summary>The highest key on <paramref name="path"/> that does not exist yet (the one we will create), or null.</summary>
        public static string TopmostMissingKey(IRegistry reg, Hive hive, string path)
        {
            var parts = path.Trim('\\').Split('\\');
            for (int i = 1; i <= parts.Length; i++)
            {
                var p = string.Join("\\", parts, 0, i);
                if (!reg.KeyExists(hive, p)) return p;
            }
            return null;
        }

        static bool TreeHasValues(IRegistry reg, Hive hive, string path)
        {
            if (reg.ValueNames(hive, path).Length > 0) return true;
            return reg.SubKeyNames(hive, path).Any(s => TreeHasValues(reg, hive, path + "\\" + s));
        }

        /// <summary>Deletes a key we created earlier, but only if nothing else has put values in it since.</summary>
        public static void DeleteTreeIfEmpty(IRegistry reg, Hive hive, string path)
        {
            if (string.IsNullOrEmpty(path) || !reg.KeyExists(hive, path)) return;
            if (TreeHasValues(reg, hive, path)) return;
            reg.DeleteKeyTree(hive, path);
        }

        public static string HiveName(Hive h) => h == Hive.HKLM ? "HKLM" : "HKCU";
    }
}
