using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI: a text summary of an RPF9 file that has no viewer of its own. Every file gets the TOC facts (extension
    /// id, name hash, resource version, CPU/GPU sizes); resources get a census of their pointers, arrays, strings and
    /// known hashes; the face/compute-graph formats (.zfid, .zcg, .zcgc, .zcps) get their decoded headers.
    /// Layouts are described in data\face_formats.md and data\rpf9_archive.md of the research project.
    /// </summary>
    public static class Rpf9Info
    {
        public const ulong CpuBase = 0x50000000, GpuBase = 0x60000000;

        /// <summary>what the game calls each extension's store, where the exe names it</summary>
        public static readonly Dictionary<string, string> Kinds = new Dictionary<string, string>
        {
            { ".zfid", "facial identity: per-vertex head shape deltas (FacialIdentityStore)" },
            { ".zcg", "compute graph program (ComputeGraphStore)" },
            { ".zcgc", "compute graph constants (ComputeGraphConstantsStore)" },
            { ".zcps", "compute graph parameter tables (name hash -> slot)" },
            { ".ztsl", "facial tangent space LUT (FacialTangentSpaceLutStore)" },
            { ".zjd", "joint limit dictionary (JointLimitDictStore)" },
            { ".zmsk", "image mask (ImageMaskStore)" },
            { ".zmmd", "motion matcher data (MotionMatcherStore)" },
        };

        public static string Summary(Rpf9FileEntry e, byte[] data)
        {
            var sb = new StringBuilder();
            var nl = e.NameLower ?? "";
            var ext = Path.GetExtension(nl);
            sb.AppendLine(e.Path);
            if (Kinds.TryGetValue(ext, out var kind)) sb.AppendLine("Kind: " + kind);
            sb.AppendLine("Extension id " + e.ExtensionId + ", name hash " + e.NameHash64.ToString("x16") + (e.IsNamed ? "" : " (unnamed)"));
            int cpu = e.IsResource ? (int)Math.Min(Math.Max(e.VirtualSize, 0), data.Length) : data.Length;
            if (e.IsResource) sb.AppendLine("Resource version " + e.ResourceVersion + ", CPU part " + cpu + " bytes, GPU part " + (data.Length - cpu) + " bytes");
            else sb.AppendLine("Plain file, " + data.Length + " bytes");
            sb.AppendLine();

            var r = new Res(data, cpu);
            try
            {
                switch (e.IsResource ? ext : "")
                {
                    case ".zfid": FacialIdentity(sb, r); break;
                    case ".zcg": ComputeGraph(sb, r); break;
                    case ".zcgc": ComputeGraphConstants(sb, r); break;
                    case ".zcps": ParameterTables(sb, r); break;
                    case ".ztsl": TangentLut(sb, r); break;
                }
            }
            catch (Exception ex) { sb.AppendLine("Decode stopped: " + ex.Message); sb.AppendLine(); }

            try
            {
                if (e.IsResource) Census(sb, r);
                else Plain(sb, data);
            }
            catch (Exception ex) { sb.AppendLine("Census stopped: " + ex.Message); }

            sb.AppendLine();
            sb.AppendLine(e.IsResource ? "CPU part, first bytes:" : "First bytes:");
            Hex(sb, data, 0, Math.Min(cpu, 0x200));
            if (data.Length > cpu)
            {
                sb.AppendLine("GPU part, first bytes:");
                Hex(sb, data, cpu, Math.Min(data.Length - cpu, 0x100));
            }
            return sb.ToString();
        }

        class Res
        {
            public readonly byte[] D; public readonly int Cpu;
            public Res(byte[] d, int cpu) { D = d; Cpu = cpu; }
            public int Gpu => D.Length - Cpu;
            public uint U32(int o) => BitConverter.ToUInt32(D, o);
            public ulong U64(int o) => BitConverter.ToUInt64(D, o);
            public float F32(int o) => BitConverter.ToSingle(D, o);
            /// <summary>CPU offset a pointer at o points to, or -1</summary>
            public int Ptr(int o) { var v = U64(o); return v >= CpuBase && v < CpuBase + (ulong)Cpu ? (int)(v - CpuBase) : -1; }
            /// <summary>offset in the data a GPU pointer at o points to, or -1</summary>
            public int GPtr(int o) { var v = U64(o); return v >= GpuBase && v < GpuBase + (ulong)Gpu ? Cpu + (int)(v - GpuBase) : -1; }
        }

        static string Name(uint h)
        {
            var s = h == 0 ? null : Rpf9Strings.Get(h);
            return s == null ? h.ToString("x8") : s + " (" + h.ToString("x8") + ")";
        }

        /// <summary>
        /// The GPU buffer record .zfid and .zcgc share: +0x08 u32 element count (u32 words), +0x0C u16 element size (4),
        /// u16 format (8 in .zcgc, 9 in .zfid), +0x10 u32 flags, +0x18 GPU pointer.
        /// </summary>
        static int Buffer(StringBuilder sb, Res r, int b, out int words)
        {
            words = (int)r.U32(b + 8);
            int g = r.GPtr(b + 0x18);
            sb.AppendLine($"  buffer record at +0x{b:x}: {words} u32 words, element size {BitConverter.ToUInt16(r.D, b + 0xc)}, format {BitConverter.ToUInt16(r.D, b + 0xe)}, flags 0x{r.U32(b + 0x10):x}, data at GPU +0x{(g < 0 ? 0 : g - r.Cpu):x}");
            return g;
        }

        static void FacialIdentity(StringBuilder sb, Res r)
        {
            sb.AppendLine("Facial identity");
            sb.AppendLine("  base head: " + Name(r.U32(0x58)));
            int b = r.Ptr(0x10);
            if (b < 0) { sb.AppendLine("  no buffer record"); return; }
            int g = Buffer(sb, r, b, out int words);
            if (g < 0) return;
            int n = (words - 8) / 2;
            var off = new[] { r.F32(g + 4), r.F32(g + 8), r.F32(g + 12) };
            var sc = new[] { r.F32(g + 16), r.F32(g + 20), r.F32(g + 24) };
            sb.AppendLine($"  GPU header: u32 {r.U32(g)}, offset ({off[0]:0.#####}, {off[1]:0.#####}, {off[2]:0.#####}), scale ({sc[0]:0.###e0}, {sc[1]:0.###e0}, {sc[2]:0.###e0})");
            sb.AppendLine($"  {n} vertices, 8 bytes each: x 24 bits, y 24 bits, z 16 bits; delta = offset + q * scale (metres)");
            float[] lo = { float.MaxValue, float.MaxValue, float.MaxValue }, hi = { float.MinValue, float.MinValue, float.MinValue };
            double len = 0, maxLen = 0;
            for (int i = 0; i < n && g + 32 + 8 * i + 8 <= r.D.Length; i++)
            {
                ulong q = r.U64(g + 32 + 8 * i);
                var v = new[] { off[0] + (q & 0xffffff) * sc[0], off[1] + ((q >> 24) & 0xffffff) * sc[1], off[2] + (q >> 48) * sc[2] };
                for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], v[k]); hi[k] = Math.Max(hi[k], v[k]); }
                double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
                len += l; maxLen = Math.Max(maxLen, l);
            }
            if (n > 0)
            {
                sb.AppendLine($"  delta range x {lo[0]:0.#####}..{hi[0]:0.#####}, y {lo[1]:0.#####}..{hi[1]:0.#####}, z {lo[2]:0.#####}..{hi[2]:0.#####}");
                sb.AppendLine($"  mean length {len / n * 1000:0.##} mm, largest {maxLen * 1000:0.##} mm");
                sb.AppendLine("  (named *_full heads carry 8983 deltas; per-LOD identities match the head drawable's LOD vertex counts)");
            }
            sb.AppendLine();
        }

        static void ComputeGraph(StringBuilder sb, Res r)
        {
            sb.AppendLine("Compute graph program");
            int size = (int)r.U32(0x10), p = r.Ptr(0x18);
            sb.AppendLine($"  program: {size} bytes at +0x{Math.Max(p, 0):x} (u32 node records, not decoded)");
            sb.AppendLine($"  +0x34 0x{r.U32(0x34):x}, +0x38 {r.U32(0x38)} ({r.U32(0x3c)} aligned), +0x40 {r.U32(0x40)} ({r.U32(0x44)} aligned), +0x50 {BitConverter.ToUInt16(r.D, 0x50)}/{BitConverter.ToUInt16(r.D, 0x52)}");
            sb.AppendLine($"  constants link: {r.U64(0x48):x16} (the matching .zcgc carries the same value at +0x20)");
            if (p >= 0)
            {
                var hist = new Dictionary<uint, int>();
                for (int o = p; o + 4 <= Math.Min(p + size, r.Cpu); o += 4) { var v = r.U32(o); if (v > 0xff && v < 0x100000) hist[v] = hist.TryGetValue(v, out int c) ? c + 1 : 1; }
                sb.AppendLine("  most frequent mid-size words (likely opcodes/node kinds): " + string.Join(", ", hist.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"0x{kv.Key:x} x{kv.Value}")));
            }
            sb.AppendLine();
        }

        static void ComputeGraphConstants(StringBuilder sb, Res r)
        {
            sb.AppendLine("Compute graph constants");
            sb.AppendLine($"  link: {r.U64(0x20):x16} (the matching .zcg carries the same value at +0x48), data size {r.U32(0x28)} bytes");
            int b = r.Ptr(0x10);
            if (b < 0) b = 0x30;
            int g = Buffer(sb, r, b, out int words);
            if (g < 0) return;
            int n = Math.Min(words, (r.D.Length - g) / 4), small = 0;
            while (small < n && r.U32(g + 4 * small) < 0x10000) small++;
            sb.AppendLine($"  leading run of {small} small u32 values (index lists), then {n - small} other words");
            sb.AppendLine("  first values: " + string.Join(" ", Enumerable.Range(0, Math.Min(24, n)).Select(i => r.U32(g + 4 * i).ToString())));
            sb.AppendLine();
        }

        static void TangentLut(StringBuilder sb, Res r)
        {
            sb.AppendLine("Facial tangent space LUT");
            int b = r.Ptr(0x10);
            if (b < 0) { sb.AppendLine("  no buffer record"); return; }
            int g = Buffer(sb, r, b, out int words);
            if (g < 0) return;
            int n = Math.Min(words * 2, (r.D.Length - g) / 2), max = 0;
            for (int i = 0; i < n; i++) max = Math.Max(max, BitConverter.ToUInt16(r.D, g + 2 * i));
            sb.AppendLine($"  {n} u16 values, largest {max} (they look like vertex indices; meaning not decoded)");
            sb.AppendLine();
        }

        static void ParameterTables(StringBuilder sb, Res r)
        {
            sb.AppendLine("Compute graph parameter tables");
            sb.AppendLine("  set hash: " + Name(r.U32(0x10)) + " (this file's 32-bit TOC name hash; the .zcg/.zcgc/.ztsl of the set share it, .zfc/.zlfc configs reference the set by it)");
            // key tables: arrays of (u32 parameter name hash, u32 type/group hash)
            var groups = new Dictionary<uint, int>();
            var keys = new List<uint>();
            foreach (var a in Arrays(r))
            {
                if (a.Stride != 8 || a.Count == 0) continue;
                bool ok = true;
                for (int i = 0; i < a.Count && ok; i++) ok = a.Ptr + 8 * i + 8 <= r.Cpu;
                if (!ok) continue;
                // group hash repeats across a key table; count tables that look like that
                var g = r.U32(a.Ptr + 4);
                if (Enumerable.Range(0, a.Count).Count(i => r.U32(a.Ptr + 8 * i + 4) == g) * 2 < a.Count) continue;
                for (int i = 0; i < a.Count; i++)
                {
                    keys.Add(r.U32(a.Ptr + 8 * i));
                    var t = r.U32(a.Ptr + 8 * i + 4);
                    groups[t] = groups.TryGetValue(t, out int c) ? c + 1 : 1;
                }
            }
            sb.AppendLine($"  {keys.Count} keyed parameters in groups: " + string.Join(", ", groups.Select(kv => Name(kv.Key) + " x" + kv.Value)));
            var named = keys.Select(k => Rpf9Strings.Get(k)).Where(s => s != null).Distinct().Take(40).ToList();
            if (named.Count > 0) sb.AppendLine("  known parameter names: " + string.Join(", ", named));
            sb.AppendLine();
        }

        struct Arr { public int At, Count, Ptr, Stride; }

        /// <summary>
        /// {u32 count, u32 capacity, u64 pointer} records in the CPU part (the layout .zcps and other new GTA VI resources
        /// use), with the element stride guessed from the gap to the next pointed-to block.
        /// </summary>
        static List<Arr> Arrays(Res r)
        {
            var list = new List<Arr>();
            var targets = new SortedSet<int> { r.Cpu };
            for (int o = 0; o + 8 <= r.Cpu; o += 8) { int p = r.Ptr(o); if (p >= 0) targets.Add(p); }
            for (int o = 0; o + 16 <= r.Cpu; o += 8)
            {
                uint n = r.U32(o), cap = r.U32(o + 4);
                int p = r.Ptr(o + 8);
                if (p < 0 || n == 0 || n > 0x100000 || cap < n || cap > n * 2 + 16) continue;
                int next = targets.GetViewBetween(p + 1, int.MaxValue).Min;
                int gap = next - p, stride = 0;
                foreach (var s in new[] { 1, 2, 4, 8, 16, 32, 48, 64, 0x50, 0x80 })
                    if (s * (int)n <= gap && gap - s * (int)n < Math.Max(16, s)) { stride = s; break; }
                list.Add(new Arr { At = o, Count = (int)n, Ptr = p, Stride = stride });
            }
            return list;
        }

        static void Census(StringBuilder sb, Res r)
        {
            int cpuPtrs = 0, gpuPtrs = 0;
            for (int o = 0; o + 8 <= r.Cpu; o += 8)
            {
                if (r.Ptr(o) >= 0) cpuPtrs++;
                else if (r.GPtr(o) >= 0) gpuPtrs++;
            }
            sb.AppendLine($"Pointers in the CPU part: {cpuPtrs} to CPU data, {gpuPtrs} to GPU data");
            var arrs = Arrays(r);
            if (arrs.Count > 0)
            {
                sb.AppendLine($"Arrays ({{u32 count, u32 capacity, pointer}} records): {arrs.Count}");
                foreach (var a in arrs.Take(40))
                    sb.AppendLine($"  +0x{a.At:x4}: {a.Count} x {(a.Stride > 0 ? a.Stride + " bytes" : "?")} at +0x{a.Ptr:x}");
            }
            var strs = Strings(r.D, 0, r.Cpu);
            if (strs.Count > 0)
            {
                sb.AppendLine($"Strings in the CPU part: {strs.Count}");
                foreach (var s in strs.Take(60)) sb.AppendLine($"  +0x{s.Key:x}: {s.Value}");
            }
            var known = new SortedDictionary<int, string>();
            for (int o = 0; o + 4 <= r.Cpu && known.Count < 60; o += 4)
            {
                var h = r.U32(o);
                if (h < 0x10000 || (h & 0xf0000000) == 0x50000000 || (h & 0xf0000000) == 0x60000000) continue;
                var s = Rpf9Strings.Get(h);
                if (s != null) known[o] = s;
            }
            if (known.Count > 0)
            {
                sb.AppendLine("Known name hashes:");
                foreach (var kv in known) sb.AppendLine($"  +0x{kv.Key:x}: {kv.Value}");
            }
        }

        static void Plain(StringBuilder sb, byte[] d)
        {
            if (d.Length >= 4)
            {
                var m = Encoding.ASCII.GetString(d, 0, 4);
                sb.AppendLine("Magic: " + (m.All(c => c >= 0x20 && c < 0x7f) ? m : BitConverter.ToUInt32(d, 0).ToString("x8")));
            }
            var strs = Strings(d, 0, d.Length);
            if (strs.Count > 0)
            {
                sb.AppendLine($"Strings: {strs.Count}");
                foreach (var s in strs.Take(80)) sb.AppendLine($"  +0x{s.Key:x}: {s.Value}");
            }
        }

        static List<KeyValuePair<int, string>> Strings(byte[] d, int from, int to)
        {
            var res = new List<KeyValuePair<int, string>>();
            int start = -1;
            for (int i = from; i < to; i++)
            {
                byte c = d[i];
                bool pr = c >= 0x20 && c < 0x7f;
                if (pr) { if (start < 0) start = i; continue; }
                if (start >= 0 && c == 0 && i - start >= 5)
                {
                    var s = Encoding.ASCII.GetString(d, start, i - start);
                    // names and labels only: letters, digits and a few separators, mostly letters, not one letter repeated
                    if (s.All(ch => char.IsLetterOrDigit(ch) || " _-./:\\".IndexOf(ch) >= 0) && s.Count(char.IsLetter) * 2 >= s.Length
                        && s.Distinct().Count() > 2 && s.Trim() == s && !Enumerable.Range(0, s.Length - 3).Any(j => s[j] == s[j + 1] && s[j] == s[j + 2] && s[j] == s[j + 3]))
                        res.Add(new KeyValuePair<int, string>(start, s));
                }
                start = -1;
            }
            return res;
        }

        static void Hex(StringBuilder sb, byte[] d, int from, int len)
        {
            for (int o = 0; o < len; o += 16)
            {
                int n = Math.Min(16, len - o);
                sb.Append($"  {o:x5}  ");
                for (int i = 0; i < 16; i++) sb.Append(i < n ? d[from + o + i].ToString("x2") + (i == 7 ? "  " : " ") : "   ");
                sb.Append(' ');
                for (int i = 0; i < n; i++) { byte c = d[from + o + i]; sb.Append(c >= 0x20 && c < 0x7f ? (char)c : '.'); }
                sb.AppendLine();
            }
        }
    }
}
