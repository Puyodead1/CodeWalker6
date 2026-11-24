using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI PSO to XML: PSIN files with a PSCH schema (sections PSIN PMAP PSCH PSIG STDN STNS CHKS), the GTA V PSO layout,
    /// big endian, GTA V schema type codes. Port of tools\pso9.py. Differences from GTA V:
    /// <list type="bullet">
    /// <item>pointers are 8 bytes, w0 w1 (u32 BE): block id = w0 &amp; 0xFFF (1-based), byte offset = (w0 &gt;&gt; 20) | (w1 &gt;&gt; 24) &lt;&lt; 12</item>
    /// <item>array subtype 10: { u32 count, u32 capacity, pointer }; subtypes 1, 2, 4: inline, count = ref &gt;&gt; 16</item>
    /// <item>string subtype 3: 16 bytes, inline text if the last byte has 0x80 (length in the low bits), else a pointer;
    /// subtype 14: 64-bit hash</item>
    /// <item>enum subtype 1: 32-bit; type 0x24: quaternion; 0x13: vec4</item>
    /// </list>
    /// STNS / STDN (hash tables) aren't needed to decode. Names come from the GTA VI hash list (<see cref="Rpf9Strings"/>).
    /// </summary>
    public class Rpf9Pso
    {
        const uint ARRAYINFO = 0x100;

        struct Entry { public uint Name, Ref; public byte Type, Sub; public ushort Off; }
        class StructInfo { public uint Hash; public int Length; public Entry[] Entries; }
        struct Block { public uint Name; public int Offset, Length; }

        readonly byte[] b;
        int psinBase;
        uint root;
        Block[] blocks;
        readonly Dictionary<uint, StructInfo> structs = new Dictionary<uint, StructInfo>();
        readonly Dictionary<uint, Dictionary<int, uint>> enums = new Dictionary<uint, Dictionary<int, uint>>();
        readonly StringBuilder sb = new StringBuilder();
        int depth;

        Rpf9Pso(byte[] data) { b = data; }

        static Dictionary<string, KeyValuePair<int, int>> Sections(byte[] b)
        {
            var s = new Dictionary<string, KeyValuePair<int, int>>();
            int o = 0;
            while (o + 8 <= b.Length)
            {
                var tag = Encoding.ASCII.GetString(b, o, 4);
                int ln = (int)BE32(b, o + 4);
                if (ln < 8 || o + ln > b.Length) break;
                s[tag] = new KeyValuePair<int, int>(o, ln);
                o += ln;
            }
            return s;
        }

        /// <summary>true for a PSIN file with a PSCH schema (the GTA V style PSO layout)</summary>
        public static bool IsPso(byte[] d)
        {
            if (!Rpf9Psin.IsPsin(d)) return false;
            var s = Sections(d);
            return s.ContainsKey("PSCH") && s.ContainsKey("PMAP") && !s.ContainsKey("LSCH");
        }

        public static string ToXml(byte[] data)
        {
            if (!IsPso(data)) return null;
            Rpf9Strings.EnsureLoaded();
            var p = new Rpf9Pso(data);
            p.Init();
            return p.Document();
        }

        // ---------------------------------------------------------------- reading

        static uint BE32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        uint U32(int o) => BE32(b, o);
        int S32(int o) => (int)BE32(b, o);
        ushort U16(int o) => (ushort)(b[o] << 8 | b[o + 1]);
        short S16(int o) => (short)U16(o);
        ulong U64(int o) => (ulong)U32(o) << 32 | U32(o + 4);
        float F32(int o) => BitConverter.ToSingle(BitConverter.GetBytes(U32(o)), 0);
        double F64(int o) => BitConverter.Int64BitsToDouble((long)U64(o));

        void Init()
        {
            var s = Sections(b);
            psinBase = s["PSIN"].Key;
            int o = s["PMAP"].Key + 8;
            root = U32(o);
            int cnt = S16(o + 4);
            o += 8;
            if (cnt <= 0) { cnt = S16(o); o += 8; }
            blocks = new Block[cnt];
            for (int i = 0; i < cnt; i++, o += 16)
                blocks[i] = new Block { Name = U32(o), Offset = S32(o + 4), Length = S32(o + 12) };

            o = s["PSCH"].Key;
            int n = S32(o + 8);
            for (int i = 0; i < n; i++)
            {
                uint h = U32(o + 12 + 8 * i);
                int p = o + S32(o + 16 + 8 * i);
                uint x = U32(p);
                if (x >> 24 == 0)
                {
                    var st = new StructInfo { Hash = h, Length = S32(p + 4), Entries = new Entry[x & 0xFFFF] };
                    for (int k = 0; k < st.Entries.Length; k++)
                    {
                        int q = p + 12 + 12 * k;
                        st.Entries[k] = new Entry { Name = U32(q), Type = b[q + 4], Sub = b[q + 5], Off = U16(q + 6), Ref = U32(q + 8) };
                    }
                    structs[h] = st;
                }
                else if (x >> 24 == 1)
                {
                    var en = new Dictionary<int, uint>();
                    for (int k = 0; k < (int)(x & 0xFFFFFF); k++)
                    {
                        int q = p + 4 + 8 * k;
                        en[S32(q + 4)] = U32(q);
                    }
                    enums[h] = en;
                }
                else throw new InvalidDataException($"PSO: schema entry {h:X8} has type {x >> 24}");
            }
        }

        Block? GetBlock(int id) => id >= 1 && id <= blocks.Length ? blocks[id - 1] : (Block?)null;

        /// <summary>8-byte pointer at o -> absolute offset of the target, or -1 for null. blockName gets the target block's type.</summary>
        int Ptr(int o, out uint blockName)
        {
            blockName = 0;
            uint w0 = U32(o), w1 = U32(o + 4);
            if (w0 == 0) return -1;
            int id = (int)(w0 & 0xFFF);
            int off = (int)(w0 >> 20 | (w1 >> 24) << 12);
            var bl = GetBlock(id);
            if (bl == null || off >= bl.Value.Length || ((w0 >> 12) & 0xFF) != 0)
                throw new InvalidDataException($"PSO: bad pointer {w0:X8} {w1:X8} at 0x{o:X}");
            blockName = bl.Value.Name;
            return psinBase + bl.Value.Offset + off;
        }

        string CStr(int o)
        {
            int e = o;
            while (e < b.Length && b[e] != 0) e++;
            return Encoding.UTF8.GetString(b, o, e - o);
        }

        // ---------------------------------------------------------------- names

        static string Nm(uint h) => Rpf9Strings.Get(h) ?? "hash_" + h.ToString("X8");
        static string HashText(uint h) => h == 0 ? "" : Nm(h);
        static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        static string F(float f) => FloatUtil.ToString(f);

        // ---------------------------------------------------------------- XML

        string Document()
        {
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            var bl = GetBlock((int)root) ?? throw new InvalidDataException("PSO: root block " + root + " missing");
            Node(bl.Name, psinBase + bl.Offset, Nm(bl.Name), "");
            return sb.ToString();
        }

        void Pad() => sb.Append(' ', depth * 2);
        void Val(string tag, string v) { Pad(); sb.Append('<').Append(tag).Append(" value=\"").Append(v).Append("\" />\n"); }
        void Text(string tag, string s)
        {
            Pad();
            if (string.IsNullOrEmpty(s)) sb.Append('<').Append(tag).Append(" />\n");
            else sb.Append('<').Append(tag).Append('>').Append(Esc(s)).Append("</").Append(tag).Append(">\n");
        }
        void Empty(string tag) { Pad(); sb.Append('<').Append(tag).Append(" />\n"); }

        void Node(uint structHash, int o, string tag, string attrs)
        {
            if (!structs.TryGetValue(structHash, out var st))
            {
                Pad(); sb.Append('<').Append(tag).Append(attrs).Append(" /><!-- unknown struct ").Append(Nm(structHash)).Append(" -->\n");
                return;
            }
            Pad(); sb.Append('<').Append(tag).Append(attrs);
            int mark = sb.Length;
            sb.Append(">\n");
            int start = sb.Length;
            depth++;
            foreach (var e in st.Entries)
                if (e.Name != ARRAYINFO) Field(st, e, o + e.Off, Nm(e.Name));
            depth--;
            if (sb.Length == start) { sb.Length = mark; sb.Append(" />\n"); }
            else { Pad(); sb.Append("</").Append(tag.Split(' ')[0]).Append(">\n"); }
        }

        string StringValue(Entry e, int o)
        {
            switch (e.Sub)
            {
                case 0: { int n = (int)(e.Ref >> 16); int z = 0; while (z < n && b[o + z] != 0) z++; return Encoding.UTF8.GetString(b, o, z); }
                case 1:
                case 2: { int p = Ptr(o, out _); return p < 0 ? "" : CStr(p); }
                case 3:
                    {
                        byte last = b[o + 15];
                        if ((last & 0x80) != 0) return Encoding.UTF8.GetString(b, o, last & 0x7F);
                        int p = Ptr(o, out _);
                        return p < 0 ? "" : CStr(p);
                    }
                case 7:
                case 8: return HashText(U32(o));
                case 14:
                    {
                        ulong h = U64(o);
                        if (h == 0) return "";
                        return Rpf9Strings.Get((uint)(h >> 32)) ?? Rpf9Strings.Get((uint)h) ?? "0x" + h.ToString("X16");
                    }
            }
            throw new InvalidDataException($"PSO: string subtype {e.Sub}");
        }

        void Field(StructInfo st, Entry e, int o, string tag)
        {
            var c = CultureInfo.InvariantCulture;
            switch (e.Type)
            {
                case 0x00: Val(tag, b[o] != 0 ? "true" : "false"); break;
                case 0x01: Val(tag, ((sbyte)b[o]).ToString(c)); break;
                case 0x02: Val(tag, b[o].ToString(c)); break;
                case 0x03: Val(tag, S16(o).ToString(c)); break;
                case 0x04: Val(tag, U16(o).ToString(c)); break;
                case 0x05: Val(tag, S32(o).ToString(c)); break;
                case 0x06: Val(tag, e.Sub == 1 ? "0x" + U32(o).ToString("X8") : S32(o).ToString(c)); break;
                case 0x07: Val(tag, F(F32(o))); break;
                case 0x08: Pad(); sb.Append('<').Append(tag).Append(" x=\"").Append(F(F32(o))).Append("\" y=\"").Append(F(F32(o + 4))).Append("\" />\n"); break;
                case 0x09:
                case 0x14:
                case 0x15:
                    Pad(); sb.Append('<').Append(tag).Append(" x=\"").Append(F(F32(o))).Append("\" y=\"").Append(F(F32(o + 4))).Append("\" z=\"").Append(F(F32(o + 8))).Append("\" />\n");
                    break;
                case 0x0A:
                case 0x13:
                case 0x24:
                    Pad(); sb.Append('<').Append(tag).Append(" x=\"").Append(F(F32(o))).Append("\" y=\"").Append(F(F32(o + 4))).Append("\" z=\"").Append(F(F32(o + 8))).Append("\" w=\"").Append(F(F32(o + 12))).Append("\" />\n");
                    break;
                case 0x0B: Text(tag, StringValue(e, o)); break;
                case 0x0C:
                    if (e.Sub == 0) Node(e.Ref, o, tag, "");
                    else if (e.Sub == 3 || e.Sub == 4)
                    {
                        int p = Ptr(o, out uint bn);
                        if (p < 0) Empty(tag);
                        else Node(structs.ContainsKey(bn) ? bn : e.Ref, p, tag, " type=\"" + Esc(Nm(bn)) + "\"");
                    }
                    else throw new InvalidDataException($"PSO: struct subtype {e.Sub}");
                    break;
                case 0x0D: Array(st, e, o, tag); break;
                case 0x0E:
                    {
                        int v = e.Sub == 2 ? b[o] : S32(o);
                        enums.TryGetValue(e.Ref, out var en);
                        Text(tag, en != null && en.TryGetValue(v, out uint vh) ? Nm(vh) : v.ToString(c));
                    }
                    break;
                case 0x0F:
                    {
                        uint v = e.Sub == 0 ? U32(o) : e.Sub == 1 ? U16(o) : b[o];
                        uint fi = e.Ref & 0xFFF;
                        Dictionary<int, uint> en = null;
                        if (fi != 0xFFF && fi < st.Entries.Length && st.Entries[fi].Name == ARRAYINFO) enums.TryGetValue(st.Entries[fi].Ref, out en);
                        if (en != null) Text(tag, string.Join(" ", en.Where(kv => kv.Key >= 0 && kv.Key < 32 && ((v >> kv.Key) & 1) != 0).OrderBy(kv => kv.Key).Select(kv => Nm(kv.Value))));
                        else Val(tag, v.ToString(c));
                    }
                    break;
                case 0x10: Map(st, e, o, tag); break;
                case 0x1E: Val(tag, F(Half(U16(o)))); break;
                case 0x1C:
                case 0x1F: Val(tag, ((long)U64(o)).ToString(c)); break;
                case 0x1D:
                case 0x20: Val(tag, U64(o).ToString(c)); break;
                case 0x21: Val(tag, F64(o).ToString("R", c)); break;
                default:
                    Pad(); sb.Append('<').Append(tag).Append(" /><!-- unknown type 0x").Append(e.Type.ToString("X2")).Append('/').Append(e.Sub).Append(" -->\n");
                    break;
            }
        }

        int ItemSize(Entry a)
        {
            switch (a.Type)
            {
                case 0x0C:
                    if (a.Sub == 3 || a.Sub == 4) return 8;
                    return structs.TryGetValue(a.Ref, out var s) ? s.Length : throw new InvalidDataException("PSO: array item struct " + Nm(a.Ref) + " missing");
                case 0x0B:
                    switch (a.Sub) { case 0: return (int)(a.Ref >> 16); case 1: case 2: case 14: return 8; case 3: return 16; case 7: case 8: return 4; }
                    break;
                case 0x0E: return a.Sub == 2 ? 1 : 4;
                case 0x0F: return a.Sub == 0 ? 4 : a.Sub == 1 ? 2 : 1;
                case 0x0D: return 16;
                case 0x00: case 0x01: case 0x02: return 1;
                case 0x03: case 0x04: case 0x1E: return 2;
                case 0x05: case 0x06: case 0x07: return 4;
                case 0x08: case 0x1C: case 0x1D: case 0x1F: case 0x20: case 0x21: return 8;
                case 0x09: case 0x0A: case 0x13: case 0x14: case 0x15: case 0x24: return 16;
            }
            throw new InvalidDataException($"PSO: array of type 0x{a.Type:X2}/{a.Sub}");
        }

        void Array(StructInfo st, Entry e, int o, string tag)
        {
            int ai = (int)(e.Ref & 0xFFFF);
            if (ai >= st.Entries.Length) ai = (int)(e.Ref & 0xFFF);
            var a = st.Entries[ai];
            int cnt, data;
            switch (e.Sub)
            {
                case 0: cnt = U16(o + 8); data = cnt > 0 ? Ptr(o, out _) : -1; break;           // ptr, u16 count, u16 capacity, u32
                case 10: cnt = (int)U32(o); data = cnt > 0 ? Ptr(o + 8, out _) : -1; break;     // u32 count, u32 capacity, ptr
                case 1: case 2: case 4: case 129: cnt = (int)(e.Ref >> 16); data = o; break;  // inline
                default: throw new InvalidDataException($"PSO: array subtype {e.Sub} on {Nm(e.Name)}");
            }
            if (cnt <= 0 || data < 0) { Empty(tag); return; }
            int sz = ItemSize(a);
            if (data + (long)cnt * sz > b.Length) throw new InvalidDataException($"PSO: array {Nm(e.Name)} runs past the end");

            string simple = null;
            switch (a.Type)
            {
                case 0x00: simple = "boolean"; break;
                case 0x01: simple = "sbyte"; break;
                case 0x02: simple = "byte"; break;
                case 0x03: simple = "short"; break;
                case 0x04: simple = "ushort"; break;
                case 0x05:
                case 0x06: simple = "int"; break;
                case 0x07: simple = "float"; break;
            }
            var c = CultureInfo.InvariantCulture;
            if (simple != null)
            {
                Pad(); sb.Append('<').Append(tag).Append(" content=\"").Append(simple).Append("_array\">\n");
                for (int i = 0; i < cnt; i++)
                {
                    if (i % 16 == 0) { if (i > 0) sb.Append('\n'); Pad(); sb.Append("  "); } else sb.Append(' ');
                    int q = data + i * sz;
                    switch (a.Type)
                    {
                        case 0x00: sb.Append(b[q] != 0 ? "true" : "false"); break;
                        case 0x01: sb.Append(((sbyte)b[q]).ToString(c)); break;
                        case 0x02: sb.Append(b[q].ToString(c)); break;
                        case 0x03: sb.Append(S16(q).ToString(c)); break;
                        case 0x04: sb.Append(U16(q).ToString(c)); break;
                        case 0x05: sb.Append(S32(q).ToString(c)); break;
                        case 0x06: sb.Append(a.Sub == 1 ? "0x" + U32(q).ToString("X8") : U32(q).ToString(c)); break;
                        case 0x07: sb.Append(F(F32(q))); break;
                    }
                }
                sb.Append('\n'); Pad(); sb.Append("</").Append(tag).Append(">\n");
                return;
            }
            Pad(); sb.Append('<').Append(tag);
            if (a.Type == 0x0C && a.Sub == 0) sb.Append(" itemType=\"").Append(Esc(Nm(a.Ref))).Append('"');
            sb.Append(">\n");
            depth++;
            for (int i = 0; i < cnt; i++) Field(st, a, data + i * sz, "Item");
            depth--;
            Pad(); sb.Append("</").Append(tag).Append(">\n");
        }

        void Map(StructInfo st, Entry e, int o, string tag)
        {
            if (e.Sub != 1) throw new InvalidDataException($"PSO: map subtype {e.Sub}");
            // atBinaryMap: u32 0x01000000, u32, then { ptr, u16 count, u16 capacity, u32 } of key/item pairs
            int cnt = U16(o + 16);
            uint pairName = 0;
            int p = cnt > 0 ? Ptr(o + 8, out pairName) : -1;
            if (p < 0) { Empty(tag); return; }
            if (!structs.TryGetValue(pairName, out var pair)) throw new InvalidDataException("PSO: map pair struct " + Nm(pairName) + " missing");
            var ents = pair.Entries.Where(x => x.Name != ARRAYINFO).OrderBy(x => x.Off).ToArray();
            if (ents.Length < 2) throw new InvalidDataException("PSO: map pair struct " + Nm(pairName) + " has " + ents.Length + " fields");
            Entry ke = ents[0], ie = ents[1];
            Pad(); sb.Append('<').Append(tag).Append(">\n");
            depth++;
            for (int i = 0; i < cnt; i++)
            {
                int q = p + i * pair.Length;
                string k = ke.Type == 0x0B ? StringValue(ke, q + ke.Off) : U32(q + ke.Off).ToString(CultureInfo.InvariantCulture);
                if (ke.Type == 0x0E && enums.TryGetValue(ke.Ref, out var en) && en.TryGetValue(S32(q + ke.Off), out uint kh)) k = Nm(kh);
                int before = sb.Length;
                Field(pair, ie, q + ie.Off, "Item");
                int at = sb.ToString(before, sb.Length - before).IndexOf("<Item", StringComparison.Ordinal);
                if (at >= 0) sb.Insert(before + at + 5, " key=\"" + Esc(k) + "\"");
            }
            depth--;
            Pad(); sb.Append("</").Append(tag).Append(">\n");
        }

        static float Half(ushort h)
        {
            int s = (h >> 15) & 1, ex = (h >> 10) & 0x1F, m = h & 0x3FF;
            float v = ex == 0 ? m / 1024f * (float)Math.Pow(2, -14) : ex == 31 ? (m == 0 ? float.PositiveInfinity : float.NaN) : (1 + m / 1024f) * (float)Math.Pow(2, ex - 15);
            return s != 0 ? -v : v;
        }
    }
}
