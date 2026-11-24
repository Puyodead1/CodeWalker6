using SharpDX;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI secondary motion: .zsm (SecMotionStore) and .zsmam (SecMotionAssetMapStore).
    /// Port of the parts of tools/zsm.py a viewer needs; data/zsm_format.md has the full layout.
    /// A .zsmam maps the RPF9 name hash of a .zdr/.zdd/.zft to the name hashes of its .zsm files.
    /// Positions are in the drawable's bind-pose model space; bone references are joaat bone tags
    /// (see <see cref="Rpf9Drawable.BoneHashMap"/>).
    /// </summary>
    public static class Rpf9SecMotion
    {
        const ulong VB = 0x50000000;

        public class BadDataException : Exception { public BadDataException(string m) : base(m) { } }

        /// <summary>A particle skinned to up to 8 bones (weights sum to 255).</summary>
        public class Particle
        {
            public Vector3 Position;
            public float W;
            public uint[] Bones = new uint[8];
            public byte[] Weights = new byte[8];
        }

        /// <summary>Type 2 particle set: chain 0 (high detail), chain 1 (low detail) or the main set.</summary>
        public class ParticleSet
        {
            public string Name;
            public List<Particle> Particles = new List<Particle>();
            public List<int[]> Edges = new List<int[]>();   // particle index pairs
            public List<KeyValuePair<uint, byte[]>> NamedSets = new List<KeyValuePair<uint, byte[]>>(); // per-particle u8 masks
        }

        /// <summary>Type 5 angular spring.</summary>
        public class Spring
        {
            public Vector4 Offset;
            public Quaternion Rotation;
            public Vector3 Extent;
            public uint Flags, Mode;
        }

        /// <summary>Type 1 dangle link between two bones (0 = none).</summary>
        public class BoneLink
        {
            public string Kind;
            public uint BoneA, BoneB;
        }

        public class Zsm
        {
            public int Type;                 // 1 weapon dangles, 2 particle chains, 3 GPU, 4 single spring, 5 per-bone springs
            public uint Tag, Flags, LayoutFlags;
            public List<ParticleSet> Sets = new List<ParticleSet>();          // type 2
            public int SkinMap0, SkinMap1;                                    // type 2 without a main set: vertex maps
            public List<uint> Bones = new List<uint>();                       // type 1 dangle bones
            public List<BoneLink> Links = new List<BoneLink>();               // type 1
            public List<Spring> Springs = new List<Spring>();                 // type 4/5
            public List<KeyValuePair<uint, ushort[]>> SpringKeys = new List<KeyValuePair<uint, ushort[]>>(); // type 5: key -> spring indices
            public Vector3 BoundsMin, BoundsMax;                              // type 3
            public bool HasBounds;
            public int GpuCount, GpuStride;                                   // type 3
            public List<uint> Hashes = new List<uint>();                      // type 3

            public static string TypeName(int t)
            {
                switch (t)
                {
                    case 1: return "weapon dangles";
                    case 2: return "particle chains";
                    case 3: return "GPU simulated";
                    case 4: return "rotational spring";
                    case 5: return "per-bone springs";
                    default: return "unknown type " + t;
                }
            }
            public override string ToString() => TypeName(Type);
        }

        class Reader
        {
            public readonly byte[] B;
            public Reader(byte[] b) { B = b; }
            void Check(long o, int n)
            {
                if (o < 0 || o + n > B.Length) throw new BadDataException("offset 0x" + o.ToString("x") + " outside the file");
            }
            public byte U8(long o) { Check(o, 1); return B[o]; }
            public ushort U16(long o) { Check(o, 2); return BitConverter.ToUInt16(B, (int)o); }
            public uint U32(long o) { Check(o, 4); return BitConverter.ToUInt32(B, (int)o); }
            public ulong U64(long o) { Check(o, 8); return BitConverter.ToUInt64(B, (int)o); }
            public float F32(long o) { Check(o, 4); return BitConverter.ToSingle(B, (int)o); }
            public Vector3 V3(long o) => new Vector3(F32(o), F32(o + 4), F32(o + 8));
            public Vector4 V4(long o) => new Vector4(F32(o), F32(o + 4), F32(o + 8), F32(o + 12));
            /// <summary>Virtual pointer -> file offset, -1 for null or anything outside the virtual segment.</summary>
            public long Ptr(long o)
            {
                ulong v = U64(o);
                if (v < VB || v >= VB + (ulong)B.Length) return -1;
                return (long)(v - VB);
            }
            /// <summary>Vec {u32 count; u32 cap; T* ptr}: (offset, count); count 0 when the pointer is null.</summary>
            public (long p, int n) Vec(long o, int esize)
            {
                int n = (int)U32(o);
                long p = Ptr(o + 8);
                if (p < 0 || n <= 0) return (-1, 0);
                Check(p, n * esize);
                return (p, n);
            }
            /// <summary>atArray {T* ptr; u16 count; u16 cap}.</summary>
            public (long p, int n) Arr(long o, int esize)
            {
                long p = Ptr(o);
                int n = U16(o + 8);
                if (p < 0 || n <= 0) return (-1, 0);
                Check(p, n * esize);
                return (p, n);
            }
            public ushort[] U16s(long o)
            {
                var (p, n) = Vec(o, 2);
                var r = new ushort[n];
                for (int i = 0; i < n; i++) r[i] = U16(p + 2 * i);
                return r;
            }
            /// <summary>SwissMap {u32 groups; u32 count; Group* ptr}: slot offsets of the occupied slots.</summary>
            public List<long> Swiss(long o, int slotSize)
            {
                var res = new List<long>();
                int groups = (int)U32(o);
                long p = Ptr(o + 8);
                if (p < 0 || groups <= 0) return res;
                int gsz = 16 + 16 * slotSize;
                Check(p, groups * gsz);
                for (int g = 0; g < groups; g++)
                {
                    long go = p + (long)g * gsz;
                    for (int k = 0; k < 16; k++)
                    {
                        if ((B[go + k] & 0x80) != 0) continue;
                        res.Add(go + 16 + k * slotSize);
                    }
                }
                return res;
            }
        }

        /// <summary>Decode a .zsm. Throws <see cref="BadDataException"/> on data this version doesn't understand.</summary>
        public static Zsm Read(byte[] data)
        {
            if (data == null || data.Length < 0x40) throw new BadDataException("file too small");
            var r = new Reader(data);
            var z = new Zsm { Type = data[0x1a], Tag = r.U32(0x1c) };
            switch (z.Type)
            {
                case 1: ReadType1(r, z); break;
                case 2: ReadType2(r, z); break;
                case 3: ReadType3(r, z); break;
                case 4: ReadType4(r, z); break;
                case 5: ReadType5(r, z); break;
                default: throw new BadDataException("unknown secondary motion type " + z.Type);
            }
            return z;
        }

        static void ReadBindings(Reader r, long group, ParticleSet set)
        {
            var (p, n) = r.Vec(group, 40);
            for (int i = 0; i < n && i < set.Particles.Count; i++)
            {
                var pt = set.Particles[i];
                long b = p + 40L * i;
                for (int k = 0; k < 8; k++)
                {
                    pt.Bones[k] = r.U32(b + 4 * k);
                    pt.Weights[k] = r.U8(b + 0x20 + k);
                }
            }
        }

        static void ReadParticles(Reader r, long arr, ParticleSet set)
        {
            var (p, n) = r.Arr(arr, 16);
            for (int i = 0; i < n; i++)
            {
                var v = r.V4(p + 16L * i);
                set.Particles.Add(new Particle { Position = new Vector3(v.X, v.Y, v.Z), W = v.W });
            }
        }

        static void ReadNamedSets(Reader r, long vec, ParticleSet set)
        {
            var (p, n) = r.Vec(vec, 0x18);
            for (int i = 0; i < n; i++)
            {
                long s = p + 0x18L * i;
                var (mp, mn) = r.Vec(s + 8, 1);
                var mask = new byte[mn];
                if (mn > 0) Buffer.BlockCopy(r.B, (int)mp, mask, 0, mn);
                set.NamedSets.Add(new KeyValuePair<uint, byte[]>(r.U32(s), mask));
            }
        }

        static void AddEdge(ParticleSet set, HashSet<long> seen, int a, int b)
        {
            int n = set.Particles.Count;
            if (a == b || a < 0 || b < 0 || a >= n || b >= n) return;
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (seen.Add(key)) set.Edges.Add(new[] { a, b });
        }

        static void ReadType2(Reader r, Zsm z)
        {
            z.Flags = r.U32(0x20);
            z.LayoutFlags = r.U32(0x24);
            for (int c = 0; c < 2; c++)
            {
                long ch = 0x28 + 0x2a0L * c;
                var set = new ParticleSet { Name = c == 0 ? "chain 0" : "chain 1 (low detail)" };
                ReadParticles(r, ch, set);
                ReadBindings(r, ch + 0x180, set);
                var e = r.U16s(ch + 0x238);           // distance constraints: index pairs
                var seen = new HashSet<long>();
                for (int i = 0; i + 1 < e.Length; i += 2) AddEdge(set, seen, e[i], e[i + 1]);
                ReadNamedSets(r, ch + 0x228, set);
                if (set.Particles.Count > 0) z.Sets.Add(set);
            }
            const long U = 0x568;
            if ((z.LayoutFlags & 0x10) != 0)
            {
                var set = new ParticleSet { Name = "main" };
                ReadParticles(r, U + 0x20, set);
                ReadBindings(r, U + 0x180, set);
                var a = r.U16s(U + 0x160);
                var b = r.U16s(U + 0x170);
                var seen = new HashSet<long>();
                for (int i = 0; i < Math.Min(a.Length, b.Length); i++) AddEdge(set, seen, a[i], b[i]);
                ReadNamedSets(r, U + 0x1c8, set);
                if (set.Particles.Count > 0) z.Sets.Add(set);
            }
            else
            {
                z.SkinMap0 = r.Vec(U, 32).n;
                z.SkinMap1 = r.Vec(U + 0x10, 32).n;
            }
        }

        static void ReadType1(Reader r, Zsm z)
        {
            z.Flags = r.U32(0x20);
            z.LayoutFlags = r.U32(0x24);
            var bones = new HashSet<uint>();
            void Bone(uint h) { if (h != 0 && bones.Add(h)) z.Bones.Add(h); }
            var (p, n) = r.Vec(0x158, 64);
            for (int i = 0; i < n; i++) Bone(r.U32(p + 64L * i + 0x24));
            (p, n) = r.Vec(0x178, 112);
            for (int i = 0; i < n; i++)
            {
                var (sp, sn) = r.Vec(p + 112L * i + 0x40, 64);
                for (int k = 0; k < sn; k++)
                {
                    long s = sp + 64L * k;
                    var l = new BoneLink { Kind = "pendulum", BoneA = r.U32(s + 0x10), BoneB = r.U32(s + 0x14) };
                    z.Links.Add(l); Bone(l.BoneA); Bone(l.BoneB);
                }
            }
            (p, n) = r.Vec(0x1d8, 80);
            for (int i = 0; i < n; i++)
            {
                long s = p + 80L * i;
                var l = new BoneLink { Kind = "limit", BoneA = r.U32(s + 0x0c), BoneB = r.U32(s + 0x10) };
                z.Links.Add(l); Bone(l.BoneA); Bone(l.BoneB);
            }
        }

        static void ReadType3(Reader r, Zsm z)
        {
            z.Flags = r.U32(0x20);
            z.LayoutFlags = r.U32(0x24);
            var mn = r.V3(0x30);
            var mx = r.V3(0x40);
            if (mn.X <= mx.X && mn.Y <= mx.Y && mn.Z <= mx.Z)
            {
                z.BoundsMin = mn; z.BoundsMax = mx; z.HasBounds = true;
            }
            long g = r.Ptr(0x70);
            if (g >= 0)
            {
                z.GpuCount = (int)r.U32(g + 8);
                z.GpuStride = r.U16(g + 0x0c);
            }
            var (p, n) = r.Vec(0x98, 4);
            for (int i = 0; i < n; i++) z.Hashes.Add(r.U32(p + 4L * i));
        }

        static void ReadType4(Reader r, Zsm z)
        {
            z.Flags = r.U32(0x20);
            z.LayoutFlags = r.U32(0x24);
            var q = r.V4(0x60);
            var lo = r.V4(0x90);
            var hi = r.V4(0xa0);
            z.Springs.Add(new Spring
            {
                Rotation = new Quaternion(q.X, q.Y, q.Z, q.W),
                Offset = Vector4.Zero,
                Extent = new Vector3(hi.X - lo.X, hi.Y - lo.Y, hi.Z - lo.Z) * 0.5f,
            });
        }

        static void ReadType5(Reader r, Zsm z)
        {
            z.Flags = r.U32(0x20);
            z.LayoutFlags = r.U32(0x24);
            var (p, n) = r.Vec(0x28, 0x90);
            for (int i = 0; i < n; i++)
            {
                long s = p + 0x90L * i;
                var q = r.V4(s + 0x10);
                z.Springs.Add(new Spring
                {
                    Offset = r.V4(s),
                    Rotation = new Quaternion(q.X, q.Y, q.Z, q.W),
                    Extent = r.V3(s + 0x50),
                    Flags = r.U32(s + 0x5c),
                    Mode = r.U32(s + 0x80),
                });
            }
            foreach (var s in r.Swiss(0x38, 0x18))
                z.SpringKeys.Add(new KeyValuePair<uint, ushort[]>(r.U32(s), r.U16s(s + 8)));
        }

        /// <summary>
        /// Decode a .zsmam: drawable name hash (.zdr/.zdd/.zft) -> .zsm name hashes.
        /// +0x10 SwissMap of 0x20-byte slots {u64 key; 16 bytes; ptr value}; value = u32 header
        /// (count = (h &gt;&gt; 3) + (h &amp; 7)) followed by the u64 name hashes.
        /// </summary>
        public static Dictionary<ulong, ulong[]> ReadAssetMap(byte[] data)
        {
            if (data == null || data.Length < 0x20) throw new BadDataException("file too small");
            var r = new Reader(data);
            var res = new Dictionary<ulong, ulong[]>();
            foreach (var s in r.Swiss(0x10, 0x20))
            {
                ulong key = r.U64(s);
                long v = r.Ptr(s + 0x18);
                if (v < 0) continue;
                uint h = r.U32(v);
                int n = (int)((h >> 3) + (h & 7));
                if (n > 4096) throw new BadDataException("implausible list length " + n);
                var list = new ulong[n];
                for (int i = 0; i < n; i++) list[i] = r.U64(v + 4 + 8L * i);
                res[key] = list;
            }
            return res;
        }

        // ------------------------------------------------------------------ text

        static string F(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);
        static string F(Vector3 v) => "(" + F(v.X) + ", " + F(v.Y) + ", " + F(v.Z) + ")";

        /// <summary>Readable summary. boneName resolves a bone tag (null/unknown = hex).</summary>
        public static string Summary(Zsm z, Func<uint, string> boneName = null, IEnumerable<string> usedBy = null)
        {
            string B(uint h)
            {
                if (h == 0) return "-";
                var n = boneName?.Invoke(h);
                return string.IsNullOrEmpty(n) ? "0x" + h.ToString("x8") : n;
            }
            var sb = new StringBuilder();
            sb.AppendLine("Secondary motion, type " + z.Type + ": " + Zsm.TypeName(z.Type));
            sb.AppendLine("tag 0x" + z.Tag.ToString("x8") + ", flags 0x" + z.Flags.ToString("x8") + ", layout 0x" + z.LayoutFlags.ToString("x8"));
            if (usedBy != null)
            {
                var u = usedBy.ToList();
                if (u.Count > 0)
                {
                    sb.AppendLine("used by:");
                    foreach (var s in u) sb.AppendLine("  " + s);
                }
            }
            sb.AppendLine();
            switch (z.Type)
            {
                case 2:
                    foreach (var set in z.Sets)
                    {
                        sb.AppendLine(set.Name + ": " + set.Particles.Count + " particles, " + set.Edges.Count + " edges");
                        var bones = new Dictionary<uint, int>();
                        foreach (var p in set.Particles)
                            for (int k = 0; k < 8; k++)
                                if (p.Weights[k] != 0) bones[p.Bones[k]] = bones.TryGetValue(p.Bones[k], out var c) ? c + 1 : 1;
                        if (set.Particles.Count > 0)
                        {
                            var mn = set.Particles[0].Position; var mx = mn;
                            foreach (var p in set.Particles) { mn = Vector3.Min(mn, p.Position); mx = Vector3.Max(mx, p.Position); }
                            sb.AppendLine("  bounds " + F(mn) + " - " + F(mx));
                        }
                        sb.AppendLine("  skinned to " + bones.Count + " bones: " + string.Join(", ", bones.OrderByDescending(kv => kv.Value).Take(12).Select(kv => B(kv.Key) + " x" + kv.Value)) + (bones.Count > 12 ? ", ..." : ""));
                        if (set.NamedSets.Count > 0)
                            sb.AppendLine("  per-particle masks: " + string.Join(", ", set.NamedSets.Select(kv => NamedSetName(kv.Key))));
                        sb.AppendLine("  particles (x, y, z, w; bone:weight):");
                        for (int i = 0; i < set.Particles.Count; i++)
                        {
                            var p = set.Particles[i];
                            var bw = new List<string>();
                            for (int k = 0; k < 8; k++) if (p.Weights[k] != 0) bw.Add(B(p.Bones[k]) + ":" + p.Weights[k]);
                            sb.AppendLine("    " + i + ": " + F(p.Position) + " " + F(p.W) + "  " + string.Join(" ", bw));
                        }
                        sb.AppendLine();
                    }
                    if (z.SkinMap0 + z.SkinMap1 > 0) sb.AppendLine("vertex maps: " + z.SkinMap0 + " + " + z.SkinMap1 + " barycentric entries");
                    break;
                case 1:
                    sb.AppendLine("dangle bones: " + string.Join(", ", z.Bones.Select(B)));
                    foreach (var l in z.Links) sb.AppendLine("  " + l.Kind + ": " + B(l.BoneA) + " -> " + B(l.BoneB));
                    break;
                case 3:
                    if (z.HasBounds) sb.AppendLine("bounds " + F(z.BoundsMin) + " - " + F(z.BoundsMax));
                    sb.AppendLine("GPU buffer: " + z.GpuCount + " x " + z.GpuStride + " bytes (compute shader data, not decoded)");
                    sb.AppendLine("hashes: " + string.Join(", ", z.Hashes.Select(B)));
                    break;
                case 4:
                case 5:
                    for (int i = 0; i < z.Springs.Count; i++)
                    {
                        var s = z.Springs[i];
                        sb.AppendLine("spring " + i + ": offset " + F(new Vector3(s.Offset.X, s.Offset.Y, s.Offset.Z)) + ", rotation (" + F(s.Rotation.X) + ", " + F(s.Rotation.Y) + ", " + F(s.Rotation.Z) + ", " + F(s.Rotation.W) + "), extent " + F(s.Extent) + ", mode " + s.Mode + ", flags " + s.Flags);
                    }
                    foreach (var kv in z.SpringKeys) sb.AppendLine("key " + B(kv.Key) + ": springs " + string.Join(", ", kv.Value));
                    break;
            }
            return sb.ToString();
        }

        static string NamedSetName(uint h)
        {
            switch (h)
            {
                case 0xe4df46d5: return "default";
                case 0xe3da9c8b: return "legacy";
                default: return "0x" + h.ToString("x8");
            }
        }
    }
}
