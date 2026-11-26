using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI audio metadata (.rel, audio.rpf config pack). Port of tools/audio_rel.py; format: data/audio_rel_format.md.
    /// u32 version, u32 data length, data; A: hash groups; B: bank-path string table (XXTEA, key in the exe);
    /// object index {name hash, data offset} (+ a 12-byte second index for schema 8); fixup lists: hash refs,
    /// bank-string refs (each followed by the u32 wave hash; low 29 bits = AWC stream id), in-chunk object refs,
    /// external objects {hash, type mask} and the fields that index them. Fixup values are file offsets (data + 8).
    /// </summary>
    public class Rpf9Rel
    {
        // type id: name, dat version, schema, state, first encrypted version, XXTEA key VA ('*_common' chunks)
        static readonly Dictionary<int, (string Name, int Version, int Schema, int State, int EncFrom, ulong KeyVa)> TYPES =
            new Dictionary<int, (string, int, int, int, int, ulong)>
        {
            { 0, ("GameObjects", 581, 8, 4, 292, 0x1461D2AE0) },
            { 1, ("Sounds", 166, 7, 4, 91, 0x1465F92A0) },
            { 2, ("Music", 86, 7, 4, 1, 0x146626890) },
            { 3, ("Speech", 18, 3, 5, 11, 0x1461FEEF0) },
            { 4, ("ModularSynth", 20, 7, 4, 14, 0x1466D0A90) },
            { 5, ("AudioConfig", 26, 7, 3, 0, 0) },
            { 6, ("DynamicMixer", 61, 8, 4, 32, 0x1461FE5E0) },
            { 7, ("Categories", 41, 7, 4, 28, 0x1461FE430) },
            { 8, ("Curves", 25, 5, 4, 20, 0x1461FE4D0) },
        };
        static readonly Dictionary<string, int> CHUNKS = new Dictionary<string, int>
        {
            { "sounds", 1 }, { "dynamix", 6 }, { "audioconfig", 5 }, { "speech2", 3 }, { "curves", 8 }, { "categories", 7 },
            { "game", 0 }, { "music", 2 }, { "speechanimal", 3 }, { "speechscanner", 3 }, { "speechsfx", 3 }, { "optamp", 4 },
        };
        static readonly Dictionary<int, string> SOUND_TYPES = new Dictionary<int, string> { { 12, "SimpleSound" }, { 49, "SoundSet" } };

        public int Version, TypeId, Schema, State;
        public string TypeName;
        public bool Encrypted;
        public byte[] Raw, Data;
        public List<(uint First, uint Count)> Groups = new List<(uint, uint)>();
        public int GroupHashes;
        public List<string> Strings = new List<string>();
        public List<(uint Hash, uint Offset)> Index = new List<(uint, uint)>();
        public int Index2;
        public List<uint> HashFixups, StringFixups, ObjectFixups = new List<uint>(), ExtFixups = new List<uint>();
        public List<(uint Hash, uint Mask)> ExtObjects = new List<(uint, uint)>();
        public long End;

        uint U(byte[] b, long o) => BitConverter.ToUInt32(b, (int)o);

        /// <summary>A .rel by name, or an unnamed file whose version is a metadata version and whose sections parse.</summary>
        public static bool IsRel(byte[] data, string nameLower)
        {
            if (data == null || data.Length < 32) return false;
            bool named = (nameLower ?? "").EndsWith(".rel");
            int ver = BitConverter.ToInt32(data, 0);
            if (!named && !TYPES.Values.Any(t => t.Version == ver)) return false;
            if (8L + BitConverter.ToUInt32(data, 4) > data.Length) return false;
            if (named) return true;
            try { return new Rpf9Rel(data, nameLower).End == data.Length; } catch { return false; }
        }

        public Rpf9Rel(byte[] b, string name)
        {
            Raw = b;
            Version = (int)U(b, 0);
            string chunk = null;
            if (!string.IsNullOrEmpty(name))
            {
                var bn = Path.GetFileName(name.Replace('/', '\\')).ToLowerInvariant();
                int dot = bn.IndexOf('.');
                if (dot >= 0) bn = bn.Substring(0, dot);
                chunk = bn.EndsWith("_common") ? bn.Substring(0, bn.Length - 7) : bn;
            }
            int tid = -1;
            if (chunk != null && CHUNKS.TryGetValue(chunk, out int c)) tid = c;
            else foreach (var kv in TYPES) if (kv.Value.Version == Version) { tid = kv.Key; break; }
            if (!TYPES.TryGetValue(tid, out var ty)) throw new InvalidDataException("not an audio .rel (version " + Version + ")");
            TypeId = tid; TypeName = ty.Name; Schema = ty.Schema; State = ty.State;

            uint dl = U(b, 4);
            Data = new byte[dl];
            Buffer.BlockCopy(b, 8, Data, 0, (int)dl);
            long p = 8 + dl;
            // A: u32 len (incl. count), u32 n groups, n x (u32 first, u32 count), u32 hashes[]
            uint la = U(b, p), na = U(b, p + 4);
            for (int i = 0; i < na; i++) Groups.Add((U(b, p + 8 + 8 * i), U(b, p + 12 + 8 * i)));
            GroupHashes = (int)((la - 4 - 8 * na) / 4);
            p += 4 + la;
            // B: u32 len (incl. count), u32 n, then n u32 offsets + strings (XXTEA when version >= first encrypted)
            uint lb = U(b, p), nb = U(b, p + 4);
            var body = new byte[lb - 4];
            Buffer.BlockCopy(b, (int)p + 8, body, 0, body.Length);
            Encrypted = ty.EncFrom != 0 && Version >= ty.EncFrom && nb > 0;
            if (Encrypted) XxteaDecrypt(body, Key(tid, ty.KeyVa));
            int heap = (int)(4 * nb);
            for (int i = 0; i < nb; i++)
            {
                int s = heap + (int)U(body, 4 * i), e = s;
                while (e < body.Length && body[e] != 0) e++;
                Strings.Add(Encoding.GetEncoding(28591).GetString(body, s, e - s));
            }
            p += 4 + lb;
            uint n = U(b, p);
            for (int i = 0; i < n; i++) Index.Add((U(b, p + 4 + 8 * i), U(b, p + 8 + 8 * i)));
            p += 4 + 8 * n;
            if (Schema >= 8) { Index2 = (int)U(b, p); p += 4 + 12L * Index2; }
            List<uint> Lst()
            {
                uint k = U(b, p);
                var l = new List<uint>((int)k);
                for (int i = 0; i < k; i++) l.Add(U(b, p + 4 + 4 * i));
                p += 4 + 4L * k;
                return l;
            }
            HashFixups = Lst();
            StringFixups = Lst();
            if (Schema >= 4) ObjectFixups = Lst();
            if (Schema >= 7)
            {
                uint k = U(b, p);
                for (int i = 0; i < k; i++) ExtObjects.Add((U(b, p + 4 + 8 * i), U(b, p + 8 + 8 * i)));
                p += 4 + 8L * k;
                ExtFixups = Lst();
            }
            End = p;
        }

        // ------------------------------------------------------------------ XXTEA (exe 0x145541720)

        static readonly Dictionary<ulong, uint[]> keys = new Dictionary<ulong, uint[]>();

        static uint[] Key(int typeId, ulong va)
        {
            lock (keys)
            {
                if (keys.TryGetValue(va, out var k)) return k;
                if (Rpf9Keys.RelKeys.TryGetValue(typeId, out k)) return keys[va] = k; // from the key file
                if (!Rpf9Keys.Loaded || string.IsNullOrEmpty(Rpf9Keys.ExePath)) throw new InvalidOperationException("bank strings are encrypted and neither the key file nor the game exe is loaded");
                var kb = new Rpf9Pe(Rpf9Keys.ExePath).Read(va, 16);
                k = new uint[4];
                for (int i = 0; i < 4; i++) k[i] = BitConverter.ToUInt32(kb, 4 * i);
                keys[va] = k;
                return k;
            }
        }

        static void XxteaDecrypt(byte[] buf, uint[] key)
        {
            const uint DELTA = 0x9E3779B9;
            int n = buf.Length / 4;
            if (n < 2) return;
            var v = new uint[n];
            Buffer.BlockCopy(buf, 0, v, 0, 4 * n);
            int rounds = 6 + 52 / n;
            uint s = (uint)(rounds * DELTA), y = v[0], z;
            while (rounds-- > 0)
            {
                uint e = (s >> 2) & 3;
                for (int p = n - 1; p >= 0; p--)
                {
                    z = v[p > 0 ? p - 1 : n - 1];
                    uint mx = (((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4))) ^ ((s ^ y) + (key[(p & 3) ^ e] ^ z));
                    y = v[p] -= mx;
                }
                s -= DELTA;
            }
            Buffer.BlockCopy(v, 0, buf, 0, 4 * n);
        }

        // ------------------------------------------------------------------ names

        static Dictionary<uint, string> relNames;

        /// <summary>namelists\rel_names.tsv (next to the loaded rpf9_names.tsv): hash TAB name TAB source</summary>
        static Dictionary<uint, string> RelNames()
        {
            if (relNames != null) return relNames;
            var d = new Dictionary<uint, string>();
            try
            {
                var dir = string.IsNullOrEmpty(Rpf9Keys.NamesPath) ? null : Path.GetDirectoryName(Rpf9Keys.NamesPath);
                var f = dir == null ? null : Path.Combine(dir, "rel_names.tsv");
                if (f != null && File.Exists(f))
                    foreach (var line in File.ReadLines(f))
                    {
                        var c = line.Split('\t');
                        if (c.Length >= 2 && c[0] != "hash" && uint.TryParse(c[0], System.Globalization.NumberStyles.HexNumber, null, out uint h)) d[h] = c[1];
                    }
            }
            catch { }
            return relNames = d;
        }

        static string Nm(uint h) => RelNames().TryGetValue(h, out var s) ? s : (Rpf9Strings.Get(h) ?? "0x" + h.ToString("X8"));

        static uint Joaat(string s)
        {
            uint h = 0;
            foreach (byte c in Encoding.UTF8.GetBytes(s.ToLowerInvariant().Replace('\\', '/')))
            {
                h += c; h += h << 10; h ^= h >> 6;
            }
            h += h << 3; h ^= h >> 11; h += h << 15;
            return h;
        }

        // ------------------------------------------------------------------ text view

        const int MaxObjectLines = 60000;

        public static string Summary(byte[] data, string name)
        {
            var r = new Rpf9Rel(data, name);
            var sb = new StringBuilder();
            var names = RelNames();
            sb.AppendLine($"{name}: GTA VI audio metadata, {r.TypeName} (type {r.TypeId}) version {r.Version}, schema {r.Schema}, state {r.State}");
            sb.AppendLine($"data {r.Data.Length:N0} bytes; hash groups {r.Groups.Count} ({r.GroupHashes} hashes); bank strings {r.Strings.Count} ({(r.Encrypted ? "XXTEA" : "plain")}); objects {r.Index.Count:N0}; index2 {r.Index2}");
            sb.AppendLine($"fixups: hash {r.HashFixups.Count}, string {r.StringFixups.Count}, object {r.ObjectFixups.Count}, ext objects {r.ExtObjects.Count}, ext refs {r.ExtFixups.Count}; parsed to {r.End:N0} of {data.Length:N0} bytes");
            sb.AppendLine($"named objects {r.Index.Count(i => names.ContainsKey(i.Hash)):N0} of {r.Index.Count:N0} (namelists\\rel_names.tsv{(names.Count == 0 ? " not found" : "")})");
            sb.AppendLine("Format: data\\audio_rel_format.md; full dump: tools\\audio_rel.py dump.");
            sb.AppendLine();

            // objects sorted by offset; size runs to the next one
            var objs = r.Index.OrderBy(i => i.Offset).ToList();
            var offs = objs.Select(o => o.Offset).ToArray();
            sb.AppendLine("Object types:");
            foreach (var g in objs.GroupBy(o => r.Data[o.Offset]).OrderBy(g => g.Key))
                sb.AppendLine($"  type {g.Key,3} {(r.TypeId == 1 && SOUND_TYPES.TryGetValue(g.Key, out var tn) ? tn : ""),-12} {g.Count():N0}");
            sb.AppendLine();

            // per-object wave links (bank string ref + following wave hash) and external refs
            var byHash = new Dictionary<uint, string>();
            foreach (var s in r.Strings) byHash[Joaat(s)] = s;
            var waves = new Dictionary<uint, List<string>>();
            var erefs = new Dictionary<uint, List<string>>();
            int Owner(uint fo)
            {
                long d = (long)fo - 8;
                int i = Array.BinarySearch(offs, (uint)Math.Max(0, d));
                if (i < 0) i = ~i - 1;
                return i;
            }
            foreach (var fo in r.StringFixups)
            {
                long d = (long)fo - 8;
                int i = Owner(fo);
                if (i < 0 || d + 8 > r.Data.Length) continue;
                uint bh = BitConverter.ToUInt32(r.Data, (int)d), w = BitConverter.ToUInt32(r.Data, (int)d + 4);
                string bank = byHash.TryGetValue(bh, out var bs) ? bs : "?" + bh.ToString("x8");
                string wn = names.TryGetValue(w, out var x) ? x : null;
                var key = offs[i];
                if (!waves.TryGetValue(key, out var l)) waves[key] = l = new List<string>();
                l.Add($"{bank} wave {w:x8} (id {w & 0x1FFFFFFF:x8}){(wn != null ? " " + wn : "")}");
            }
            foreach (var fo in r.ExtFixups)
            {
                long d = (long)fo - 8;
                int i = Owner(fo);
                if (i < 0 || d + 4 > r.Data.Length) continue;
                uint v = BitConverter.ToUInt32(r.Data, (int)d);
                string t = v < r.ExtObjects.Count ? Nm(r.ExtObjects[(int)v].Hash) + " [" + MaskNames(r.ExtObjects[(int)v].Mask) + "]" : "#" + v;
                var key = offs[i];
                if (!erefs.TryGetValue(key, out var l)) erefs[key] = l = new List<string>();
                l.Add(t);
            }

            if (r.Strings.Count > 0)
            {
                sb.AppendLine($"Bank paths ({r.Strings.Count}):");
                foreach (var s in r.Strings) sb.AppendLine("  " + s);
                sb.AppendLine();
            }

            if (waves.Count > 0)
            {
                // all of them (the object list below is capped): sound -> bank path, wave hash (low 29 bits = AWC stream id)
                sb.AppendLine($"Wave links ({waves.Sum(w => w.Value.Count):N0}; sound: bank path, wave):");
                foreach (var (h, o) in objs)
                    if (waves.TryGetValue(o, out var wl)) foreach (var w in wl) sb.AppendLine($"  {Nm(h)}: {w}");
                sb.AppendLine();
            }

            sb.AppendLine("Objects (offset, size, type, name; then wave links and external refs):");
            int lines = 0;
            for (int i = 0; i < objs.Count; i++)
            {
                if (lines >= MaxObjectLines) { sb.AppendLine($"... {objs.Count - i:N0} more objects (tools\\audio_rel.py dump lists all)"); break; }
                var (h, o) = objs[i];
                uint size = (i + 1 < objs.Count ? objs[i + 1].Offset : (uint)r.Data.Length) - o;
                int t = r.Data[o];
                string tn = r.TypeId == 1 && SOUND_TYPES.TryGetValue(t, out var stn) ? stn : "type " + t;
                sb.AppendLine($"  {o,9:x8} {size,7} {tn,-12} {Nm(h)}");
                lines++;
                if (waves.TryGetValue(o, out var wl)) foreach (var w in wl) { sb.AppendLine("      wave: " + w); lines++; }
                if (erefs.TryGetValue(o, out var el))
                {
                    sb.AppendLine("      refs: " + string.Join(", ", el.Take(12)) + (el.Count > 12 ? $", ... ({el.Count})" : ""));
                    lines++;
                }
            }
            return sb.ToString();
        }

        static string MaskNames(uint m)
        {
            var l = new List<string>();
            for (int i = 0; i < 32; i++) if (((m >> i) & 1) != 0) l.Add(TYPES.TryGetValue(i, out var t) ? t.Name : i.ToString());
            return string.Join("|", l);
        }
    }
}
