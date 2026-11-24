using SharpDX;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI (RPF9 gen9 Xbox) drawables, drawable dictionaries and fragments -> CodeWalker Drawable objects
    /// for the model viewer. Port of rsc_mesh.py (see that file for the format notes).
    /// Positions, normals, tangents, colour, uv0, and the skeleton with blend weights/indices when the drawable
    /// has one (all its models are then skin meshes, so clips from <see cref="Rpf9Anim"/> can pose them).
    /// </summary>
    public static class Rpf9Drawable
    {
        const ulong P_CPU = 0x50000000, P_GPU = 0x60000000;
        static readonly int[][] AXES = { new[] { 2, 1, 0 }, new[] { 1, 2, 0 }, new[] { 0, 1, 2 } };
        static readonly int[] UV_SLOTS = { 3, 4, 14, 15 };

        public class BadDataException : Exception { public BadDataException(string m) : base(m) { } }

        /// <summary>One drawable found in a resource.</summary>
        public class Item
        {
            public int Root;          // offset in the CPU part
            public string Name;
            public uint Hash;         // dictionary hash (0 when not a dictionary item)
            public Drawable Drawable;
            public int Triangles;
            public int Bones;         // skeleton bone count, 0 without a skeleton
            public List<TexRef> TexRefs = new List<TexRef>();
        }

        /// <summary>Decoded geometry, before conversion (also used by the test harness).</summary>
        public class Mesh
        {
            public int VertexCount;
            public float[] Positions;   // 3 per vertex
            public float[] Normals;     // 3 per vertex or null
            public float[] Tangents;    // 4 per vertex or null
            public float[] Uv0;         // 2 per vertex or null
            public uint[] Colours;      // RGBA8 or null
            public byte[] Weights;      // 4 per vertex (unorm) or null
            public byte[] Joints;       // 4 per vertex (skeleton bone indices) or null
            public int[] Indices;       // 3 per triangle
            public int Shader;
        }

        // ------------------------------------------------------------------ helpers

        class Res
        {
            public byte[] C, G;
            public ulong U64(long o) => BitConverter.ToUInt64(C, (int)o);
            public uint U32(long o) => BitConverter.ToUInt32(C, (int)o);
            public ushort U16(long o) => BitConverter.ToUInt16(C, (int)o);
            public long CPtr(long o)
            {
                if (o < 0 || o + 8 > C.Length) return -1;
                var v = U64(o);
                return (v >= P_CPU && v < P_CPU + (ulong)C.Length) ? (long)(v - P_CPU) : -1;
            }
            public string CStr(long o)
            {
                if (o < 0 || o >= C.Length) return null;
                int e = (int)o, end = (int)Math.Min(C.Length, o + 256);
                while (e < end && C[e] != 0) { if (C[e] < 32 || C[e] >= 127) return null; e++; }
                if (e >= end || e == o) return null;
                return Encoding.ASCII.GetString(C, (int)o, e - (int)o);
            }
        }

        static void Bad(string m) { throw new BadDataException(m); }

        static long[] BufferObj(Res r, long o, uint type)
        {
            if (o < 0 || o + 0x20 > r.C.Length) Bad("buffer object");
            uint n = r.U32(o + 8);
            ushort es = r.U16(o + 12);
            uint t = r.U32(o + 16);
            ulong g = r.U64(o + 0x18);
            if (t != type || es != 4 || g < P_GPU || g >= P_GPU + 0x10000000) Bad("buffer type " + t.ToString("x"));
            return new[] { (long)(g - P_GPU), (long)n * 4 };
        }

        class GeomRef { public long VO, VS, MO, MS; public int Shader; }

        /// <summary>-> per LOD (index 0..3, null when missing): models -> geometries</summary>
        static List<GeomRef>[][] Walk(Res r, long root)
        {
            var lods = new List<GeomRef>[4][];
            bool any = false;
            for (int li = 0; li < 4; li++)
            {
                long lp = r.CPtr(root + 0x50 + 8 * li);
                if (lp < 0)
                {
                    if (root + 0x58 + 8 * li > r.C.Length || r.U64(root + 0x50 + 8 * li) != 0) Bad("lod pointer");
                    continue;
                }
                if (lp + 10 > r.C.Length) Bad("lod");
                long arr = r.CPtr(lp);
                int n = r.U16(lp + 8);
                if (arr < 0 || n <= 0 || n >= 256) Bad("model array");
                var models = new List<GeomRef>[n];
                for (int mi = 0; mi < n; mi++)
                {
                    long m = r.CPtr(arr + 8 * mi);
                    if (m < 0 || m + 0x28 > r.C.Length) Bad("model");
                    long garr = r.CPtr(m + 8);
                    int gn = r.U16(m + 0x10);
                    long sidx = r.CPtr(m + 0x20);
                    if (garr < 0 || gn <= 0 || gn >= 1024) Bad("geometry array");
                    var geoms = new List<GeomRef>();
                    for (int gi = 0; gi < gn; gi++)
                    {
                        long go = r.CPtr(garr + 8 * gi);
                        if (go < 0 || go + 0x48 > r.C.Length) Bad("geometry");
                        var vb = BufferObj(r, r.CPtr(go + 8), 0x1601);
                        long holder = r.CPtr(go + 0x40);
                        var mb = BufferObj(r, holder >= 0 ? r.CPtr(holder) : -1, 0x1380);
                        int mat = (sidx >= 0 && sidx + 2 * gi + 2 <= r.C.Length) ? r.U16(sidx + 2 * gi) : 0;
                        geoms.Add(new GeomRef { VO = vb[0], VS = vb[1], MO = mb[0], MS = mb[1], Shader = mat });
                    }
                    models[mi] = geoms;
                }
                lods[li] = models;
                any = true;
            }
            if (!any) Bad("no lods");
            return lods;
        }

        static List<Item> FindDrawables(Res r)
        {
            var outl = new List<Item>();
            bool TryRoot(long o, string fallback, uint hash)
            {
                try { Walk(r, o); }
                catch (BadDataException) { return false; }
                catch (ArgumentException) { return false; }
                string nm = (o + 0xb0 <= r.C.Length) ? r.CStr(r.CPtr(o + 0xa8)) : null;
                outl.Add(new Item { Root = (int)o, Name = nm ?? fallback, Hash = hash });
                return true;
            }
            if (r.C.Length >= 0xb0 && TryRoot(0, null, 0)) return outl;
            if (r.C.Length < 0x40) return outl;
            long items = r.CPtr(0x30);
            int n = r.U16(0x38);
            if (items >= 0 && n > 0 && n < 4096)
            {
                long hashes = r.CPtr(0x20);
                for (int i = 0; i < n; i++)
                {
                    if (items + 8 * i + 8 > r.C.Length) break;
                    long it = r.CPtr(items + 8 * i);
                    uint h = (hashes >= 0 && hashes + 4 * i + 4 <= r.C.Length) ? r.U32(hashes + 4 * i) : (uint)i;
                    if (it >= 0) TryRoot(it, h.ToString("x8"), h);
                }
                if (outl.Count > 0) return outl;
            }
            long fr = r.CPtr(0x18);
            if (fr >= 0) TryRoot(fr, null, 0);
            return outl;
        }

        // ------------------------------------------------------------------ geometry

        static Mesh DecodeVertices(byte[] g, long vo, long vs)
        {
            if (vs < 0x80 || vo + vs > g.Length) Bad("gpu data missing");
            int b0 = (int)vo, len = (int)vs;
            uint U(int o) => BitConverter.ToUInt32(g, b0 + o);
            uint magic = U(0);
            var slots = new List<int[]>(); // slot, offset, format
            int first = len;
            for (int i = 1; i <= 22; i++)
            {
                uint w = U(4 * i);
                if (w == 0) continue;
                slots.Add(new[] { i, (int)(w >> 7), (int)(w & 0x7f) });
                first = Math.Min(first, (int)(w >> 7));
            }
            var m = new Mesh();
            int nv;
            if (magic == 0x3063)
            {
                nv = (first - 0x60) / 12;
                if (nv < 0) Bad("vertex count");
                m.Positions = new float[3 * nv];
                Buffer.BlockCopy(g, b0 + 0x60, m.Positions, 0, 12 * nv);
            }
            else if (magic == 0x4042)
            {
                nv = (first - 0x80) / 8;
                if (nv < 0) Bad("vertex count");
                var f = new float[8];
                Buffer.BlockCopy(g, b0 + 0x60, f, 0, 32);
                uint k = U(0x6c);
                if (k > 2) Bad("axis code " + k);
                var ax = AXES[k];
                m.Positions = new float[3 * nv];
                var raw = new double[3];
                for (int v = 0; v < nv; v++)
                {
                    long w0 = U(0x80 + 8 * v), w1 = U(0x84 + 8 * v);
                    long q0 = w0 >> 8, q1 = (w0 & 0xff) | ((w1 & 0xffff) << 8), q2 = w1 >> 16;
                    raw[0] = q0 * (double)f[4] + f[0];
                    raw[1] = q1 * (double)f[5] + f[1];
                    raw[2] = q2 * (double)f[6] + f[2];
                    m.Positions[3 * v] = (float)raw[ax[0]];
                    m.Positions[3 * v + 1] = (float)raw[ax[1]];
                    m.Positions[3 * v + 2] = (float)raw[ax[2]];
                }
            }
            else { Bad("vertex block magic " + magic.ToString("x")); return null; }
            m.VertexCount = nv;

            foreach (var s in slots)
            {
                int slot = s[0], o = s[1], fm = s[2];
                int es = fm == 0x42 ? 8 : (fm == 0x63 && magic == 0x3063) ? 12 : 4;
                if ((long)o + (long)es * nv > len) Bad("stream past block end");
                int p = b0 + o;
                int uvi = Array.IndexOf(UV_SLOTS, slot);
                if (slot == 2 && fm == 0x63 && magic == 0x3063)
                {
                    m.Normals = new float[3 * nv];
                    Buffer.BlockCopy(g, p, m.Normals, 0, 12 * nv);
                }
                else if (slot == 2 && fm == 0x21 && magic == 0x4042)
                {
                    m.Normals = new float[3 * nv];
                    m.Tangents = new float[4 * nv];
                    for (int v = 0; v < nv; v++) NormalTangent(BitConverter.ToUInt32(g, p + 4 * v), m.Normals, m.Tangents, v);
                }
                else if (slot == 2 && fm == 0x27 && magic == 0x4042)
                {
                    m.Normals = new float[3 * nv];
                    for (int v = 0; v < nv; v++) Unit1010102(BitConverter.ToUInt32(g, p + 4 * v), m.Normals, v);
                }
                else if (uvi == 0 && fm == 0x28 && magic == 0x4042)
                {
                    m.Uv0 = new float[2 * nv];
                    for (int v = 0; v < 2 * nv; v++) m.Uv0[v] = BitConverter.ToInt16(g, p + 2 * v) / 8192.0f;
                }
                else if (uvi == 0 && fm == 0x42)
                {
                    m.Uv0 = new float[2 * nv];
                    Buffer.BlockCopy(g, p, m.Uv0, 0, 8 * nv);
                }
                else if (slot == 1 && fm == 0x26 && magic == 0x4042)
                {
                    m.Colours = new uint[nv];
                    Buffer.BlockCopy(g, p, m.Colours, 0, 4 * nv);
                }
                else if (slot == 6 && fm == 0x26)
                {
                    m.Weights = new byte[4 * nv];
                    Buffer.BlockCopy(g, p, m.Weights, 0, 4 * nv);
                }
                else if (slot == 8 && fm == 0x25)
                {
                    m.Joints = new byte[4 * nv];
                    Buffer.BlockCopy(g, p, m.Joints, 0, 4 * nv);
                }
            }
            return m;
        }

        static void Octahedral(double u, double v, out double x, out double y, out double z)
        {
            z = 1 - Math.Abs(u) - Math.Abs(v);
            x = z < 0 ? (1 - Math.Abs(v)) * (u >= 0 ? 1 : -1) : u;
            y = z < 0 ? (1 - Math.Abs(u)) * (v >= 0 ? 1 : -1) : v;
            double l = Math.Sqrt(x * x + y * y + z * z);
            x /= l; y /= l; z /= l;
        }

        static void NormalTangent(uint w, float[] n, float[] t, int i)
        {
            Octahedral((w & 1023) / 511.5 - 1, ((w >> 10) & 1023) / 511.5 - 1, out var nx, out var ny, out var nz);
            double s = nz >= 0 ? 1.0 : -1.0;
            double a = -1 / (s + nz);
            double bb = nx * ny * a;
            double b1x = 1 + s * nx * nx * a, b1y = s * bb, b1z = -s * nx;
            double b2x = bb, b2y = s + ny * ny * a, b2z = -ny;
            double th = -2 * Math.PI * (((w >> 20) & 1023) + 1) / 1024;
            double c = Math.Cos(th), sn = Math.Sin(th);
            n[3 * i] = (float)nx; n[3 * i + 1] = (float)ny; n[3 * i + 2] = (float)nz;
            t[4 * i] = (float)(c * b1x + sn * b2x);
            t[4 * i + 1] = (float)(c * b1y + sn * b2y);
            t[4 * i + 2] = (float)(c * b1z + sn * b2z);
            t[4 * i + 3] = ((w >> 30) & 1) != 0 ? 1f : -1f;
        }

        static void Unit1010102(uint w, float[] n, int i)
        {
            double x = (w & 1023) / 511.5 - 1, y = ((w >> 10) & 1023) / 511.5 - 1, z = ((w >> 20) & 1023) / 511.5 - 1;
            double l = Math.Sqrt(x * x + y * y + z * z) + 1e-30;
            n[3 * i] = (float)(x / l); n[3 * i + 1] = (float)(y / l); n[3 * i + 2] = (float)(z / l);
        }

        static int[] DecodeMeshlets(byte[] g, long mo, long ms, int nv)
        {
            if (ms < 12 || mo + ms > g.Length) Bad("gpu data missing");
            int b0 = (int)mo;
            long nwords = ms / 4;
            uint U(long wi) => BitConverter.ToUInt32(g, (int)(b0 + 4 * wi));
            uint h = U(0);
            if ((h >> 31) != 1) Bad("meshlet block magic " + h.ToString("x"));
            int nm = (int)(h & 0xffff);
            long ntri = U(1), nvl = U(2);
            long triw = 3 + 4L * nm;
            long vlb = b0 + 4 * (triw + ntri);
            if (triw + ntri > nwords || vlb + 2 * nvl > b0 + ms) Bad("meshlet block size");
            var idx = new List<int>((int)Math.Min(ntri * 3, int.MaxValue / 2));
            long vbase = 0, tbase = 0, pvo = -1, pto = -1;
            for (int i = 0; i < nm; i++)
            {
                uint a = U(3 + 4 * i), bb = U(4 + 4 * i);
                int mv = (int)(a & 0x7f) + 1, mt = (int)((a >> 7) & 0x7f) + 1;
                long vo = bb & 0xffff, to = bb >> 16;
                if (vo + vbase < pvo) vbase += 0x10000;
                if (to + tbase < pto) tbase += 0x10000;
                vo += vbase; to += tbase;
                pvo = vo; pto = to;
                long tend = Math.Min(to + mt, ntri);
                for (long t = to; t < tend; t++)
                {
                    uint tr = U(triw + t);
                    for (int k = 0; k < 3; k++)
                    {
                        int loc = (int)((tr >> (7 * k)) & 0x7f);
                        if (loc >= mv) Bad("meshlet index out of range");
                        long li = vo + loc;
                        if (li >= nvl) Bad("vertex list index");
                        int vi = BitConverter.ToUInt16(g, (int)(vlb + 2 * li));
                        if (vi >= nv) Bad("vertex index " + vi + " >= " + nv);
                        idx.Add(vi);
                    }
                }
            }
            return idx.ToArray();
        }

        // ------------------------------------------------------------------ materials

        /// <summary>shader name from the hash list (strings_gta6.txt), or null</summary>
        public static string ShaderName(uint h) => Rpf9Strings.Get(h);

        /// <summary>A shader's texture reference: slot in the shader's texture list, joaat(texture name), 64-bit
        /// texture key (= TTDB hash for streamed textures, 0 if none).</summary>
        public class TexRef
        {
            public int Shader, Slot;
            public uint Hash;
            public ulong Key;
            public override string ToString() => Shader + "/" + Slot + " " + Hash.ToString("x8") + (Key != 0 ? " key " + Key.ToString("x16") : "");
        }

        static List<TexRef> TexRefs(Res r, long s, int shader)
        {
            var res = new List<TexRef>();
            long ta = r.CPtr(s + 0x10);
            for (int k = 0; ta >= 0 && k < 32; k++)
            {
                if (ta + 8 * k + 8 > r.C.Length) break;
                long t = r.CPtr(ta + 8 * k);
                if (t < 0)
                {
                    if (r.U64(ta + 8 * k) != 0) break;
                    continue;
                }
                if (t + 0x38 > r.C.Length || r.U16(t + 0x10) != 0x980) break;
                long nr = r.CPtr(t + 0x30);
                if (nr < 8) continue;
                var tr = new TexRef { Shader = shader, Slot = k, Hash = r.U32(nr - 8) };
                if (nr + 16 <= r.C.Length) tr.Key = r.U64(nr + 8);
                res.Add(tr);
            }
            return res;
        }

        static ShaderFX[] Shaders(Res r, long root, int needed, List<TexRef> texrefs = null)
        {
            var list = new List<ShaderFX>();
            long sg = r.CPtr(root + 0x10);
            if (sg >= 0 && sg + 0x1a <= r.C.Length)
            {
                long arr = r.CPtr(sg + 0x10);
                int n = arr >= 0 ? r.U16(sg + 0x18) : 0;
                for (int i = 0; i < n; i++)
                {
                    long s = (arr + 8 * i + 8 <= r.C.Length) ? r.CPtr(arr + 8 * i) : -1;
                    bool ok = s >= 0 && s + 0x18 <= r.C.Length;
                    uint h = ok ? r.U32(s) : 0;
                    list.Add(MakeShader(h));
                    if (ok && texrefs != null) texrefs.AddRange(TexRefs(r, s, i));
                }
            }
            while (list.Count < needed) list.Add(MakeShader(0));
            return list.ToArray();
        }

        static ShaderFX MakeShader(uint h)
        {
            var nm = ShaderName(h);
            var sh = new ShaderFX
            {
                Name = new MetaHash(h),
                FileName = new MetaHash(nm != null ? JenkHash.GenHash(nm + ".sps") : 0),
                RenderBucket = 0,
            };
            sh.ParametersList = new ShaderParametersBlock
            {
                Parameters = new ShaderParameter[0],
                Hashes = new MetaName[0],
                Owner = sh,
            };
            return sh;
        }

        // ------------------------------------------------------------------ skeleton

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Skeleton, Dictionary<uint, ushort>> boneHashes =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Skeleton, Dictionary<uint, ushort>>();

        /// <summary>
        /// Skeleton at drawable +0x18 as a CodeWalker Skeleton, or null. Bone.Tag = bone index (GTA VI animations
        /// address bones by joaat(name), see <see cref="BoneHashMap"/>). Layout: +0x28 bones (0x50 each),
        /// +0x30 inverse bind matrices (16 f32, column-major), +0x40 i16 parents, +0x6c u16 count.
        /// Bone: +0 quat, +0x10 translation, +0x1c joaat(name), +0x20 scale, +0x30 name ptr.
        /// </summary>
        static Skeleton ReadSkeleton(Res r, long root)
        {
            long sk = r.CPtr(root + 0x18);
            if (sk < 0 || sk + 0x70 > r.C.Length) return null;
            int n = r.U16(sk + 0x6c);
            long ba = r.CPtr(sk + 0x28), ib = r.CPtr(sk + 0x30), pa = r.CPtr(sk + 0x40);
            if (n == 0 || ba < 0 || ib < 0 || pa < 0 || ba + 0x50L * n > r.C.Length || ib + 0x40L * n > r.C.Length || pa + 2L * n > r.C.Length)
                return null;
            float F(long o) => BitConverter.ToSingle(r.C, (int)o);
            var bones = new Bone[n];
            var parents = new short[n];
            var inv = new Matrix[n];
            var hashes = new Dictionary<uint, ushort>();
            for (int i = 0; i < n; i++)
            {
                long b = ba + 0x50L * i;
                short par = BitConverter.ToInt16(r.C, (int)(pa + 2 * i));
                if (par >= i) return null;
                parents[i] = par;
                string name = r.CStr(r.CPtr(b + 0x30)) ?? ("bone" + i);
                var bone = new Bone
                {
                    Rotation = new Quaternion(F(b), F(b + 4), F(b + 8), F(b + 12)),
                    Translation = new Vector3(F(b + 0x10), F(b + 0x14), F(b + 0x18)),
                    Scale = new Vector3(F(b + 0x20), F(b + 0x24), F(b + 0x28)),
                    Index = (short)i,
                    Tag = (ushort)i,
                    ParentIndex = par,
                    NextSiblingIndex = -1,
                    Name = name,
                };
                bone.AnimRotation = bone.Rotation;
                bone.AnimTranslation = bone.Translation;
                bone.AnimScale = bone.Scale;
                bones[i] = bone;
                uint h = r.U32(b + 0x1c);
                if (h != 0 && !hashes.ContainsKey(h)) hashes[h] = (ushort)i;
                uint hn = JenkHash.GenHash(name.ToLowerInvariant());
                if (!hashes.ContainsKey(hn)) hashes[hn] = (ushort)i;
                // stored column-major for column vectors = row-major for SharpDX's row vectors
                long m = ib + 0x40L * i;
                var mx = new Matrix();
                for (int k = 0; k < 16; k++) mx[k] = F(m + 4 * k);
                mx.M14 = 0; mx.M24 = 0; mx.M34 = 0; mx.M44 = 1;
                inv[i] = mx;
            }
            var skel = new Skeleton
            {
                Bones = new SkeletonBonesBlock { Items = bones, Count = (uint)n },
                ParentIndices = parents,
                TransformationsInverted = inv,
                BonesCount = (ushort)n,
            };
            skel.AssignBoneParents();
            skel.BuildBonesMap();
            boneHashes.Add(skel, hashes);
            return skel;
        }

        /// <summary>joaat(bone name) -> Bone.Tag for a skeleton read from a GTA VI drawable (null for other skeletons).</summary>
        public static Dictionary<uint, ushort> BoneHashMap(Skeleton skel)
        {
            if (skel == null) return null;
            return boneHashes.TryGetValue(skel, out var m) ? m : null;
        }

        // ------------------------------------------------------------------ CodeWalker objects

        const int STRIDE = 52; // VertexType.DefaultEx: pos f3, normal f3, colour u32, uv0 f2, tangent f4
        const int STRIDE_SKIN = 60; // VertexType.PBBNCTX: pos f3, weights u32, indices u32, normal f3, colour u32, uv0 f2, tangent f4

        static VertexDeclaration DeclDefaultEx() => new VertexDeclaration
        {
            Flags = (uint)VertexType.DefaultEx,
            Stride = STRIDE,
            Count = 5,
            Types = VertexDeclarationTypes.GTAV1,
        };

        static VertexDeclaration DeclSkin() => new VertexDeclaration
        {
            Flags = (uint)VertexType.PBBNCTX,
            Stride = STRIDE_SKIN,
            Count = 7,
            Types = VertexDeclarationTypes.GTAV1,
        };

        /// <summary>Make the mesh's blend data usable for a skeleton of nbones bones: weights summing to 255 and
        /// indices in range (the renderer's bone buffer holds 255 matrices); bone 0 when there is none.</summary>
        static void FixSkin(Mesh m, int nbones)
        {
            int nv = m.VertexCount;
            bool ok = m.Weights != null && m.Joints != null;
            if (ok) foreach (var j in m.Joints) if (j >= nbones) { ok = false; break; }
            if (!ok)
            {
                m.Weights = new byte[4 * nv];
                m.Joints = new byte[4 * nv];
                for (int v = 0; v < nv; v++) m.Weights[4 * v] = 255;
                return;
            }
            for (int v = 0; v < nv; v++)
            {
                int sum = 0, big = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += m.Weights[4 * v + k];
                    if (m.Weights[4 * v + k] > m.Weights[4 * v + big]) big = k;
                    if (m.Joints[4 * v + k] > 254) m.Joints[4 * v + k] = 254;
                }
                m.Weights[4 * v + big] = (byte)Math.Max(0, Math.Min(255, m.Weights[4 * v + big] + 255 - sum));
            }
        }

        /// <summary>Split a mesh into DrawableGeometries of at most 65535 vertices (ushort indices).</summary>
        static void AddGeometries(Mesh m, ShaderFX shader, List<DrawableGeometry> geoms, List<AABB_s> bounds)
        {
            int ntri = m.Indices.Length / 3;
            var map = new int[m.VertexCount];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            var used = new List<int>();
            var idx = new List<ushort>();
            int t = 0;
            while (t < ntri)
            {
                used.Clear(); idx.Clear();
                for (; t < ntri; t++)
                {
                    int newv = 0;
                    for (int k = 0; k < 3; k++) if (map[m.Indices[3 * t + k]] < 0) newv++;
                    if (used.Count + newv > 65535) break;
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = m.Indices[3 * t + k];
                        if (map[vi] < 0) { map[vi] = used.Count; used.Add(vi); }
                        idx.Add((ushort)map[vi]);
                    }
                }
                geoms.Add(MakeGeometry(m, used, idx.ToArray(), shader, out var bb));
                bounds.Add(bb);
                foreach (var vi in used) map[vi] = -1;
            }
        }

        static DrawableGeometry MakeGeometry(Mesh m, List<int> verts, ushort[] indices, ShaderFX shader, out AABB_s bb)
        {
            int n = verts.Count;
            bool skin = m.Weights != null && m.Joints != null;
            int stride = skin ? STRIDE_SKIN : STRIDE;
            var vb = new byte[n * stride];
            var mn = new Vector3(float.MaxValue);
            var mx = new Vector3(float.MinValue);
            for (int i = 0; i < n; i++)
            {
                int v = verts[i], o = i * stride;
                float px = m.Positions[3 * v], py = m.Positions[3 * v + 1], pz = m.Positions[3 * v + 2];
                Put(vb, o, px); Put(vb, o + 4, py); Put(vb, o + 8, pz);
                mn = Vector3.Min(mn, new Vector3(px, py, pz));
                mx = Vector3.Max(mx, new Vector3(px, py, pz));
                if (skin)
                {
                    Buffer.BlockCopy(m.Weights, 4 * v, vb, o + 12, 4);
                    Buffer.BlockCopy(m.Joints, 4 * v, vb, o + 16, 4);
                    o += 8; // the rest is laid out as DefaultEx
                }
                if (m.Normals != null) { Put(vb, o + 12, m.Normals[3 * v]); Put(vb, o + 16, m.Normals[3 * v + 1]); Put(vb, o + 20, m.Normals[3 * v + 2]); }
                else Put(vb, o + 20, 1f);
                uint col = m.Colours != null ? m.Colours[v] : 0xffffffff;
                Buffer.BlockCopy(BitConverter.GetBytes(col), 0, vb, o + 24, 4);
                if (m.Uv0 != null) { Put(vb, o + 28, m.Uv0[2 * v]); Put(vb, o + 32, m.Uv0[2 * v + 1]); }
                if (m.Tangents != null)
                {
                    Put(vb, o + 36, m.Tangents[4 * v]); Put(vb, o + 40, m.Tangents[4 * v + 1]);
                    Put(vb, o + 44, m.Tangents[4 * v + 2]); Put(vb, o + 48, m.Tangents[4 * v + 3]);
                }
                else { Put(vb, o + 36, 1f); Put(vb, o + 48, 1f); }
            }
            bb = new AABB_s { Min = new Vector4(mn, 0), Max = new Vector4(mx, 0) };
            var decl = skin ? DeclSkin() : DeclDefaultEx();
            var vd = new VertexData
            {
                VertexStride = stride,
                VertexCount = n,
                Info = decl,
                VertexType = skin ? VertexType.PBBNCTX : VertexType.DefaultEx,
                VertexBytes = vb,
            };
            var g = new DrawableGeometry
            {
                VertexData = vd,
                VertexBuffer = new VertexBuffer { VertexStride = (ushort)stride, VertexCount = (uint)n, Info = decl, Data1 = vd },
                IndexBuffer = new IndexBuffer { Indices = indices, IndicesCount = (uint)indices.Length },
                IndicesCount = (uint)indices.Length,
                TrianglesCount = (uint)(indices.Length / 3),
                VerticesCount = (ushort)n,
                VertexStride = (ushort)stride,
                Shader = shader,
                AABB = bb,
            };
            return g;
        }

        static void Put(byte[] b, int o, float v)
        {
            var x = BitConverter.GetBytes(v);
            b[o] = x[0]; b[o + 1] = x[1]; b[o + 2] = x[2]; b[o + 3] = x[3];
        }

        /// <summary>Decoded meshes of one drawable: per LOD (null when missing), per model, per geometry.</summary>
        static List<Mesh>[][] Meshes(Res r, long root)
        {
            var lods = Walk(r, root);
            var outl = new List<Mesh>[4][];
            for (int li = 0; li < 4; li++)
            {
                if (lods[li] == null) continue;
                outl[li] = new List<Mesh>[lods[li].Length];
                for (int mi = 0; mi < lods[li].Length; mi++)
                {
                    var list = new List<Mesh>();
                    foreach (var gr in lods[li][mi])
                    {
                        var m = DecodeVertices(r.G, gr.VO, gr.VS);
                        m.Indices = DecodeMeshlets(r.G, gr.MO, gr.MS, m.VertexCount);
                        m.Shader = gr.Shader;
                        if (m.Indices.Length > 0) list.Add(m);
                    }
                    outl[li][mi] = list;
                }
            }
            return outl;
        }

        static Drawable Build(Res r, Item it)
        {
            var meshes = Meshes(r, it.Root);
            var skel = ReadSkeleton(r, it.Root);
            int nbones = skel?.Bones?.Items?.Length ?? 0;
            int maxShader = 0;
            foreach (var lod in meshes)
                if (lod != null) foreach (var mdl in lod) foreach (var m in mdl)
                        {
                            maxShader = Math.Max(maxShader, m.Shader + 1);
                            if (nbones > 0) FixSkin(m, nbones);
                            else { m.Weights = null; m.Joints = null; }
                        }
            var shaders = Shaders(r, it.Root, maxShader, it.TexRefs);

            var d = new Drawable
            {
                Name = it.Name,
                LightAttributes = new ResourceSimpleList64<LightAttributes> { data_items = new LightAttributes[0] },
            };
            d.ShaderGroup = new ShaderGroup
            {
                Shaders = new ResourcePointerArray64<ShaderFX> { data_items = shaders },
                ShadersCount1 = (ushort)shaders.Length,
                ShadersCount2 = (ushort)shaders.Length,
            };
            var dm = new DrawableModelsBlock { Owner = d };
            var all = new List<DrawableModel>();
            var bmin = new Vector3(float.MaxValue);
            var bmax = new Vector3(float.MinValue);
            int ntri = 0;
            for (int li = 0; li < 4; li++)
            {
                if (meshes[li] == null) continue;
                var models = new List<DrawableModel>();
                foreach (var mdl in meshes[li])
                {
                    var geoms = new List<DrawableGeometry>();
                    var bounds = new List<AABB_s>();
                    foreach (var m in mdl)
                    {
                        AddGeometries(m, shaders[m.Shader], geoms, bounds);
                        ntri += m.Indices.Length / 3;
                    }
                    if (geoms.Count == 0) continue;
                    var shaderMap = new ushort[geoms.Count];
                    for (int i = 0; i < geoms.Count; i++)
                    {
                        int si = Array.IndexOf(shaders, geoms[i].Shader);
                        shaderMap[i] = (ushort)Math.Max(si, 0);
                        geoms[i].ShaderID = shaderMap[i];
                    }
                    models.Add(new DrawableModel
                    {
                        Geometries = geoms.ToArray(),
                        GeometriesCount1 = (ushort)geoms.Count,
                        GeometriesCount2 = (ushort)geoms.Count,
                        GeometriesCount3 = (ushort)geoms.Count,
                        ShaderMapping = shaderMap,
                        BoundsData = bounds.ToArray(),
                        SkeletonBinding = nbones > 0 ? 0x100u : 0u, // skin mesh, bone 0
                        RenderMaskFlags = 0x00FF,
                    });
                }
                if (models.Count == 0) continue;
                var arr = models.ToArray();
                switch (li)
                {
                    case 0: dm.High = arr; break;
                    case 1: dm.Med = arr; break;
                    case 2: dm.Low = arr; break;
                    default: dm.VLow = arr; break;
                }
                all.AddRange(arr);
            }
            if (all.Count == 0) Bad("no triangles");
            if (dm.High == null) { dm.High = dm.Med ?? dm.Low ?? dm.VLow; if (dm.High == dm.Med) dm.Med = null; else if (dm.High == dm.Low) dm.Low = null; else dm.VLow = null; }
            foreach (var mdl in dm.High) foreach (var bb in mdl.BoundsData) { bmin = Vector3.Min(bmin, bb.Min.XYZ()); bmax = Vector3.Max(bmax, bb.Max.XYZ()); }
            d.DrawableModels = dm;
            d.AllModels = all.ToArray();
            d.Skeleton = skel;
            it.Bones = nbones;
            d.BoundingBoxMin = bmin;
            d.BoundingBoxMax = bmax;
            d.BoundingCenter = (bmin + bmax) * 0.5f;
            d.BoundingSphereRadius = Math.Max((bmax - bmin).Length() * 0.5f, 0.01f);
            float LodDist(int i) => (it.Root + 0x70 + 4 * i + 4 <= r.C.Length) ? BitConverter.ToSingle(r.C, it.Root + 0x70 + 4 * i) : 9999f;
            d.LodDistHigh = LodDist(0); d.LodDistMed = LodDist(1); d.LodDistLow = LodDist(2); d.LodDistVlow = LodDist(3);
            it.Triangles = ntri;
            return d;
        }

        static Res Split(byte[] data, long virtualSize)
        {
            var cpu = new byte[Math.Min(Math.Max(virtualSize, 0), data.Length)];
            Buffer.BlockCopy(data, 0, cpu, 0, cpu.Length);
            var gpu = new byte[data.Length - cpu.Length];
            Buffer.BlockCopy(data, cpu.Length, gpu, 0, gpu.Length);
            return new Res { C = cpu, G = gpu };
        }

        /// <summary>All drawables in a GTA VI .zdr/.zdd/.zft resource (data = CPU part then GPU part,
        /// virtualSize = Rpf9FileEntry.VirtualSize). Drawables that fail to decode are skipped with a note.</summary>
        public static List<Item> Read(byte[] data, long virtualSize, List<string> notes = null)
        {
            var r = Split(data, virtualSize);
            var items = FindDrawables(r);
            var outl = new List<Item>();
            foreach (var it in items)
            {
                try
                {
                    it.Drawable = Build(r, it);
                    outl.Add(it);
                }
                catch (BadDataException ex) { notes?.Add((it.Name ?? "drawable") + ": " + ex.Message); }
                catch (ArgumentException ex) { notes?.Add((it.Name ?? "drawable") + ": " + ex.Message); }
                catch (IndexOutOfRangeException ex) { notes?.Add((it.Name ?? "drawable") + ": " + ex.Message); }
            }
            return outl;
        }

        /// <summary>Test helper: per drawable name, vertex and triangle totals over all LODs (as rsc_mesh.py counts).</summary>
        public static List<string> Summary(byte[] data, long virtualSize)
        {
            var r = Split(data, virtualSize);
            var res = new List<string>();
            foreach (var it in FindDrawables(r))
            {
                try
                {
                    var ms = Meshes(r, it.Root);
                    long nv = 0, nt = 0; double sum = 0;
                    foreach (var lod in ms)
                        if (lod != null) foreach (var mdl in lod) foreach (var m in mdl)
                                {
                                    nv += m.VertexCount; nt += m.Indices.Length / 3;
                                    foreach (var p in m.Positions) sum += p;
                                    foreach (var ix in m.Indices) sum += ix;
                                }
                    res.Add(string.Format("{0}\t{1}\t{2}\t{3:R}", it.Name, nv, nt, Math.Round(sum, 2)));
                }
                catch (Exception ex) { res.Add(it.Name + "\tBAD\t" + ex.Message); }
            }
            return res;
        }

        /// <summary>CodeWalker file objects for the model viewer: a YdrFile for one drawable, a YddFile for several.</summary>
        public static GameFile ToGameFile(string name, RpfFileEntry entry, List<Item> items)
        {
            if (items == null || items.Count == 0) return null;
            if (items.Count == 1)
            {
                var ydr = new YdrFile { Name = name, RpfFileEntry = entry, Drawable = items[0].Drawable, Loaded = true };
                items[0].Drawable.Owner = ydr;
                return ydr;
            }
            var ydd = new YddFile { Name = name, RpfFileEntry = entry, Loaded = true };
            var dict = new Dictionary<uint, Drawable>();
            var list = new List<Drawable>();
            foreach (var it in items)
            {
                uint h = it.Hash != 0 ? it.Hash : JenkHash.GenHash((it.Name ?? list.Count.ToString()).ToLowerInvariant());
                while (dict.ContainsKey(h)) h++;
                dict[h] = it.Drawable;
                list.Add(it.Drawable);
                it.Drawable.Owner = ydd;
            }
            ydd.Dict = dict;
            ydd.Drawables = list.ToArray();
            return ydd;
        }
    }
}
