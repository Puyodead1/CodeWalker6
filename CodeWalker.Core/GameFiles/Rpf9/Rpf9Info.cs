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
            { ".zpm", "parameterized motion set: clips blended by 1..5 parameters" },
            { ".zpt", "particle effect list (rage::ptxFxList): effect, emitter and particle rules" },
            { ".zfd", "frame filter dictionary: weighted (bone, track) lists" },
            { ".zrd", "rig dictionary: compiled RIG0 programs" },
            { ".zpmd", "pose matcher data: poses sampled from clips" },
            { ".zdatb", "binary data table (key, label rows)" },
            { ".zasd", "relocatable object blob: object offset table, pointers tagged 71717171" },
        };

        static ushort U16(Res r, int o) => BitConverter.ToUInt16(r.D, o);
        static string AtString(Res r, int o)
        {
            int p = r.Ptr(o);
            return p < 0 ? "" : Encoding.ASCII.GetString(r.D, p, Math.Min(U16(r, o + 8), r.Cpu - p));
        }
        /// <summary>element offsets of an atArray {ptr, u16 count, u16 capacity}</summary>
        static List<int> AtArray(Res r, int o, int size)
        {
            var l = new List<int>();
            int p = r.Ptr(o), n = U16(r, o + 8);
            for (int i = 0; i < n && p >= 0; i++) l.Add(p + size * i);
            return l;
        }
        /// <summary>entries of an atMap {ptr buckets, u32 bucket count, u32 0, u32 count}; next pointer is the entry's last u64</summary>
        static List<int> AtMap(Res r, int o, int entrySize)
        {
            var l = new List<int>();
            int tb = r.Ptr(o), nb = (int)r.U32(o + 8);
            for (int i = 0; i < nb && tb >= 0; i++)
                for (int e = r.Ptr(tb + 8 * i), guard = 0; e >= 0 && guard < 100000; e = r.Ptr(e + entrySize - 8), guard++) l.Add(e);
            return l;
        }

        /// <summary>.zpm (v70), data\anim_format.md: set header 0xB0, motions 0x140 B, sampler nodes typed at +0x08</summary>
        static void ParameterizedMotion(StringBuilder sb, Res r)
        {
            var dicts = AtArray(r, 0x70, 16).Select(e => r.U32(e + 8)).ToList();
            var motions = AtMap(r, 0x48, 0x18);
            sb.AppendLine($"Parameterized motion set {AtString(r, 0x38)} (hash {r.U32(0x68):x8}), {motions.Count} motions");
            sb.AppendLine("  clip dictionaries: " + string.Join(", ", dicts.Select(Name)));
            sb.AppendLine($"  shared data: {r.U32(0xA8)} bytes at +0x{Math.Max(r.Ptr(0xA0), 0):x}");
            foreach (var me in motions)
            {
                int m = r.Ptr(me + 8);
                if (m < 0) continue;
                uint kind = r.U32(m + 0x58);
                var clips = AtArray(r, m + 0x60, 16);
                var curves = AtArray(r, m + 0x70, 0x28);
                sb.AppendLine();
                sb.AppendLine($"  motion {AtString(r, m + 0x30)} (key {r.U32(m + 0x28):x8}), kind {kind}, {r.U32(m + 0x5C)} parameters, {clips.Count} clips");
                for (int i = 0; i < clips.Count; i++)
                {
                    uint d = r.U32(clips[i] + 12);
                    string dur = i < curves.Count ? $", duration {r.F32(curves[i]):0.###} s, curve " + string.Join(" ",
                        AtArray(r, curves[i] + 8, 8).Select(p => $"{r.F32(p):0.###}>{r.F32(p + 4):0.###}")) : "";
                    sb.AppendLine($"    clip {i}: {Name(r.U32(clips[i] + 8))}, dictionary {dicts.IndexOf(d)}{dur}");
                }
                if (kind == 1)
                {
                    int n = r.Ptr(m + 0x138);
                    if (n >= 0) sb.AppendLine("    " + SamplerNode(r, n));
                }
                else if (kind == 2)
                {
                    foreach (var reg in AtArray(r, m + 0x120, 0x38))
                    {
                        int n = r.Ptr(reg + 0x18);
                        sb.AppendLine($"    region: clips {r.U32(reg + 0x20)}..{r.U32(reg + 0x20) + r.U32(reg + 0x24) - 1}" + (n >= 0 ? ", " + SamplerNode(r, n) : ""));
                    }
                }
            }
            sb.AppendLine();
        }

        /// <summary>16-byte string: inline when byte 15 has bit 7 (length in the low bits), else {char*, u16 len, u16 cap, u32}</summary>
        static string SStr(Res r, int o)
        {
            byte t = r.D[o + 15];
            return (t & 0x80) != 0 ? Encoding.ASCII.GetString(r.D, o, Math.Min(t & 0x7F, 15)) : CStr(r, o);
        }
        static readonly Dictionary<uint, string> TrackTypes = new Dictionary<uint, string>
        {
            { 0x98e1750c, "boneRotation" }, { 0xf791a95f, "boneTranslation" }, { 0x7aaa9f60, "boneScale" }, { 0x2deefadd, "genericControl" },
            { 0x60d0644c, "firstPersonCameraWeight" }, { 0x6ddef121, "cameraTranslation" }, { 0xf5c3a5b6, "genericTranslation" },
        };
        /// <summary>pgDictionary at 0: (hash, item offset) pairs</summary>
        static List<(uint, int)> PgDict(Res r) =>
            AtArray(r, 0x20, 4).Zip(AtArray(r, 0x30, 8), (h, p) => (r.U32(h), r.Ptr(p))).ToList();

        /// <summary>.zfd, data\animdata_formats.md: frame filters (0x40) with 0x30-byte (id, weight, track) entries</summary>
        static void FrameFilters(StringBuilder sb, Res r)
        {
            var fs = PgDict(r);
            sb.AppendLine($"Frame filter dictionary: {fs.Count} filters");
            foreach (var (h, f) in fs.Where(x => x.Item2 >= 0))
            {
                var es = AtArray(r, f + 0x28, 0x30);
                var tracks = es.GroupBy(e => r.U32(e + 0x28)).Select(g => $"{(TrackTypes.TryGetValue(g.Key, out var n) ? n : g.Key.ToString("x8"))} {g.Count()}");
                sb.AppendLine($"  {SStr(r, f + 0x18)} ({h:x8}): {es.Count} entries, {es.Count(e => r.F32(e + 4) == 1f)} at weight 1; {string.Join(", ", tracks)}");
            }
            sb.AppendLine();
        }

        /// <summary>.zjd: joint limit sets (0x70) with 0x90-byte rotation limits</summary>
        static void JointLimits(StringBuilder sb, Res r)
        {
            var js = PgDict(r);
            sb.AppendLine($"Joint limit dictionary: {js.Count} sets");
            foreach (var (h, o) in js.Where(x => x.Item2 >= 0))
            {
                int n = (int)r.U32(o + 0x18), lp = r.Ptr(o + 0x20);
                sb.AppendLine($"  {SStr(r, o + 0x58)} ({h:x8}): {n} rotation limits");
                for (int i = 0; i < n && lp >= 0; i++)
                {
                    int e = lp + 0x90 * i;
                    uint cp = r.U32(e + 0x78);
                    var pts = Enumerable.Range(0, (int)Math.Min(cp, 8)).Select(k => $"({r.F32(e + 0x18 + 12 * k):0.###} {r.F32(e + 0x1C + 12 * k):0.###} {r.F32(e + 0x20 + 12 * k):0.###})");
                    sb.AppendLine($"    bone {Name(r.U32(e + 0x80))}: twist {r.F32(e + 0x10):0.###}..{r.F32(e + 0x14):0.###}, {cp} control points {string.Join(" ", pts)}");
                }
            }
            sb.AppendLine();
        }

        static readonly Dictionary<uint, string> RigSections = new Dictionary<uint, string>
        {
            { 0xe636c3e9, "ops" }, { 0x2316833f, "inputs" }, { 0x7224f987, "outputs" }, { 0xb00f0959, "data" },
            { 0x74d00508, "property fields" }, { 0x6257c371, "properties" },
        };

        /// <summary>.zrd: named dictionary of RIG\x02 blobs (RIG0 program with tagged sections)</summary>
        static void Rigs(StringBuilder sb, Res r)
        {
            var items = AtMap(r, 0x48, 0x18);
            sb.AppendLine($"Rig dictionary {SStr(r, 0x38)}: {items.Count} rigs");
            foreach (var e in items)
            {
                int v = r.Ptr(e + 8), b = v < 0 ? -1 : r.Ptr(v + 0x20);
                if (b < 0 || r.U32(b) != 0x02474952) { sb.AppendLine($"  {r.U32(e):x8}: no rig"); continue; }
                int g = b + 0x100, ns = (int)r.U32(g + 0x10);
                var secs = new List<string>();
                string name = null; int ops = 0, ins = 0, outs = 0;
                int fields = -1, props = -1;
                for (int k = 0; k < ns; k++)
                {
                    int s = g + (int)r.U32(g + 0x14 + 4 * k);
                    uint t = r.U32(s);
                    if (t == 0xe636c3e9) ops = (int)r.U32(s + 8);
                    else if (t == 0x2316833f) ins = (int)r.U32(s + 8);
                    else if (t == 0x7224f987) outs = (int)r.U32(s + 8);
                    else if (t == 0x74d00508) fields = s + 8;
                    else if (t == 0x6257c371) props = s + 8;
                    else secs.Add(t.ToString("x8"));
                }
                if (fields >= 0 && props >= 0)
                    for (int i = 0; i < r.U32(fields); i++)
                    {
                        int f = fields + 4 + 8 * i;
                        if (r.U32(f) == 0xc9e2bfa4) name = Encoding.ASCII.GetString(r.D, props + U16(r, f + 4), U16(r, f + 6)).TrimEnd('\0');
                    }
                sb.AppendLine($"  {name ?? r.U32(e).ToString("x8")}: " + (ops > 0 ? $"{ops} ops, {ins} inputs, {outs} outputs" : "table rig") + (secs.Count > 0 ? " + " + string.Join(" ", secs) : ""));
            }
            sb.AppendLine();
        }

        /// <summary>embedded crSkeletonData (0x80): bones 0x50 at +0x28, parents u16 at +0x40, count u16 at +0x6c</summary>
        static string SkeletonBones(Res r, int s)
        {
            if (s < 0) return "";
            int n = U16(r, s + 0x6C), bp = r.Ptr(s + 0x28);
            return $"{n} bones: " + string.Join(" ", Enumerable.Range(0, bp < 0 ? 0 : n).Select(i => CStr(r, bp + 0x50 * i + 0x30)));
        }

        /// <summary>.zmmd: named dictionary of motion matchers (clip ranges, clip refs, skeleton)</summary>
        static void MotionMatchers(StringBuilder sb, Res r)
        {
            var dicts = AtArray(r, 0x70, 16).Select(e => r.U32(e + 8)).ToList();
            var items = AtMap(r, 0x48, 0x18);
            sb.AppendLine($"Motion matchers {SStr(r, 0x38)}: {items.Count} matchers, clip dictionaries {string.Join(", ", dicts.Select(Name))}");
            foreach (var e in items)
            {
                int v = r.Ptr(e + 8);
                if (v < 0) continue;
                int n = (int)r.U32(v + 0x50), cp = r.Ptr(v + 0x58);
                var clips = Enumerable.Range(0, cp < 0 ? 0 : n).Select(i => r.U32(cp + 0x18 * i + 12)).Distinct().Count();
                sb.AppendLine($"  {SStr(r, v + 0x38)}: {n} ranges over {clips} clips, {r.U32(v + 0x90)} clip refs");
                int cfg = r.Ptr(v + 0x20);
                if (cfg >= 0) sb.AppendLine("    " + SkeletonBones(r, r.Ptr(cfg + 0x28)));
            }
            sb.AppendLine();
        }

        /// <summary>.zpmd: named dictionary of pose matchers (poses referencing clip frames)</summary>
        static void PoseMatchers(StringBuilder sb, Res r)
        {
            var items = AtMap(r, 0x48, 0x18);
            sb.AppendLine($"Pose matchers {SStr(r, 0x38)}: {items.Count}");
            foreach (var e in items)
            {
                int v = r.Ptr(e + 8);
                if (v < 0) continue;
                var poses = AtArray(r, v + 0x20, 8).Select(r.Ptr).Where(p => p >= 0).ToList();
                sb.AppendLine($"  {SStr(r, v + 0x40)}: {poses.Count} poses, {AtArray(r, v + 0x30, 0x10).Count} weighted bones, frame time {r.F32(v + 0x68):0.#####}");
                foreach (var p in poses) sb.AppendLine($"    clip {Name(r.U32(p + 4))} of {Name(r.U32(p))}, frame {r.U32(p + 12)}, weight {r.F32(p + 8):0.###}");
                sb.AppendLine("    " + SkeletonBones(r, r.Ptr(v + 0x50)));
            }
            sb.AppendLine();
        }

        static string CStr(Res r, int o)
        {
            int p = r.Ptr(o);
            if (p < 0) return "";
            int e = p;
            while (e < r.Cpu && r.D[e] != 0) e++;
            return Encoding.ASCII.GetString(r.D, p, e - p);
        }

        /// <summary>particle behaviour mask bit -> name (data\particle_format.md); unnamed bits print as ?n</summary>
        static readonly Dictionary<int, string> PtxBehaviours = new Dictionary<int, string>
        {
            { 1, "Age" }, { 2, "Acceleration" }, { 3, "Attractor" }, { 4, "Velocity" }, { 5, "Rotation" }, { 7, "Size" },
            { 8, "Dampening" }, { 9, "MatrixWeight" }, { 12, "ZCull" }, { 16, "Collision" }, { 17, "AnimateTexture" },
            { 19, "Colour" }, { 21, "Decal" }, { 23, "Light" }, { 24, "FogVolume" }, { 27, "Sprite" }, { 28, "Trail" }, { 29, "Model" },
        };
        static readonly string[] PtxDomainShapes = { "box", "sphere", "cylinder", "attractor", "texture" };

        /// <summary>.zpt (v72), data\particle_format.md: rage::ptxFxList with effect, emitter and particle rule dictionaries</summary>
        static void ParticleEffects(StringBuilder sb, Res r)
        {
            List<int> Rules(int slot) { int d = r.Ptr(slot); return d < 0 ? new List<int>() : AtArray(r, d + 0x30, 8).Select(r.Ptr).ToList(); }
            int HashCount(int slot) => r.Ptr(slot) < 0 ? 0 : (int)r.U32(slot + 0x10);
            var effects = Rules(0x28); var emitters = Rules(0x30); var particles = Rules(0x38);
            sb.AppendLine($"Particle effect list {CStr(r, 0x10)}: {effects.Count} effects, {emitters.Count} emitters, {particles.Count} particle rules, {HashCount(0x40)} models, {HashCount(0x58)} domain textures");
            sb.AppendLine();
            foreach (var e in effects.Where(x => x >= 0))
            {
                sb.AppendLine($"effect {CStr(r, e + 0x20)}: duration {r.F32(e + 0x78):0.###}..{r.F32(e + 0x7C):0.###} s, loops {(int)r.U32(e + 0x50)}, cull distance {r.F32(e + 0xA8):0.#}");
                foreach (var ev in AtArray(r, e + 0x38, 8).Select(r.Ptr).Where(x => x >= 0))
                    sb.AppendLine($"  {r.F32(ev + 0x10):0.###}..{r.F32(ev + 0x14):0.###}: emitter {CStr(r, ev + 0x40)}, particle {CStr(r, ev + 0x48)}");
            }
            sb.AppendLine();
            foreach (var e in emitters.Where(x => x >= 0))
            {
                var doms = new List<string>();
                for (int k = 0; k < 3; k++)
                {
                    int d = r.Ptr(e + 0x30 + 8 * k);
                    if (d < 0) continue;
                    uint shape = r.U32(d + 0x18C);
                    doms.Add(new[] { "creation", "target", "attractor" }[k] + " " + (shape < PtxDomainShapes.Length ? PtxDomainShapes[shape] : shape.ToString()));
                }
                sb.AppendLine($"emitter {CStr(r, e + 0x20)}: {string.Join(", ", doms)}{(r.D[e + 0x2F0] != 0 ? ", one shot" : "")}");
            }
            sb.AppendLine();
            foreach (var p in particles.Where(x => x >= 0))
            {
                uint mask = r.U32(p + 0x180);
                var bs = Enumerable.Range(0, 32).Where(k => (mask >> k & 1) != 0).Select(k => PtxBehaviours.TryGetValue(k, out var n) ? n : "?" + k);
                sb.AppendLine($"particle {CStr(r, p + 0x178)}: {string.Join(" ", bs)}");
                foreach (var m in AtArray(r, p + 0x3B0, 0x20)) sb.AppendLine("  model " + CStr(r, m));
            }
            sb.AppendLine();
        }

        static string SamplerNode(Res r, int n)
        {
            uint t = r.U32(n + 8);
            if (t != 3) return $"sampler type {t} ({(t == 1 ? "single clip" : "multi-parameter grid in the shared data")})";
            var pts = AtArray(r, n + 0x20, 8).Select(p => $"clip {r.U32(p)} at {r.F32(p + 4):0.####}");
            return $"sampler type 3 (1D), range {r.F32(n + 0x30):0.####} .. {r.F32(n + 0x34):0.####}: " + string.Join(", ", pts);
        }

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
                    case ".zpm": ParameterizedMotion(sb, r); break;
                    case ".zpt": ParticleEffects(sb, r); break;
                    case ".zfd": FrameFilters(sb, r); break;
                    case ".zjd": JointLimits(sb, r); break;
                    case ".zrd": Rigs(sb, r); break;
                    case ".zmmd": MotionMatchers(sb, r); break;
                    case ".zpmd": PoseMatchers(sb, r); break;
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
