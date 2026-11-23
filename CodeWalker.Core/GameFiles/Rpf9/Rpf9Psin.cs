using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI binary metadata to XML. Port of psin.py (PSIN files with an LSCH schema: .zmt, .zfc, .zlfc, ...) and
    /// prd0.py (PRD0 resources, the in-memory form of the same data: .ztyp and others).
    ///
    /// PSIN = chain of sections {char[4] tag, u32 BE length incl. header}: PSIN instance data, LSCH struct schemas,
    /// LENM enum schemas, LSNS, LGID, LRSC, CHKS. Instance data is split into blocks, each holding a run of instances
    /// of one type; pointers are u64 with block = v &amp; 0xFFFFF (1-based) and byte offset = v &gt;&gt; 20.
    /// PRD0 keeps the schema and blocks as pointer tables in the CPU half of a resource instead.
    /// Names: psin_names.txt (one name per line), registered under joaat(lowercase) and joaat(as written).
    /// </summary>
    public class Rpf9Psin
    {
        const uint TERM = 0x70707070, ANON = 0x100;
        const ulong P_CPU = 0x50000000;

        struct Field
        {
            public uint Name, Off, Type, Ref;
            public int Kind => (int)(Type & 0xff);
        }
        class StructDef
        {
            public uint Hash, Sig, Size;
            public byte[] Flags;
            public Field[] Fields;
            public int Align;
        }
        struct Block { public long Hash, Off, Size; }      // Hash -1: the untyped first PSIN block
        struct Prim { public string Name; public int Size, Align, N; public char T; }
        class RangeError : Exception { public RangeError() : base("out of range") { } }

        static readonly Dictionary<int, Prim> PRIM = new Dictionary<int, Prim>
        {
            { 0x01, P("bool", 1, 1, '?') }, { 0x10, P("s8", 1, 1, 'b') }, { 0x11, P("u8", 1, 1, 'B') },
            { 0x12, P("s16", 2, 2, 'h') }, { 0x13, P("u16", 2, 2, 'H') }, { 0x14, P("s32", 4, 4, 'i') },
            { 0x15, P("u32", 4, 4, 'I') }, { 0x07, P("u64", 8, 8, 'Q') }, { 0x08, P("hash64", 8, 8, 'Q') },
            { 0x4f, P("hash64", 8, 8, 'Q') }, { 0x20, P("half", 2, 2, 'e') }, { 0x21, P("float", 4, 4, 'f') },
            { 0x30, P("vec2", 8, 8, 'f', 2) }, { 0x33, P("vec3", 16, 16, 'f', 3) }, { 0x31, P("vec4", 16, 16, 'f', 4) },
            { 0x34, P("vec4b", 16, 16, 'f', 4) }, { 0x36, P("mat44", 64, 16, 'f', 16) },
            { 0x4a, P("hash", 4, 4, 'I') }, { 0x4d, P("string", 4, 4, 'I') },
            { 0x60, P("enum8", 1, 1, 'B') }, { 0x63, P("enum16", 2, 2, 'H') }, { 0x62, P("enum32", 4, 4, 'I') },
            { 0x16, P("hash64", 8, 8, 'Q') }, { 0x32, P("vec3p", 12, 4, 'f', 3) },
            { 0x17, P("u64", 8, 8, 'Q') }, { 0x67, P("enum64", 8, 8, 'q') },
        };
        static Prim P(string n, int s, int a, char t, int c = 1) => new Prim { Name = n, Size = s, Align = a, T = t, N = c };
        static bool IsRaw(int k) => k == 0x44 || k == 0x42;                 // 16-byte string / object pointer
        static int RawSize(int k) => k == 0x44 ? 16 : 8;
        static bool IsContainer(int k) => k == 0x53 || k == 0x05 || k == 0x50 || k == 0x52 || k == 0x5c || k == 0x75 || k == 0x71;
        static bool IsEnum(int k) => k == 0x60 || k == 0x62 || k == 0x63 || k == 0x67;

        readonly byte[] b;
        readonly Dictionary<uint, string> names;
        readonly Dictionary<uint, StructDef> structs = new Dictionary<uint, StructDef>();
        readonly List<uint> order = new List<uint>();
        readonly Dictionary<uint, Dictionary<long, uint>> enums = new Dictionary<uint, Dictionary<long, uint>>();
        readonly List<Block> blocks = new List<Block>();
        bool layoutExact = true;
        HashSet<long> seen = new HashSet<long>();
        public List<string> Warnings { get; } = new List<string>();

        Rpf9Psin(byte[] data, Dictionary<uint, string> nameMap)
        {
            b = data;
            names = nameMap ?? new Dictionary<uint, string>();
        }

        // ---------------------------------------------------------------- public API

        public static bool IsPsin(byte[] d) => d != null && d.Length >= 16 && d[0] == 'P' && d[1] == 'S' && d[2] == 'I' && d[3] == 'N';
        public static bool IsPrd0(byte[] cpu) => cpu != null && cpu.Length >= 0x50 && cpu[0x10] == '0' && cpu[0x11] == 'D' && cpu[0x12] == 'R' && cpu[0x13] == 'P';

        /// <summary>the CPU half of an extracted resource (data = CPU part then GPU part)</summary>
        public static byte[] CpuPart(byte[] data, Rpf9FileEntry e)
        {
            if (e == null || !e.IsResource || e.VirtualSize <= 0 || e.VirtualSize >= data.Length) return data;
            var c = new byte[e.VirtualSize];
            Buffer.BlockCopy(data, 0, c, 0, c.Length);
            return c;
        }

        /// <summary>true if the file is PSIN or PRD0 metadata this class can turn into XML</summary>
        public static bool IsMetadata(byte[] data, Rpf9FileEntry e) => IsPsin(data) || (e != null && e.IsResource && IsPrd0(CpuPart(data, e)));

        /// <summary>XML for a PSIN file or a PRD0 resource, or null if it's neither. Throws on a broken file.</summary>
        public static string ToXml(byte[] data, Rpf9FileEntry e = null)
        {
            if (IsPsin(data))
            {
                var ps = new Rpf9Psin(data, Names);
                ps.InitPsin();
                var r = ps.Root(out long at);
                return ps.Document(r, at);
            }
            var cpu = CpuPart(data, e);
            if (IsPrd0(cpu))
            {
                var ps = new Rpf9Psin(cpu, Names);
                var r = ps.InitPrd0(out long at);
                return ps.Document(r, at);
            }
            return null;
        }

        // ---------------------------------------------------------------- names

        static Dictionary<uint, string> names_;
        static readonly object namesLock = new object();
        /// <summary>set before first use to override the psin_names.txt search</summary>
        public static string NamesFile;

        public static Dictionary<uint, string> Names
        {
            get
            {
                lock (namesLock)
                {
                    if (names_ != null) return names_;
                    var d = new Dictionary<uint, string>();
                    var fn = NamesFile ?? FindNamesFile();
                    if (fn != null)
                    {
                        foreach (var line in File.ReadLines(fn, Encoding.UTF8))
                        {
                            var w = line.Trim();
                            if (w.Length == 0) continue;
                            uint h = Joaat(w);
                            if (!d.ContainsKey(h)) d[h] = w;
                            if (w != w.ToLowerInvariant())
                            {
                                h = Joaat(w, false);
                                if (!d.ContainsKey(h)) d[h] = w;
                            }
                        }
                        NamesFile = fn;
                    }
                    names_ = d;
                    return d;
                }
            }
        }

        static string FindNamesFile()
        {
            var c = new List<string>();
            var np = Rpf9Keys.NamesPath;
            if (!string.IsNullOrEmpty(np))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(np));
                c.Add(Path.Combine(dir, "psin_names.txt"));
                var up = Path.GetDirectoryName(dir);
                if (up != null) c.Add(Path.Combine(up, "psin_names.txt"));
            }
            c.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "psin_names.txt"));
            return c.FirstOrDefault(File.Exists);
        }

        /// <summary>RAGE one-at-a-time hash; lower = hash the lowercased string (data values), else as written (schema names)</summary>
        public static uint Joaat(string s, bool lower = true)
        {
            if (lower) s = s.ToLowerInvariant();
            uint h = 0;
            foreach (char ch in s)
            {
                if (ch > 255) continue;                     // latin1, 'ignore'
                h += ch;
                h += h << 10;
                h ^= h >> 6;
            }
            h += h << 3;
            h ^= h >> 11;
            return h + (h << 15);
        }

        string Name(uint h) => names.TryGetValue(h, out var n) && !string.IsNullOrEmpty(n) ? n : "hash_" + h.ToString("X8");
        static readonly Regex TagRx = new Regex(@"^[A-Za-z_][\w.-]*$", RegexOptions.Compiled);
        string Tag(uint h) => names.TryGetValue(h, out var n) && !string.IsNullOrEmpty(n) && TagRx.IsMatch(n) ? n : "hash_" + h.ToString("X8");

        // ---------------------------------------------------------------- byte access

        void Chk(long at, long n) { if (at < 0 || n < 0 || at + n > b.LongLength) throw new RangeError(); }
        byte U8(long at) { Chk(at, 1); return b[at]; }
        ushort U16(long at) { Chk(at, 2); return BitConverter.ToUInt16(b, (int)at); }
        uint U32(long at) { Chk(at, 4); return BitConverter.ToUInt32(b, (int)at); }
        ulong U64(long at) { Chk(at, 8); return BitConverter.ToUInt64(b, (int)at); }
        long S64(long at) { Chk(at, 8); return BitConverter.ToInt64(b, (int)at); }
        float F32(long at) { Chk(at, 4); return BitConverter.ToSingle(b, (int)at); }
        uint BE32(long at) { Chk(at, 4); return (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]); }
        byte[] Slice(long lo, long hi)
        {
            lo = Math.Max(0, Math.Min(lo, b.LongLength));
            hi = Math.Max(lo, Math.Min(hi, b.LongLength));
            var r = new byte[hi - lo];
            Buffer.BlockCopy(b, (int)lo, r, 0, r.Length);
            return r;
        }
        string Utf8(long lo, long hi) => Encoding.UTF8.GetString(Slice(lo, hi));
        string CStr(long lo, long hi)
        {
            lo = Math.Max(0, Math.Min(lo, b.LongLength));
            hi = Math.Max(lo, Math.Min(hi, b.LongLength));
            int z = hi > lo ? Array.IndexOf(b, (byte)0, (int)lo, (int)(hi - lo)) : -1;
            return Utf8(lo, z >= 0 ? z : hi);
        }

        static string Hex(byte[] a)
        {
            var sb = new StringBuilder(a.Length * 2);
            foreach (var x in a) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
        static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        static string Pad(int ind) => new string(' ', ind * 2);
        static string Inv(long v) => v.ToString(CultureInfo.InvariantCulture);
        static string Inv(ulong v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>Python's '%.6g'</summary>
        static string Fmtf(double x)
        {
            if (double.IsNaN(x)) return "nan";
            if (double.IsPositiveInfinity(x)) return "inf";
            if (double.IsNegativeInfinity(x)) return "-inf";
            if (x == 0) return BitConverter.DoubleToInt64Bits(x) < 0 ? "-0" : "0";
            return G6(x);
        }

        /// <summary>exact 6-significant-digit rounding (half to even on the exact binary value, like C printf);
        /// .NET Framework's "G6" rounds a 15-digit intermediate and breaks ties away from zero</summary>
        static string G6(double x)
        {
            bool neg = x < 0;
            if (neg) x = -x;
            long bits = BitConverter.DoubleToInt64Bits(x);
            int e2 = (int)((bits >> 52) & 0x7ff);
            long man = bits & 0xfffffffffffffL;
            if (e2 == 0) e2 = 1; else man |= 1L << 52;
            e2 -= 1075;                                         // x = man * 2^e2
            BigInteger num = man, den = BigInteger.One;
            if (e2 >= 0) num <<= e2; else den <<= -e2;
            int d = (int)Math.Floor(Math.Log10(x));             // 10^d <= x < 10^(d+1), fixed up below
            BigInteger q, r, div;
            var lo = BigInteger.Pow(10, 5);
            var hi = BigInteger.Pow(10, 6);
            while (true)
            {
                BigInteger n = num, dv = den;                   // x / 10^(d-5)
                if (d - 5 >= 0) dv *= BigInteger.Pow(10, d - 5); else n *= BigInteger.Pow(10, 5 - d);
                q = BigInteger.DivRem(n, dv, out r);
                div = dv;
                if (q >= hi) { d++; continue; }
                if (q < lo) { d--; continue; }
                break;
            }
            int c = (r * 2).CompareTo(div);
            if (c > 0 || (c == 0 && !q.IsEven)) q += 1;
            if (q >= hi) { q /= 10; d++; }
            string dg = q.ToString(CultureInfo.InvariantCulture);   // 6 digits
            string s;
            if (d < -4 || d >= 6)
            {
                string m = dg.Substring(1).TrimEnd('0');
                s = dg.Substring(0, 1) + (m.Length > 0 ? "." + m : "") + "e" + (d < 0 ? "-" : "+") + Math.Abs(d).ToString("00");
            }
            else if (d >= 0)
            {
                string f = dg.Substring(d + 1).TrimEnd('0');
                s = dg.Substring(0, d + 1) + (f.Length > 0 ? "." + f : "");
            }
            else s = "0." + new string('0', -d - 1) + dg.TrimEnd('0');
            return neg ? "-" + s : s;
        }
        static double Half(ushort h)
        {
            int s = h >> 15, e = (h >> 10) & 0x1f, m = h & 0x3ff;
            double v;
            if (e == 0) v = m * Math.Pow(2, -24);
            else if (e == 31) v = m == 0 ? double.PositiveInfinity : double.NaN;
            else v = (1 + m / 1024.0) * Math.Pow(2, e - 15);
            return s != 0 ? -v : v;
        }

        /// <summary>integer primitive as text (decimal, signed or unsigned per its format)</summary>
        string IntStr(Prim p, long at)
        {
            switch (p.T)
            {
                case 'b': return Inv((sbyte)U8(at));
                case 'B': return Inv(U8(at));
                case 'h': return Inv((short)U16(at));
                case 'H': return Inv(U16(at));
                case 'i': return Inv((int)U32(at));
                case 'I': return Inv(U32(at));
                case 'q': return Inv(S64(at));
                default: return Inv(U64(at));
            }
        }
        long IntVal(Prim p, long at)
        {
            switch (p.T)
            {
                case 'b': return (sbyte)U8(at);
                case 'B': return U8(at);
                case 'h': return (short)U16(at);
                case 'H': return U16(at);
                case 'i': return (int)U32(at);
                case 'I': return U32(at);
                case 'q': return S64(at);
                default: return (long)U64(at);
            }
        }

        // ---------------------------------------------------------------- PSIN container

        static Dictionary<string, KeyValuePair<long, long>> Sections(byte[] b)
        {
            var o = new Dictionary<string, KeyValuePair<long, long>>();
            long p = 0;
            while (p + 8 <= b.LongLength)
            {
                bool upper = false, ok = true;
                for (int i = 0; i < 4; i++)
                {
                    byte c = b[p + i];
                    if (c >= 'A' && c <= 'Z') upper = true;
                    else if (!(c >= '0' && c <= '9')) { ok = false; break; }
                }
                long ln = (uint)(b[p + 4] << 24 | b[p + 5] << 16 | b[p + 6] << 8 | b[p + 7]);
                if (!ok || !upper || ln < 8 || p + ln > b.LongLength) break;
                o[Encoding.ASCII.GetString(b, (int)p, 4)] = new KeyValuePair<long, long>(p, ln);
                p += ln;
            }
            return o;
        }

        bool Run(long at, byte v, int n)
        {
            if (at < 0 || at + n > b.LongLength) return false;
            for (int i = 0; i < n; i++) if (b[at + i] != v) return false;
            return true;
        }

        void InitPsin()
        {
            var s = Sections(b);
            if (!s.ContainsKey("PSIN") || !s.ContainsKey("LSCH"))
                throw new InvalidDataException("not an LSCH-format PSIN (sections: " + string.Join(" ", s.Keys) + ")");
            ParseLsch(s["LSCH"].Key, s["LSCH"].Value);
            if (s.ContainsKey("LENM")) ParseLenm(s["LENM"].Key, s["LENM"].Value);
            long p = s["PSIN"].Key, ln = s["PSIN"].Value;
            long blob = U32(p + 12);
            long q = p + 16;
            var ents = new List<KeyValuePair<uint, uint>>();
            while (q + 16 <= p + ln)
            {
                uint h = U32(q + 8), sz = U32(q + 12);
                q += 16;
                if (h == TERM) break;
                ents.Add(new KeyValuePair<uint, uint>(h, sz));
            }
            blocks.Add(new Block { Hash = -1, Off = q, Size = blob });
            q = (q + blob + 15) & ~15L;
            foreach (var en in ents)
            {
                q = (q + 15) & ~15L;
                blocks.Add(new Block { Hash = en.Key, Off = q, Size = en.Value });
                q += en.Value;
            }
            layoutExact = ((q + 15) & ~15L) == p + ln;
        }

        void ParseLsch(long p, long ln)
        {
            long end = p + ln, q = p + 8;
            var heads = new List<StructDef>();
            var counts = new List<int>();
            while (q + 32 <= end && Run(q + 16, 0xaa, 8))
            {
                heads.Add(new StructDef { Hash = U32(q), Sig = U32(q + 4), Flags = Slice(q + 8, q + 16), Size = U32(q + 24) });
                counts.Add(U16(q + 30));
                q += 32;
            }
            for (int i = 0; i < heads.Count; i++)
            {
                var s = heads[i];
                s.Align = s.Flags[1] != 0 ? 1 << Math.Min((int)s.Flags[1], 4) : 4;
                s.Fields = new Field[counts[i]];
                for (int k = 0; k < counts[i]; k++, q += 16)
                    s.Fields[k] = new Field { Name = U32(q), Off = U32(q + 4), Type = U32(q + 8), Ref = U32(q + 12) };
                if (!structs.ContainsKey(s.Hash)) structs[s.Hash] = s;
                order.Add(s.Hash);
            }
        }

        /// <summary>enum headers (24 B: u32 hash, u32 sig, 8 x 0xEE, u32 count, u32 0), then the values of each
        /// in the same order (16 B: s64 value, u32 nameHash, u32 0)</summary>
        void ParseLenm(long p, long ln)
        {
            long end = p + ln, q = p + 8;
            var heads = new List<KeyValuePair<uint, uint>>();
            while (q + 24 <= end && Run(q + 8, 0xee, 8))
            {
                heads.Add(new KeyValuePair<uint, uint>(U32(q), U32(q + 16)));
                q += 24;
            }
            foreach (var hd in heads)
            {
                if (q + 16L * hd.Value > end) break;
                var vals = new Dictionary<long, uint>();
                for (uint i = 0; i < hd.Value; i++, q += 16) vals[S64(q)] = U32(q + 8);
                if (!enums.ContainsKey(hd.Key)) enums[hd.Key] = vals;
            }
        }

        // ---------------------------------------------------------------- PRD0 container

        long CPtr(long o)
        {
            ulong v = U64(o);
            return v >= P_CPU && v < P_CPU + (ulong)b.LongLength ? (long)(v - P_CPU) : -1;
        }

        /// <summary>CPU half: +0x20 u64 root block, +0x28 structs[], +0x30 enums[], +0x38 blocks[],
        /// +0x48 u16 nstructs, nenums, nblocks. Struct 32 B (fields by pointer), enum 24 B, block 16 B {hash, size, ptr}.</summary>
        StructDef InitPrd0(out long rootAt)
        {
            int nst = U16(0x48), nenum = U16(0x4a), nblk = U16(0x4c);
            long sp = CPtr(0x28), ep = CPtr(0x30), bp = CPtr(0x38);
            if (sp < 0 || bp < 0) throw new InvalidDataException("missing struct or block table");
            for (int i = 0; i < nst; i++)
            {
                long q = sp + 32L * i;
                var s = new StructDef { Hash = U32(q), Sig = U32(q + 4), Flags = Slice(q + 8, q + 16), Size = U32(q + 24) };
                if (s.Flags.Length < 8) throw new RangeError();
                long fp = CPtr(q + 16);
                int nf = U16(q + 30);
                s.Align = s.Flags[1] != 0 ? 1 << Math.Min((int)s.Flags[1], 4) : 4;
                s.Fields = new Field[nf != 0 && fp >= 0 ? nf : 0];
                for (int k = 0; k < s.Fields.Length; k++)
                {
                    long f = fp + 16L * k;
                    s.Fields[k] = new Field { Name = U32(f), Off = U32(f + 4), Type = U32(f + 8), Ref = U32(f + 12) };
                }
                if (!structs.ContainsKey(s.Hash)) structs[s.Hash] = s;
                order.Add(s.Hash);
            }
            for (int i = 0; ep >= 0 && i < nenum; i++)
            {
                long q = ep + 24L * i;
                uint h = U32(q);
                long vp = CPtr(q + 8);
                ulong cnt = U64(q + 16);
                var vals = new Dictionary<long, uint>();
                if (vp >= 0)
                {
                    Chk(vp, (long)Math.Min(cnt, (ulong)b.LongLength) * 16);
                    for (ulong k = 0; k < cnt; k++) vals[S64(vp + 16 * (long)k)] = U32(vp + 16 * (long)k + 8);
                }
                if (!enums.ContainsKey(h)) enums[h] = vals;
            }
            for (int i = 0; i < nblk; i++)
            {
                long q = bp + 16L * i;
                long o = CPtr(q + 8);
                blocks.Add(new Block { Hash = U32(q), Off = o >= 0 ? o : 0, Size = U32(q + 4) });
            }
            ulong rb = U64(0x20);
            if (rb < 1 || rb > (ulong)nblk) throw new InvalidDataException("root block " + rb + " of " + nblk);
            var bl = blocks[(int)rb - 1];
            if (!structs.TryGetValue((uint)bl.Hash, out var st)) throw new InvalidDataException("root block type " + ((uint)bl.Hash).ToString("x8") + " has no struct");
            rootAt = bl.Off;
            return st;
        }

        // ---------------------------------------------------------------- type info

        /// <summary>size (-1 = unknown) and alignment of a field type</summary>
        long SizeAlign(int kind, uint rf, Field[] fields, uint tword, out int align)
        {
            if (PRIM.TryGetValue(kind, out var p)) { align = p.Align; return p.Size; }
            if (kind == 0x05)
            {
                if (structs.TryGetValue(rf, out var st)) { align = st.Align; return st.Size; }
                align = 1; return 0;
            }
            if (IsRaw(kind)) { align = 8; return RawSize(kind); }
            if (kind == 0x53)                                   // T[ref] followed by u32 count
            {
                long es = CountedElem(fields, tword, out int ea);
                if (es < 0) { align = 1; return -1; }
                int al = Math.Max(ea, 4);
                long n = ((rf * es + 3) & ~3L) + 4;
                align = al;
                return (n + al - 1) & ~(long)(al - 1);
            }
            if (kind == 0x52 || kind == 0x5c || kind == 0x75) { align = 8; return 16; }
            if (kind == 0x71) { align = 8; return 24; }
            align = 1; return -1;
        }

        /// <summary>element of a 0x53 counted array; a descriptor pointing at itself means plain u32</summary>
        long CountedElem(Field[] fields, uint tword, out int align)
        {
            int idx = (int)(tword >> 16);
            if (fields == null || idx >= fields.Length || fields[idx].Kind == 0x53) { align = 4; return 4; }
            var f = fields[idx];
            return SizeAlign(f.Kind, f.Ref, fields, f.Type, out align);
        }

        bool PairLayout(Field[] fields, int kidx, int vidx, out long voff, out long size, out int al)
        {
            var k = fields[kidx];
            var v = fields[vidx];
            long ks = SizeAlign(k.Kind, k.Ref, fields, k.Type, out int ka);
            long vs = SizeAlign(v.Kind, v.Ref, fields, v.Type, out int va);
            voff = size = 0; al = 1;
            if (ks < 0 || vs < 0) return false;
            voff = (ks + va - 1) & ~(long)(va - 1);
            al = Math.Max(ka, va);
            size = (voff + vs + al - 1) & ~(long)(al - 1);
            return true;
        }

        bool Ptr(ulong v, out long lo, out long hi)
        {
            ulong blk = v & 0xfffff, off = v >> 20;
            lo = hi = 0;
            if (blk == 0 || blk > (ulong)blocks.Count) return false;
            var bl = blocks[(int)blk - 1];
            lo = bl.Off + (long)off;
            hi = bl.Off + bl.Size;
            return true;
        }

        /// <summary>the object a pointer points at (type = target block's type), or null if pv doesn't land on a
        /// string or on a whole struct instance</summary>
        string Deref(ulong pv, string tag, int ind, int depth)
        {
            string pad = Pad(ind);
            if (!Ptr(pv, out long lo, out long hi) || (pv >> 52) != 0) return null;
            int blk = (int)(pv & 0xfffff);
            long off = (long)(pv >> 20);
            long bh = blocks[blk - 1].Hash;
            if (bh == 0x10 || bh == 0x11)                       // char block: C string
                return pad + "<" + tag + ">" + Esc(CStr(lo, hi)) + "</" + tag + ">\n";
            if (bh < 0 || !structs.TryGetValue((uint)bh, out var st) || st.Size == 0 || off % st.Size != 0 || lo + st.Size > hi) return null;
            if (seen.Contains(lo))                              // shared or back pointer: refer, don't repeat
                return pad + "<" + tag + " ptr=\"block" + blk + "+0x" + off.ToString("x") + "\" type=\"" + Esc(Name(st.Hash)) + "\"/>\n";
            seen.Add(lo);
            try
            {
                return StructXml(st, lo, tag, ind, depth + 1, false);
            }
            catch (Exception e) when (e is RangeError || e is IndexOutOfRangeException || e is KeyNotFoundException || e is ArgumentException)
            {
                Warnings.Add("pointer 0x" + pv.ToString("x") + " -> " + Name(st.Hash) + ": " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>kind 0x44: last byte 0x80|len -> up to 15 chars inline; else u64 pointer, u16 length, u16 capacity</summary>
        string Sso(long at)
        {
            byte last = U8(at + 15);
            if ((last & 0x80) != 0)
            {
                int n = last & 0x7f;
                return n <= 15 ? Utf8(at, at + n) : null;
            }
            ulong pv = U64(at);
            int len = U16(at + 8), cap = U16(at + 10);
            if (pv == 0 && len == 0) return "";
            if (!Ptr(pv, out long lo, out long hi) || len > cap || lo + len > hi) return null;
            return Utf8(lo, lo + len);
        }

        // ---------------------------------------------------------------- XML

        string Value(Field[] fields, int kind, uint tword, uint rf, long at, string tag, int ind, int depth)
        {
            string pad = Pad(ind);
            if (depth > 64) return pad + "<" + tag + " error=\"recursion\"/>\n";
            if (kind == 0x07)                                   // struct pointer; a plain u64 if it doesn't resolve
            {
                ulong pv = U64(at);
                if (pv > 1)
                {
                    var d = Deref(pv, tag, ind, depth);
                    if (d != null) return d;
                }
            }
            if (PRIM.TryGetValue(kind, out var p))
            {
                if (kind == 0x4a || kind == 0x4d)
                {
                    uint v = U32(at);
                    return pad + "<" + tag + ">" + (v != 0 ? Esc(Name(v)) : "") + "</" + tag + ">\n";
                }
                if (kind == 0x08 || kind == 0x4f || kind == 0x16 || kind == 0x17)
                    return pad + "<" + tag + ">0x" + U64(at).ToString("X16") + "</" + tag + ">\n";
                if (IsEnum(kind))
                {
                    long x = IntVal(p, at);
                    int bits = p.Size * 8;
                    if (enums.TryGetValue(rf, out var vals))
                    {
                        if (vals.TryGetValue(x, out uint nh) || (bits < 64 && vals.TryGetValue(x - (1L << bits), out nh)))
                            return pad + "<" + tag + ">" + Esc(Name(nh)) + "</" + tag + ">\n";
                    }
                    return pad + "<" + tag + " value=\"" + Inv(x) + "\"/>\n";
                }
                if (kind == 0x01) return pad + "<" + tag + " value=\"" + (U8(at) != 0 ? "true" : "false") + "\"/>\n";
                if (p.T == 'f' || p.T == 'e')
                {
                    var fs = new string[p.N];
                    for (int i = 0; i < p.N; i++) fs[i] = Fmtf(p.T == 'f' ? F32(at + 4 * i) : Half(U16(at)));
                    if (fs.Length == 1) return pad + "<" + tag + " value=\"" + fs[0] + "\"/>\n";
                    if (fs.Length <= 4)
                    {
                        var sb = new StringBuilder(pad + "<" + tag);
                        for (int i = 0; i < fs.Length; i++) sb.Append(' ').Append("xyzw"[i]).Append("=\"").Append(fs[i]).Append('"');
                        return sb.Append("/>\n").ToString();
                    }
                    return pad + "<" + tag + " content=\"float_array\">" + string.Join(" ", fs) + "</" + tag + ">\n";
                }
                return pad + "<" + tag + " value=\"" + IntStr(p, at) + "\"/>\n";
            }
            if (kind == 0x05)
            {
                if (!structs.TryGetValue(rf, out var st)) return pad + "<" + tag + " type=\"" + Esc(Name(rf)) + "\" error=\"unknown struct\"/>\n";
                return StructXml(st, at, tag, ind, depth + 1, false);
            }
            if (kind == 0x53)
            {
                long es = CountedElem(fields, tword, out _);
                int idx = (int)(tword >> 16);
                int ek; uint et, er;
                if (fields == null || idx >= fields.Length || fields[idx].Kind == 0x53) { ek = 0x15; et = 0x15; er = 0; }
                else { ek = fields[idx].Kind; et = fields[idx].Type; er = fields[idx].Ref; }
                if (es < 0) return pad + "<" + tag + " capacity=\"" + rf + "\" error=\"unknown element size\"/>\n";
                long cnt = U32(at + ((rf * es + 3) & ~3L));
                if (cnt > rf)
                {
                    Warnings.Add("counted array at 0x" + at.ToString("x") + ": count " + cnt + " > capacity " + rf);
                    cnt = rf;
                }
                if (cnt == 0) return pad + "<" + tag + "/>\n";
                return Items(fields, ek, et, er, at, cnt, es, tag, ind, depth);
            }
            if (kind == 0x50)
            {
                var f = fields[tword >> 16];
                long es = SizeAlign(f.Kind, f.Ref, fields, f.Type, out _);
                if (f.Kind == 0x11 || f.Kind == 0x10)
                    return pad + "<" + tag + " content=\"char_array\">" + Hex(Slice(at, at + rf)) + "</" + tag + ">\n";
                if (es < 0) return pad + "<" + tag + " count=\"" + rf + "\" error=\"unknown element size\"/>\n";
                return Items(fields, f.Kind, f.Type, f.Ref, at, rf, es, tag, ind, depth);
            }
            if (kind == 0x52 || kind == 0x5c)
            {
                ulong pv; long cnt;
                if (kind == 0x5c) { cnt = U32(at); pv = U64(at + 8); }
                else { pv = U64(at); cnt = U16(at + 8); }
                if (cnt == 0) return pad + "<" + tag + "/>\n";
                var f = fields[tword >> 16];
                long es = SizeAlign(f.Kind, f.Ref, fields, f.Type, out _);
                if (!Ptr(pv, out long lo, out long hi) || es < 0 || lo + cnt * es > hi)
                {
                    Warnings.Add("bad array ptr 0x" + pv.ToString("x") + " count " + cnt + " at 0x" + at.ToString("x"));
                    return pad + "<" + tag + " count=\"" + cnt + "\" ptr=\"0x" + pv.ToString("X") + "\" error=\"unresolved\"/>\n";
                }
                return Items(fields, f.Kind, f.Type, f.Ref, lo, cnt, es, tag, ind, depth);
            }
            if (kind == 0x71)
            {
                ulong pv = U64(at + 8);
                long cnt = U16(at + 16);
                if (cnt == 0) return pad + "<" + tag + "/>\n";
                bool okp = Ptr(pv, out long lo, out long hi);
                bool okl = PairLayout(fields, (int)(rf & 0xffff), (int)(rf >> 16), out long voff, out long psz, out _);
                if (!okp || !okl || lo + cnt * psz > hi)
                {
                    Warnings.Add("bad sortedmap ptr 0x" + pv.ToString("x") + " count " + cnt + " at 0x" + at.ToString("x"));
                    return pad + "<" + tag + " count=\"" + cnt + "\" error=\"unresolved\"/>\n";
                }
                var slots = new List<long>();
                for (long i = 0; i < cnt; i++) slots.Add(lo + i * psz);
                return MapItems(fields, rf, slots, voff, tag, ind, depth);
            }
            if (kind == 0x75)
            {
                long groups = U32(at), cnt = U32(at + 4);
                ulong pv = U64(at + 8);
                if (cnt == 0 || groups == 0) return pad + "<" + tag + "/>\n";
                bool okp = Ptr(pv, out long lo, out long hi);
                bool okl = PairLayout(fields, (int)(rf & 0xffff), (int)(rf >> 16), out long voff, out long psz, out int al);
                if (!okp || !okl)
                {
                    Warnings.Add("bad hashmap ptr 0x" + pv.ToString("x") + " at 0x" + at.ToString("x"));
                    return pad + "<" + tag + " count=\"" + cnt + "\" error=\"unresolved\"/>\n";
                }
                long first = (16 + al - 1) & ~(long)(al - 1);
                long gsize = (first + 16 * psz + al - 1) & ~(long)(al - 1);
                var slots = new List<long>();
                for (long g = 0; g < groups; g++)
                {
                    long go = lo + g * gsize;
                    if (go + gsize > hi) break;
                    var ctrl = Slice(go, go + 16);
                    for (int i = 0; i < ctrl.Length; i++) if (ctrl[i] < 0x80) slots.Add(go + first + i * psz);
                }
                if (slots.Count != cnt) Warnings.Add("hashmap at 0x" + at.ToString("x") + ": " + slots.Count + " occupied slots, header says " + cnt);
                return MapItems(fields, rf, slots, voff, tag, ind, depth);
            }
            if (kind == 0x42)                                   // pointer to one object; type = the target block's type
            {
                ulong pv = U64(at);
                if (pv == 0) return pad + "<" + tag + " ptr=\"null\"/>\n";
                var d = Deref(pv, tag, ind, depth);
                if (d != null) return d;
                return pad + "<" + tag + " ptr=\"block" + (pv & 0xfffff) + "+0x" + (pv >> 20).ToString("x") + "\"/>\n";
            }
            if (kind == 0x44)                                   // 16-byte string, small-string optimised
            {
                var s = Sso(at);
                if (s != null) return pad + "<" + tag + ">" + Esc(s) + "</" + tag + ">\n";
            }
            int rawn = IsRaw(kind) ? RawSize(kind) : 8;
            return pad + "<" + tag + " kind=\"0x" + kind.ToString("x2") + "\" raw=\"" + Hex(Slice(at, at + rawn)) + "\"/>\n";
        }

        string Items(Field[] fields, int ek, uint et, uint er, long at, long cnt, long es, string tag, int ind, int depth)
        {
            string pad = Pad(ind);
            var sb = new StringBuilder();
            if (ek == 0x4a || ek == 0x4d)
            {
                sb.Append(pad).Append('<').Append(tag).Append(">\n");
                for (long i = 0; i < cnt; i++)
                {
                    uint v = U32(at + i * es);
                    sb.Append(pad).Append("  <Item>").Append(v != 0 ? Esc(Name(v)) : "").Append("</Item>\n");
                }
                return sb.Append(pad).Append("</").Append(tag).Append(">\n").ToString();
            }
            if (PRIM.TryGetValue(ek, out var p) && !IsEnum(ek) && "fBbHhIi".IndexOf(p.T) >= 0 && p.N == 1)
            {
                var vs = new string[cnt];
                for (long i = 0; i < cnt; i++) vs[i] = p.T == 'f' ? Fmtf(F32(at + i * es)) : IntStr(p, at + i * es);
                return pad + "<" + tag + " content=\"" + p.Name + "_array\">" + string.Join(" ", vs) + "</" + tag + ">\n";
            }
            sb.Append(pad).Append('<').Append(tag).Append(">\n");
            for (long i = 0; i < cnt; i++) sb.Append(Value(fields, ek, et, er, at + i * es, "Item", ind + 1, depth + 1));
            return sb.Append(pad).Append("</").Append(tag).Append(">\n").ToString();
        }

        string MapItems(Field[] fields, uint rf, List<long> slots, long voff, string tag, int ind, int depth)
        {
            string pad = Pad(ind);
            var k = fields[rf & 0xffff];
            var v = fields[rf >> 16];
            int kk = k.Kind;
            bool simpleKey = kk == 0x4a || kk == 0x4d || kk == 0x08 || kk == 0x4f || kk == 0x14 || kk == 0x15 || kk == 0x13 || kk == 0x11 || kk == 0x62;
            var sb = new StringBuilder();
            sb.Append(pad).Append('<').Append(tag).Append(">\n");
            foreach (var so in slots)
            {
                if (simpleKey)
                {
                    string key = (kk == 0x4a || kk == 0x4d) ? Name(U32(so)) : IntStr(PRIM[kk], so);
                    var vx = Value(fields, v.Kind, v.Type, v.Ref, so + voff, "Item", ind + 1, depth + 1);
                    int i = vx.IndexOf("<Item", StringComparison.Ordinal);
                    if (i >= 0) vx = vx.Substring(0, i + 5) + " key=\"" + Esc(key) + "\"" + vx.Substring(i + 5);
                    sb.Append(vx);
                }
                else
                {
                    sb.Append(pad).Append("  <Item>\n");
                    sb.Append(Value(fields, kk, k.Type, k.Ref, so, "Key", ind + 2, depth + 1));
                    sb.Append(Value(fields, v.Kind, v.Type, v.Ref, so + voff, "Value", ind + 2, depth + 1));
                    sb.Append(pad).Append("  </Item>\n");
                }
            }
            return sb.Append(pad).Append("</").Append(tag).Append(">\n").ToString();
        }

        string StructXml(StructDef st, long at, string tag, int ind, int depth, bool root)
        {
            string pad = Pad(ind);
            string attrs = root ? "" : " type=\"" + Esc(Name(st.Hash)) + "\"";
            var named = st.Fields.Where(f => f.Name != ANON).OrderBy(f => f.Off).ToArray();
            if (named.Length == 0) return pad + "<" + tag + attrs + "/>\n";
            var sb = new StringBuilder();
            sb.Append(pad).Append('<').Append(tag).Append(attrs).Append(">\n");
            for (int i = 0; i < named.Length; i++)
            {
                var f = named[i];
                int kind = f.Kind;
                string ft = Tag(f.Name);
                if (!PRIM.ContainsKey(kind) && !IsContainer(kind) && !IsRaw(kind))
                {
                    long end = i + 1 < named.Length ? named[i + 1].Off : st.Size;
                    var raw = Slice(at + f.Off, at + end);
                    if (kind == 0x65)                           // bitset (ref = enum naming the bits, usually absent)
                    {
                        enums.TryGetValue(f.Ref, out var bn);
                        var on = new List<string>();
                        for (int j = 0; j < raw.Length; j++)
                            for (int q = 0; q < 8; q++)
                                if ((raw[j] >> q & 1) != 0)
                                {
                                    long x = j * 8 + q;
                                    on.Add(bn != null && bn.TryGetValue(x, out uint nh) ? Name(nh) : Inv(x));
                                }
                        sb.Append(pad).Append("  <").Append(ft).Append(" content=\"bitset\" bits=\"").Append(Esc(string.Join(" ", on))).Append("\"/>\n");
                    }
                    else sb.Append(pad).Append("  <").Append(ft).Append(" kind=\"0x").Append(kind.ToString("x2")).Append("\" raw=\"").Append(Hex(raw)).Append("\"/>\n");
                    continue;
                }
                try
                {
                    sb.Append(Value(st.Fields, kind, f.Type, f.Ref, at + f.Off, ft, ind + 1, depth));
                }
                catch (RangeError)
                {
                    sb.Append(pad).Append("  <").Append(ft).Append(" error=\"out of range\"/>\n");
                }
                catch (IndexOutOfRangeException)
                {
                    sb.Append(pad).Append("  <").Append(ft).Append(" error=\"bad element descriptor\"/>\n");
                }
            }
            return sb.Append(pad).Append("</").Append(tag).Append(">\n").ToString();
        }

        string Document(StructDef st, long at)
        {
            seen = new HashSet<long> { at };
            var body = StructXml(st, at, Tag(st.Hash), 0, 0, true);
            var head = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            if (!layoutExact) head.Append("<!-- warning: block layout did not match section length -->\n");
            foreach (var w in Warnings.Take(50)) head.Append("<!-- warning: ").Append(Esc(w).Replace("--", "- -")).Append(" -->\n");
            return head.Append(body).ToString();
        }

        // ---------------------------------------------------------------- root detection (PSIN)

        /// <summary>which byte ranges are pointed at by some container / pointer field</summary>
        void Walk(Field[] fields, int kind, uint tword, uint rf, long at, List<KeyValuePair<long, long>> refs, int depth)
        {
            if (depth > 64) return;
            if (kind == 0x05)
            {
                if (structs.TryGetValue(rf, out var st))
                    foreach (var f in st.Fields)
                        if (f.Name != ANON) Walk(st.Fields, f.Kind, f.Type, f.Ref, at + f.Off, refs, depth + 1);
            }
            else if (kind == 0x07 || kind == 0x42)              // u64 slots are often untyped object pointers
            {
                ulong v = U64(at);
                if (v != 1 && Ptr(v, out long lo, out long hi) && lo < hi) refs.Add(new KeyValuePair<long, long>(lo, lo + 1));
            }
            else if (kind == 0x50)
            {
                var f = fields[tword >> 16];
                int ek = f.Kind;
                long es = SizeAlign(ek, f.Ref, fields, f.Type, out _);
                if (es > 0 && (ek == 0x05 || ek == 0x50 || ek == 0x52 || ek == 0x5c || ek == 0x71 || ek == 0x75 || ek == 0x07 || ek == 0x42))
                    for (long i = 0; i < rf; i++) Walk(fields, ek, f.Type, f.Ref, at + i * es, refs, depth + 1);
            }
            else if (kind == 0x52 || kind == 0x5c)
            {
                ulong pv; long cnt;
                if (kind == 0x5c) { cnt = U32(at); pv = U64(at + 8); }
                else { pv = U64(at); cnt = U16(at + 8); }
                var f = fields[tword >> 16];
                int ek = f.Kind;
                long es = SizeAlign(ek, f.Ref, fields, f.Type, out _);
                if (cnt != 0 && Ptr(pv, out long lo, out long hi) && es > 0)
                {
                    refs.Add(new KeyValuePair<long, long>(lo, lo + Math.Max(cnt * es, 1)));
                    // the untyped block 1 and pointer arrays are not walked as instances: follow them here
                    if (((pv & 0xfffff) == 1 || ek == 0x07 || ek == 0x42) && lo + cnt * es <= hi)
                        for (long i = 0; i < cnt; i++) Walk(fields, ek, f.Type, f.Ref, lo + i * es, refs, depth + 1);
                }
            }
            else if (kind == 0x71)
            {
                ulong pv = U64(at + 8);
                long cnt = U16(at + 16);
                if (cnt != 0 && Ptr(pv, out long lo, out _))
                {
                    bool okl = PairLayout(fields, (int)(rf & 0xffff), (int)(rf >> 16), out _, out long psz, out _);
                    refs.Add(new KeyValuePair<long, long>(lo, lo + (okl ? cnt * psz : 1)));
                }
            }
            else if (kind == 0x75)
            {
                long cnt = U32(at + 4);
                ulong pv = U64(at + 8);
                if (cnt != 0 && Ptr(pv, out long lo, out long hi)) refs.Add(new KeyValuePair<long, long>(lo, hi));
            }
        }

        /// <summary>
        /// The root object is the one nothing points at. Every instance in a struct-typed block is scanned for pointers.
        /// If nothing points at the start of block 1 (the untyped block) the root lives there and its type is a struct with
        /// no block of its own; otherwise block 1 is storage for primitive arrays and the root is the first unreferenced
        /// instance in a struct block (schema order first).
        /// </summary>
        StructDef Root(out long rootAt)
        {
            var refs = new List<KeyValuePair<long, long>>();
            var typed = new List<KeyValuePair<StructDef, long>>();
            foreach (var bl in blocks.Skip(1))
            {
                if (bl.Hash < 0 || !structs.TryGetValue((uint)bl.Hash, out var st) || st.Size == 0) continue;
                for (long k = 0; k < bl.Size / st.Size; k++)
                {
                    long at = bl.Off + k * st.Size;
                    typed.Add(new KeyValuePair<StructDef, long>(st, at));
                    try { Walk(null, 0x05, 0x05, st.Hash, at, refs, 0); }
                    catch (Exception e) when (e is RangeError || e is IndexOutOfRangeException || e is NullReferenceException) { }
                }
            }
            var merged = new List<long[]>();
            foreach (var r in refs.OrderBy(r => r.Key).ThenBy(r => r.Value))
            {
                if (merged.Count > 0 && r.Key <= merged[merged.Count - 1][1]) merged[merged.Count - 1][1] = Math.Max(merged[merged.Count - 1][1], r.Value);
                else merged.Add(new[] { r.Key, r.Value });
            }
            var los = merged.Select(m => m[0]).ToArray();
            Func<long, bool> covered = a =>
            {
                int i = Array.BinarySearch(los, a);
                if (i < 0) i = ~i - 1;
                else while (i + 1 < los.Length && los[i + 1] == a) i++;     // bisect_right
                return i >= 0 && a < merged[i][1];
            };

            var schemaRef = new HashSet<uint>();
            foreach (var st in structs.Values)
                foreach (var f in st.Fields)
                    if (f.Kind == 0x05 && f.Ref != st.Hash) schemaRef.Add(f.Ref);
            var blocked = new HashSet<long>(blocks.Skip(1).Select(x => x.Hash));
            long blobAt = blocks[0].Off, blob = blocks[0].Size;
            bool blobFree = blob != 0 && !covered(blobAt);
            var free = order.Where(h => !blocked.Contains(h) && structs[h].Flags[0] == 0).Select(h => structs[h]).ToList();
            Func<long, long> al = n => (n + 15) & ~15L;

            var rank = new Dictionary<uint, int>();
            for (int i = 0; i < order.Count; i++) rank[order[i]] = i;
            typed = typed.OrderBy(t => rank.TryGetValue(t.Key.Hash, out int r) ? r : 1 << 30).ThenBy(t => t.Value).ToList();

            StructDef res = null;
            long resAt = 0;
            Func<Func<StructDef, bool>, bool> inBlob = ok =>
            {
                if (!blobFree) return false;
                foreach (var s in free) if (ok(s)) { res = s; resAt = blobAt; return true; }
                return false;
            };
            Func<bool, bool> inBlock = strict =>
            {
                foreach (var t in typed)
                    if (t.Key.Flags[0] == 0 && !covered(t.Value) && !(strict && schemaRef.Contains(t.Key.Hash))) { res = t.Key; resAt = t.Value; return true; }
                return false;
            };
            if (inBlob(s => al(s.Size) == al(blob) && !schemaRef.Contains(s.Hash))
                || inBlock(true)
                || inBlob(s => al(s.Size) == al(blob))
                || inBlob(s => s.Size <= blob && !schemaRef.Contains(s.Hash))
                || inBlock(false))
            {
                rootAt = resAt;
                return res;
            }
            var where = new Dictionary<long, long>();
            foreach (var bl in blocks.Skip(1)) where[bl.Hash] = bl.Off;
            foreach (var h in order)
                if (where.ContainsKey(h) && structs[h].Flags[0] == 0) { rootAt = where[h]; return structs[h]; }
            rootAt = blobAt;
            return structs[order[0]];
        }
    }
}
