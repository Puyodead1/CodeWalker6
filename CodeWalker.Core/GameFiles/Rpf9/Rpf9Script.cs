using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI compiled script (.zscript, ext 89): header, imports, types and a disassembly. Port of tools/zscript.py;
    /// format: data/script_format.md. A RAGE resource (pointers 0x50000000 + offset) whose 0x180-byte header is the
    /// scrProgram. Opcodes are u16, op = raw * 0x3315 (0..529); operand byte i after the opcode is stored
    /// obfuscated: plain = ((b ^ K[i &amp; 7]) * 0x6d) &amp; 0xff.
    /// </summary>
    public class Rpf9Script
    {
        static readonly byte[] K = { 0x85, 0x94, 0xA7, 0xB6, 0xC1, 0xD0, 0xE3, 0xF2 };
        const int OPMUL = 0x3315, NOPS = 530;
        const ulong EXE_BASE_KEY_VA = 0x141D109B0;
        static readonly byte[][] DECODE = Enumerable.Range(0, 8)
            .Select(i => Enumerable.Range(0, 256).Select(b => (byte)(((b ^ K[i]) * 0x6d) & 0xff)).ToArray()).ToArray();

        // operand byte sizes per opcode ('-' = none); from the VM handlers of sub_143DE2550 (same table as zscript.py)
        const string FIELDS_TXT = @"
- 1 - - - - - 1,1 1,1 1 1,1 1 1 - - - - - - - 1,1 1,1 1 1 - - - - - - - 1,1 1 - - - - - - - - - - - - 1 4 4 8
1,1,1,1 8,8 1 4 - 1 1 1 1 1 1 1 1 1 1 1,1 1 1 1 1 1 1 1 1,1 1,1 1,1 1,1 1,1 1,1 1,1 1,1,1 1 1 1 1 1 1 1 1 1,1 1 1,1
1,1 1,1 1,1 1,1 1,1 1,1,1 1,1 1 1 1 1 1 1 1,1 1,1 1,1 1 1 1 1 1 1,1 1 1 1 1 1 1,1 1,1 1,1 1,1 1,1 1,1 1,1 1,1,1 1,4
4,1 - 1 1 1 1,1 1,4 1,2 1,4,1 1,2,1 1,1,1 1,1 4,1,1 2,2 4,1 1,1 1 - - 1 1 - 1 4 4,1 - 4 - - 4 1 4,4 4 1 4 4 1 4 4 4
4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 4 - - - - - - - - - - - - - - - - - - - - - - - - - - - -
- - - - - - - - - 1,1 8 8,8 - - - - - - 2 - - - - - 1 - - - - - - - - - 1 1 1 1 1 1 - 1 1 1 1 1 1 - - - - - - - - -
- - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - - -
- - - - - - - - - - - - - - - - - - - - - - - - - - - - 2 1,1 2 1 1,1,1,1 - 1,1 4,1 4 1 2 2 4 - - - - - - - - - - -
- 1 2 2,2 2 2 2,2 1,2,1 1,4,2 4,2 2 2,1 2 2 2 2 2 2 2,2 2 2,2 2,2 2,2 2,2 2,2 2,2 2,2,2 2 2 2 2 2 2 2,2 2 1,4 1,4
1,4 1,4 1,4 1,4 1,4,2 1,4 2 2 2 2 2 2,2 2 2 2 2 2 2,2 1,4 1,4 1,4 1,4 1,4,2 1,4 1,4 1,4 1,4 1,4 1,4 1,4,2 1,4 1,4,2
- 2,2 2,2 2,2 2,2 2 2 - 1 - 1 1 1 - - - - - - - - - - - - - - - - 8,4 1 1 1 1 1 1 1 1 1 1 1 1,4 1,4 1,1 1,4 8,8 -
1,1,1,1,1 1,1,1,1,1 1,1,1,1,1,1 1,1,1,1,1,1 1,1 1,1 1,1 1,1 - 1,1,1,1 1,1,1,1,1,1 1,1,1,1,1 1,1,1,1,1 1,1,1,1 -
1,1,1,1 1,1,1,1 - - - 1,1,1,1 1,1,1,1 1,1,1,1,1
";
        static readonly int[][] FIELDS = FIELDS_TXT.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t == "-" ? new int[0] : t.Split(',').Select(int.Parse).ToArray()).ToArray();
        static readonly int[] OPLEN = FIELDS.Select(f => f.Sum()).ToArray();

        static readonly Dictionary<int, string> NAMES = BuildNames();
        static Dictionary<int, string> BuildNames()
        {
            var n = new Dictionary<int, string>
            {
                {0,"NOP?"},{1,"LOAD_ARG"},{7,"LOAD_ARG_N"},{9,"STORE_ARG"},{11,"ARG_ADDR"},{12,"LOAD_LOCAL"},
                {20,"LOAD_LOCAL_N"},{21,"LOAD_LOCAL_WIDE"},{22,"LOCAL_ADDR"},{23,"STORE_LOCAL"},{32,"TEE_LOCAL"},
                {43,"PUSH_M1"},{44,"PUSH_F1"},{45,"PUSH_U8"},{46,"PUSH_32"},{47,"PUSH_32B"},{48,"PUSH_64"},{49,"PUSH_U8_U8"},
                {50,"PUSH_128"},{51,"PUSH_POW2"},{52,"PUSH_STATIC"},{53,"DUP"},{55,"PICK"},
                {56,"LOAD_FIELD_I8"},{57,"LOAD_FIELD_U8"},{58,"LOAD_FIELD_I16"},{59,"LOAD_FIELD_U16"},{60,"LOAD_FIELD_32"},
                {61,"LOAD_THIS_FIELD_32"},{62,"LOAD_FIELD_64"},{63,"LOAD_THIS_FIELD_64"},{101,"STORE_FIELD_32"},
                {103,"STORE_FIELD_64"},{115,"STORE_AT_32"},{127,"CALL_IFACE?"},{400,"CALL_IFACE_W?"},{128,"DROP"},
                {131,"ENTER"},{132,"ENTER_ALIGN"},{398,"ENTER_WIDE"},{133,"CALL"},{134,"CALL_S"},
                {135,"VCALL"},{136,"VCALL"},{137,"VCALL"},{399,"VCALL"},{138,"BUILTIN_CALL"},{139,"NATIVE"},
                {140,"CALL_INDIRECT"},{141,"CAST"},{142,"CAST_BUILTIN"},{144,"RET"},{145,"RET_1"},{146,"RET_N"},
                {156,"JMP"},{157,"JMP_S"},{158,"SWITCH"},{370,"LOAD_BUILTIN"},{377,"ZERO_FIELD"},{380,"CONST_DATA"},
                {408,"LOAD_FIELD_64_W"},{429,"GLOBAL_IMP_W?"},{451,"GLOBAL_IMP?"},{505,"RESUME"},
            };
            for (int i = 2; i < 7; i++) n[i] = "LOAD_ARG_" + (i - 2);
            for (int i = 13; i < 20; i++) n[i] = "LOAD_LOCAL_" + (i - 13);
            for (int i = 24; i < 31; i++) n[i] = "STORE_LOCAL_" + (i - 24);
            for (int i = 33; i < 43; i++) n[i] = "PUSH_" + (i - 33);
            for (int i = 159; i < 197; i++) n[i] = "BR_" + i;     // conditional branches; conditions not mapped
            return n;
        }
        static bool IsEnter(int op) => op == 131 || op == 132 || op == 398;
        static bool IsBranch(int op) => op == 156 || op == 157 || (op >= 159 && op < 197);

        public struct Ins { public int Pc, Op; public long[] V; }
        public struct Import { public ulong ModuleId; public uint Name, Globals, TypeStart, TypeEnd; }

        readonly byte[] d;
        public uint NameHash, FileHash, BcVersion, ModuleId, GlobalsTotal, GlobalsOwn, TypesN, CodeSize, BlobSize, EntryA, EntryB;
        public ushort Version;
        public long TypesOff, CodeOff, BlobOff;
        public ulong NativeBase;
        public List<Import> Imports = new List<Import>();

        uint U32(long o) => BitConverter.ToUInt32(d, (int)o);
        long Ptr(long o) { ulong v = BitConverter.ToUInt64(d, (int)o); return v >= 0x50000000 ? (long)(v - 0x50000000) : -1; }

        public Rpf9Script(byte[] data)
        {
            d = data;
            if (d.Length < 0x180) throw new Exception("too small for a scrProgram header");
            NameHash = U32(0x10); Version = BitConverter.ToUInt16(d, 0x18);
            FileHash = U32(0x24); BcVersion = U32(0x110); ModuleId = U32(0x114);
            GlobalsTotal = U32(0x118); GlobalsOwn = U32(0x11C);
            TypesN = U32(0x28); TypesOff = Ptr(0x30);
            CodeOff = Ptr(0xE8); BlobOff = Ptr(0xF0); CodeSize = U32(0xF8); BlobSize = U32(0xFC);
            EntryA = U32(0xBC); EntryB = U32(0xC0);
            if (CodeOff < 0 || CodeOff + CodeSize > d.Length) throw new Exception("code pointer out of range");
            uint n = U32(0x140); long p = Ptr(0x148);
            for (int i = 0; i < n && p >= 0 && p + 24 * (i + 1) <= d.Length; i++)
            {
                long r = p + 24 * i;
                Imports.Add(new Import { ModuleId = BitConverter.ToUInt64(d, (int)r), Name = U32(r + 8), Globals = U32(r + 12),
                    TypeStart = U32(r + 16), TypeEnd = U32(r + 20) });
            }
            uint key = (FileHash >> 12) | (FileHash << 20);
            NativeBase = EXE_BASE_KEY_VA - (key ^ NameHash);
        }

        /// <summary>Linear sweep over the code. Throws on a bad opcode or an overrun.</summary>
        public List<Ins> Decode()
        {
            var list = new List<Ins>();
            long c = CodeOff; int pc = 0, n = (int)CodeSize;
            while (pc < n)
            {
                int op = (BitConverter.ToUInt16(d, (int)(c + pc)) * OPMUL) & 0xffff;
                if (op >= NOPS) throw new Exception($"invalid opcode {op} at 0x{pc:x}");
                var f = FIELDS[op]; var vals = new long[f.Length]; int o = 0;
                for (int k = 0; k < f.Length; k++)
                {
                    ulong v = 0;
                    for (int j = 0; j < f[k]; j++) v |= (ulong)DECODE[(o + j) & 7][d[c + pc + 2 + o + j]] << (8 * j);
                    if (k == 0 && IsBranch(op) && f[k] < 8) { int sh = 64 - 8 * f[k]; vals[k] = (long)(v << sh) >> sh; }
                    else vals[k] = (long)v;
                    o += f[k];
                }
                list.Add(new Ins { Pc = pc, Op = op, V = vals });
                pc += 2 + OPLEN[op];
            }
            if (pc != n) throw new Exception($"overran code end (0x{pc:x}/0x{n:x})");
            return list;
        }

        int[] SwitchTable(long count, long off)
        {
            var t = new int[Math.Max(0, count)];
            for (int i = 0; i < t.Length; i++)
            {
                long a = BlobOff + off + 4 * i;
                t[i] = (BlobOff >= 0 && a + 4 <= d.Length) ? BitConverter.ToInt32(d, (int)a) : int.MinValue;
            }
            return t;
        }

        public HashSet<int> FunctionStarts(List<Ins> ins)
        {
            var s = new HashSet<int> { 0 };
            foreach (var i in ins)
            {
                if (IsEnter(i.Op)) s.Add(i.Pc);
                else if ((i.Op == 133 || i.Op == 134) && i.V[0] == 0) s.Add((int)i.V[1]);
            }
            foreach (var e in new[] { EntryA, EntryB }) if (e != 0xffffffff && e < CodeSize) s.Add((int)e);
            return s;
        }

        static string Num(long x) => (ulong)x > 9 ? "0x" + ((ulong)x).ToString("x") : x.ToString();   // operands are unsigned

        string Fmt(Ins i, Dictionary<uint, string> names, out string mn)
        {
            var v = i.V; int op = i.Op, pc = i.Pc;
            mn = NAMES.TryGetValue(op, out var m) ? m : "op_" + op.ToString("000");
            if (op == 133 || op == 134)
            {
                long imp = v[0], off = v[1];
                if (imp == 0) return $"func_{off:x6}";
                string nm = imp < Imports.Count && names.TryGetValue(Imports[(int)imp].Name, out var s) ? s : "imp" + imp;
                return $"{nm}:{off:x6}";
            }
            if (IsBranch(op)) return $"loc_{pc + 2 + v[0]:x6}";
            if (op == 158)
                return "[" + string.Join(", ", SwitchTable(v[0], v[1]).Select(t => $"loc_{pc + 2 + t:x6}")) + $"] default loc_{pc + 2 + 8:x6}";
            if (op == 139) return $"sub_{NativeBase + (ulong)v[0]:X} argc={v[1]} retc={v[2]}";
            if (op == 131) return $"args={v[0] >> 4} zeroed={v[0] & 15}";
            if (op == 132) return $"args={v[0] >> 4} zeroed={v[0] & 15} align={1L << (int)v[1]}";
            if (op == 398) return $"args={v[0]} zeroed={v[1]} align={1L << (int)v[2]}";
            if (op == 135 || op == 136 || op == 137 || op == 399) return $"argc={v[0]} type=0x{v[1] & 0x7fffffff:x} slot={v[2]}";
            if (op == 138) return $"builtin={v[0]} method={v[1]}";
            if (op == 140) return $"argc={v[0]} retc={v[1]}";
            if (op == 46 || op == 47)
            {
                uint u = (uint)v[0];
                float f = BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
                string g = ((double)f).ToString("G6", System.Globalization.CultureInfo.InvariantCulture).Replace("E", "e");
                if (double.IsPositiveInfinity(f)) g = "inf"; else if (double.IsNegativeInfinity(f)) g = "-inf"; else if (float.IsNaN(f)) g = "nan";
                return $"{(int)u}  ; 0x{u:x} {g}";
            }
            return string.Join(", ", v.Select(Num));
        }

        public static Dictionary<uint, string> NameMap(IEnumerable<string> extra)
        {
            var m = new Dictionary<uint, string>();
            if (extra != null) foreach (var s in extra) { if (!string.IsNullOrEmpty(s)) { uint h = Rpf9Psin.Joaat(s); if (!m.ContainsKey(h)) m[h] = s; } }
            foreach (var s in new[] { "shared", "gamemodes", "native", "headerdata", "scriptbase" }) { uint h = Rpf9Psin.Joaat(s); if (!m.ContainsKey(h)) m[h] = s; }
            return m;
        }

        /// <summary>Text view: header, imports, type counts, functions and disassembly (capped at maxLines).</summary>
        public static string Summary(byte[] data, string name, IEnumerable<string> siblingStems, int maxLines = 400000)
        {
            var s = new Rpf9Script(data);
            var names = NameMap(siblingStems);
            var sb = new StringBuilder();
            sb.AppendLine(name);
            sb.AppendLine($"  name joaat {s.NameHash:x8} ({(names.TryGetValue(s.NameHash, out var nn) ? nn : "?")})  version 0x{s.Version:x}  bytecode 0x{s.BcVersion:x}  module id {s.ModuleId:x8}  file hash {s.FileHash:x8}");
            sb.AppendLine($"  code @0x{s.CodeOff:x} size 0x{s.CodeSize:x}  blob @0x{Math.Max(0, s.BlobOff):x} size 0x{s.BlobSize:x}  globals own 0x{s.GlobalsOwn:x} total 0x{s.GlobalsTotal:x}  native base 0x{s.NativeBase:X}");
            sb.AppendLine($"  entry offsets +0xBC 0x{s.EntryA:x} +0xC0 0x{s.EntryB:x}");
            sb.AppendLine($"  imports ({s.Imports.Count}): module id, name, globals offset, type index range");
            for (int i = 0; i < s.Imports.Count; i++)
            {
                var im = s.Imports[i];
                string nm = im.Name == 0 ? "(self)" : names.TryGetValue(im.Name, out var x) ? x : im.Name.ToString("x8");
                sb.AppendLine($"    [{i}] {im.ModuleId:x8} {nm,-12} globals +0x{im.Globals:x} types [0x{im.TypeStart:x},0x{im.TypeEnd:x})");
            }
            if (s.TypesOff >= 0)
            {
                var kinds = new SortedDictionary<int, int>();
                for (int i = 0; i < s.TypesN && s.TypesOff + 0x88 * (i + 1) <= data.Length; i++)
                {
                    int k = (int)(BitConverter.ToUInt64(data, (int)(s.TypesOff + 0x88 * i + 8)) & 7);
                    kinds[k] = kinds.TryGetValue(k, out var c) ? c + 1 : 1;
                }
                sb.AppendLine($"  types: {s.TypesN} records of 0x88 (by tag kind: {string.Join(", ", kinds.Select(kv => kv.Key + ": " + kv.Value))})");
            }

            List<Ins> ins;
            try { ins = s.Decode(); }
            catch (Exception ex) { sb.AppendLine("  disassembly failed: " + ex.Message); return sb.ToString(); }
            var starts = s.FunctionStarts(ins);
            var natives = new SortedSet<ulong>();
            int unnamed = 0;
            foreach (var i in ins) { if (i.Op == 139) natives.Add(s.NativeBase + (ulong)i.V[0]); if (!NAMES.ContainsKey(i.Op)) unnamed++; }
            sb.AppendLine($"  instructions {ins.Count} ({unnamed} with unnamed opcodes), functions {starts.Count}, distinct natives {natives.Count}");
            sb.AppendLine("  native targets (exe VAs): " + string.Join(" ", natives.Take(200).Select(a => "sub_" + a.ToString("X"))) + (natives.Count > 200 ? " ..." : ""));
            sb.AppendLine();
            sb.AppendLine("Disassembly (conditional branches print as BR_<op>; their conditions are not mapped yet)");

            int lines = 0;
            foreach (var i in ins)
            {
                if (starts.Contains(i.Pc)) { sb.Append('\n').Append($"func_{i.Pc:x6}:").Append('\n'); }
                string a = s.Fmt(i, names, out var mn);
                sb.Append($"  {i.Pc:x6}  {mn,-18} {a}").Append('\n');
                if (++lines >= maxLines)
                {
                    sb.AppendLine($"\n... stopped after {maxLines} instructions of {ins.Count} (tools/zscript.py --dis prints all).");
                    break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>GTA VI fwdbStore binary data (.zdatb, ext 14). Port of tools/zdatb.py; data/script_format.md (".zdatb").</summary>
    public static class Rpf9Zdatb
    {
        public static bool IsZdatb(byte[] d, string nameLower)
        {
            if (nameLower == null || !nameLower.EndsWith(".zdatb") || d.Length < 0x20) return false;
            return BitConverter.ToUInt16(d, 10) < d.Length;
        }

        public static string Summary(byte[] d, string name, bool slots = true)
        {
            var sb = new StringBuilder();
            uint a = BitConverter.ToUInt32(d, 0), size = BitConverter.ToUInt32(d, 4);
            int nblk = BitConverter.ToUInt16(d, 8), first = BitConverter.ToUInt16(d, 10);
            sb.AppendLine($"{name}: {d.Length} bytes (header size field 0x{size:x}), +0x00 0x{a:x}, blocks {nblk}, first block @0x{first:x}");
            int fill = 0; for (int i = 0; i + 16 <= d.Length; i += 16) { bool cd = true; for (int j = 0; j < 16 && cd; j++) cd = d[i + j] == 0xcd; if (cd) fill++; }
            if (fill > 0) sb.AppendLine($"  0xCD fill: {fill} x 16-byte aligned runs");
            long lastEnd = first; int o = first;
            for (int i = 0; i < nblk; i++)
            {
                if (o + 0x10 > d.Length) break;
                uint v = BitConverter.ToUInt32(d, o);
                int nslots = BitConverter.ToUInt16(d, o + 4), hsz = BitConverter.ToUInt16(d, o + 6);
                int nrec = (int)BitConverter.ToUInt32(d, o + 8), body = (int)BitConverter.ToUInt32(d, o + 12);
                int ln = (int)(v & 0xffff), fl = (int)(v >> 16);
                long end = nblk > 1 ? o + ln : d.Length;
                long recsz = nrec > 0 ? (end - (o + body)) / nrec : 0;
                long slotsz = nslots > 0 ? (body - hsz) / nslots : 0;
                string extra = "";
                if (nblk > 1 && o + 0x18 <= d.Length) extra = $" items {BitConverter.ToUInt32(d, o + 0x10)} @0x{BitConverter.ToUInt32(d, o + 0x14):x}";
                sb.AppendLine($"  block {i,2} @0x{o:x5} len 0x{ln:x4} flags 0x{fl:x4} slots {nslots,2} x 0x{slotsz:x} (hdr 0x{hsz:x}) records {nrec} x 0x{recsz:x}{extra}");
                if (slots && nslots > 0 && slotsz > 0)
                    for (int k = 0; k < nslots; k++)
                    {
                        long s = o + hsz + k * slotsz;
                        if (s + 16 > d.Length) break;
                        sb.AppendLine($"      slot {k,2} @0x{s:x5} " + BitConverter.ToString(d, (int)s, 16).Replace('-', ' ').ToLowerInvariant());
                    }
                if (recsz >= 8)
                    for (int k = 0; k < nrec; k++)
                    {
                        long r = o + body + k * recsz;
                        if (r + 6 > d.Length) break;
                        uint h = BitConverter.ToUInt32(d, (int)r); int n = BitConverter.ToUInt16(d, (int)r + 4);
                        if (n <= 0 || n >= recsz || r + 6 + n > d.Length) continue;
                        bool ok = true; for (int j = 0; j < n && ok; j++) ok = d[r + 6 + j] >= 32 && d[r + 6 + j] < 127;
                        if (ok) sb.AppendLine($"      record {k}: {h:x8} \"{Encoding.ASCII.GetString(d, (int)r + 6, n)}\"");
                    }
                lastEnd = Math.Max(lastEnd, end);
                if (ln == 0 || nblk == 1) break;
                o += ln;
            }
            if (nblk > 1)
            {
                uint r2 = BitConverter.ToUInt32(d, 0x10);
                sb.AppendLine($"  block area ends @0x{lastEnd:x} (+0x10 = 0x{r2:x}); region @0x{r2:x}..0x{a:x}; tail 0x{a:x}..0x{d.Length:x} ({(d.Length - (long)a) / 8} x 8-byte entries)");
                return sb.ToString();
            }
            var ms = Regex.Matches(Encoding.GetEncoding(28591).GetString(d), @"[A-Za-z_][A-Za-z0-9_ ]{2,}(?=\x00)");
            if (ms.Count > 0)
            {
                sb.AppendLine($"  NUL-terminated strings: {ms.Count}");
                foreach (Match m in ms.Cast<Match>().Take(400)) sb.AppendLine($"    @0x{m.Index:x4} {Rpf9Psin.Joaat(m.Value):x8} {m.Value}");
            }
            return sb.ToString();
        }
    }
}
