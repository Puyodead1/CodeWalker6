using SharpDX;
using System;
using System.Collections.Generic;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI physics bounds (.zbn, and the composite bound of a .zft fragment), read into the GTA V Bounds classes so
    /// the existing collision renderer can show them. Layout: data\bounds_format.md of the research project.
    /// The base header (0x70 B) is GTA V's phBound. Types: 0 sphere, 1 capsule, 2 tapered capsule, 3 box,
    /// 4 geometry, 5 BVH geometry, 6 composite, 9 cylinder, 13 box as 8 corners, 14 cylinder variant.
    /// Polygons are 16 B: u8 edge[3], u8 flags, u16 vertex[3], s16 neighbour[3]; vertex = s16[3] * quantum.
    /// Material ids (15 bit) aren't mapped to GTA V material types, so everything gets the DEFAULT colour.
    /// </summary>
    public static class Rpf9Bounds
    {
        private const ulong CpuBase = 0x50000000;

        public static string TypeName(int t)
        {
            switch (t)
            {
                case 0: return "sphere";
                case 1: return "capsule";
                case 2: return "tapered capsule";
                case 3: return "box";
                case 4: return "geometry";
                case 5: return "BVH";
                case 6: return "composite";
                case 9: return "cylinder";
                case 13: return "box (8 corners)";
                case 14: return "cylinder (type 14)";
                default: return "type " + t;
            }
        }

        private class Reader
        {
            public byte[] B;
            public int Cpu;
            public Dictionary<int, int> Counts = new Dictionary<int, int>();
            public int Polygons;

            public int Ptr(int o)
            {
                ulong v = BitConverter.ToUInt64(B, o);
                if (v == 0) return -1;
                if (v < CpuBase || v >= CpuBase + (ulong)Cpu) throw new Exception("pointer 0x" + v.ToString("x") + " at 0x" + o.ToString("x") + " is outside the CPU half");
                return (int)(v - CpuBase);
            }
            public Vector3 V3(int o) => new Vector3(BitConverter.ToSingle(B, o), BitConverter.ToSingle(B, o + 4), BitConverter.ToSingle(B, o + 8));
            public float F(int o) => BitConverter.ToSingle(B, o);
        }

        /// <summary>The bound of a .zbn (at 0) or a .zft (fragment +0x10 -> physics object, +0x20 -> composite).
        /// Returns null when there is none. summary: counts per type, for the status bar.</summary>
        public static Bounds Read(byte[] data, int cpuSize, string ext, out string summary)
        {
            summary = "";
            var r = new Reader { B = data, Cpu = Math.Min(cpuSize > 0 ? cpuSize : data.Length, data.Length) };
            if (r.Cpu < 0x70) return null;
            int at = 0;
            if (ext == ".zft")
            {
                int x = r.Ptr(0x10);
                at = x >= 0 ? r.Ptr(x + 0x20) : -1;
                if (at < 0) return null;
            }
            var b = ReadBound(r, at, 0);
            var parts = new List<string>();
            foreach (var kv in r.Counts) parts.Add(kv.Value + " " + TypeName(kv.Key));
            summary = string.Join(", ", parts) + (r.Polygons > 0 ? ", " + r.Polygons.ToString("N0") + " polygons" : "");
            return b;
        }

        private static void Base(Reader r, int at, Bounds b)
        {
            b.SphereRadius = r.F(at + 0x14);
            b.BoxMax = r.V3(at + 0x20);
            b.Margin = r.F(at + 0x2c);
            b.BoxMin = r.V3(at + 0x30);
            b.BoxCenter = r.V3(at + 0x40);
            b.SphereCenter = r.V3(at + 0x50);
            b.Unknown_60h = r.V3(at + 0x60);
            b.Volume = r.F(at + 0x6c);
        }

        private static Bounds ReadBound(Reader r, int at, int depth)
        {
            if (depth > 8) throw new Exception("bound nesting too deep");
            int t = r.B[at + 0x10];
            r.Counts[t] = (r.Counts.TryGetValue(t, out int c) ? c : 0) + 1;
            Bounds b;
            switch (t)
            {
                case 6: b = ReadComposite(r, at, depth); break;
                case 4:
                case 5: b = ReadGeometry(r, at, t); break;
                case 0: b = new BoundSphere { Type = BoundsType.Sphere }; break;
                case 1: b = new BoundCapsule { Type = BoundsType.Capsule }; break;
                case 9:
                case 14: b = new BoundCylinder { Type = BoundsType.Cylinder }; break;
                case 2: b = new BoundCapsule { Type = BoundsType.Capsule }; break;
                default: b = new BoundBox { Type = BoundsType.Box }; break;        // 3, 13 and anything unknown: its box
            }
            if (!(b is BoundComposite) && !(b is BoundGeometry)) Base(r, at, b);
            if (t == 2)
            {
                // tapered capsule: +0x70 radius 1, radius 2, length -> a capsule with the larger radius; the renderer
                // takes the half length (caps included) from SphereRadius, which is the box's Y half extent for capsules
                float r1 = r.F(at + 0x70), r2 = r.F(at + 0x74);
                b.Margin = Math.Max(r1, r2);
                b.SphereRadius = Math.Max((b.BoxMax.Y - b.BoxMin.Y) * 0.5f, b.Margin);
            }
            return b;
        }

        private static BoundComposite ReadComposite(Reader r, int at, int depth)
        {
            var comp = new BoundComposite { Type = BoundsType.Composite };
            Base(r, at, comp);
            int cp = r.Ptr(at + 0x70), tp = r.Ptr(at + 0x78);
            int n = BitConverter.ToUInt16(r.B, at + 0x98);
            var kids = new Bounds[n];
            for (int i = 0; i < n; i++)
            {
                int ca = cp >= 0 ? r.Ptr(cp + 8 * i) : -1;
                if (ca < 0) continue;
                var k = ReadBound(r, ca, depth + 1);
                if (tp >= 0)
                {
                    var m = new Matrix();
                    for (int j = 0; j < 16; j++) m[j] = r.F(tp + 64 * i + 4 * j);
                    m.M44 = 1.0f;
                    k.Transform = m;
                    k.TransformInv = Matrix.Invert(m);
                }
                k.Parent = comp;
                kids[i] = k;
            }
            comp.Children = new ResourcePointerArray64<Bounds> { data_items = kids };
            return comp;
        }

        private static BoundGeometry ReadGeometry(Reader r, int at, int type)
        {
            BoundGeometry g = type == 5 ? new BoundBVH { Type = BoundsType.GeometryBVH } : new BoundGeometry { Type = BoundsType.Geometry };
            Base(r, at, g);
            var quantum = r.V3(at + 0x90);
            g.Quantum = quantum;
            g.CenterGeom = r.V3(at + 0xa0);
            int nv = BitConverter.ToInt32(r.B, at + 0xd0), np = BitConverter.ToInt32(r.B, at + 0xd4);
            int vp = r.Ptr(at + 0xb0), pp = r.Ptr(at + 0x88), mp = r.Ptr(at + 0xf0), mi = r.Ptr(at + 0x118);
            int nm = r.B[at + 0x120];
            var verts = new Vector3[vp >= 0 ? nv : 0];
            for (int i = 0; i < verts.Length; i++)
            {
                int o = vp + 6 * i;
                verts[i] = new Vector3(BitConverter.ToInt16(r.B, o), BitConverter.ToInt16(r.B, o + 2), BitConverter.ToInt16(r.B, o + 4)) * quantum;
            }
            g.Vertices = verts;
            g.VerticesCount = (uint)verts.Length;
            var polys = new BoundPolygon[pp >= 0 ? np : 0];
            for (int i = 0; i < polys.Length; i++)
            {
                int o = pp + 16 * i;
                var p = new BoundPolygonTriangle
                {
                    Owner = g,
                    Index = i,
                    edgeIndex1 = BitConverter.ToUInt16(r.B, o + 10),
                    edgeIndex2 = BitConverter.ToUInt16(r.B, o + 12),
                    edgeIndex3 = BitConverter.ToUInt16(r.B, o + 14),
                };
                p.vertIndex1 = BitConverter.ToUInt16(r.B, o + 4);
                p.vertIndex2 = BitConverter.ToUInt16(r.B, o + 6);
                p.vertIndex3 = BitConverter.ToUInt16(r.B, o + 8);
                polys[i] = p;
            }
            g.Polygons = polys;
            g.PolygonsCount = (uint)polys.Length;
            r.Polygons += polys.Length;
            // materials: GTA VI ids don't map to GTA V material types; keep the count and indices, DEFAULT type
            var mats = new BoundMaterial_s[mp >= 0 ? Math.Max((int)nm, 1) : 1];
            g.Materials = mats;
            g.MaterialsCount = (byte)mats.Length;
            var pmi = new byte[polys.Length];
            if (mi >= 0)
                for (int i = 0; i < pmi.Length && mi + i < r.Cpu; i++) pmi[i] = (byte)Math.Min((int)r.B[mi + i], mats.Length - 1);
            g.PolygonMaterialIndices = pmi;
            return g;
        }
    }
}
