using SharpDX;
using System;
using System.Collections.Generic;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI navmesh (.znv, resource v79) read into a YnvFile for display (not for saving). Layout:
    /// data\navmesh_format.md of the research project. GTA V's CNavMesh without the adjacent-area array: header
    /// 0xF0 B; NavMeshList blocks for vertices (u16[3]), indices (u16), edges (12 B) and polygons (0x2C B, index run
    /// = first &lt;&lt; 4 | count); portals 32 B (u16[3] from, u16[3] to). Vertex = sector min + u16 / 65535 * size.
    /// Polygons are coloured by their GTA VI flags (+0x08), since the GTA V flag bits don't apply.
    /// </summary>
    public static class Rpf9Navmesh
    {
        private const ulong CpuBase = 0x50000000;

        public class Info
        {
            public uint Area, Flags;
            public int Vertices, Polygons, Portals, FlagKinds;
            public string Summary =>
                Polygons.ToString("N0") + " polygons, " + Vertices.ToString("N0") + " vertices, " + Portals.ToString("N0") + " portals"
                + (Area != 0 ? ", area " + Area + " (cell " + (Area % 512) + ", " + (Area / 512) + ")" : "")
                + ", flags 0x" + Flags.ToString("x") + "; colour = polygon flags (" + FlagKinds + " kinds)";
        }

        private static int Ptr(byte[] b, int o)
        {
            ulong v = BitConverter.ToUInt64(b, o);
            if (v == 0) return -1;
            if (v < CpuBase || v >= CpuBase + (ulong)b.Length) throw new Exception("pointer 0x" + v.ToString("x") + " at 0x" + o.ToString("x") + " outside the file");
            return (int)(v - CpuBase);
        }

        /// <summary>Item offsets of a NavMeshList (0x30 B header: vftable, 1, count, 0, parts, offsets, part count).</summary>
        private static List<int> ListItems(byte[] b, int at, int size)
        {
            var r = new List<int>();
            if (at < 0) return r;
            int count = BitConverter.ToInt32(b, at + 8);
            int parts = Ptr(b, at + 0x10);
            int nparts = BitConverter.ToInt32(b, at + 0x20);
            for (int i = 0; i < nparts && parts >= 0; i++)
            {
                int p = Ptr(b, parts + 16 * i);
                int n = BitConverter.ToInt32(b, parts + 16 * i + 8);
                for (int k = 0; k < n; k++) r.Add(p + size * k);
            }
            if (r.Count != count) throw new Exception("list at 0x" + at.ToString("x") + ": " + r.Count + " items, header says " + count);
            return r;
        }

        private static Vector3 V3(byte[] b, int o) => new Vector3(BitConverter.ToSingle(b, o), BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8));

        private static readonly uint[] Palette =
        {
            0xFF3FA04C, 0xFFD08A2E, 0xFF2E6FD0, 0xFFB04FC4, 0xFF40B8B8, 0xFFC04040, 0xFF9AA83A, 0xFF5A5AE0,
            0xFFE0A0A0, 0xFF60D080, 0xFFA07040, 0xFF808080,
        };

        public static YnvFile Read(byte[] b, string name, RpfFileEntry entry, out Info info)
        {
            info = new Info();
            if (b.Length < 0xF0) throw new Exception("too small for a navmesh");
            info.Flags = BitConverter.ToUInt32(b, 0x10);
            var size = V3(b, 0x60);
            info.Area = BitConverter.ToUInt32(b, 0xC8);
            int nportals = BitConverter.ToInt32(b, 0xD4);

            var smin = Vector3.Zero;
            var smax = size;
            int st = Ptr(b, 0xA0);
            if (st >= 0) { smin = V3(b, st); smax = V3(b, st + 0x10); }
            // a zero size component (Z of flat sea tiles): the sector tree has the range
            var scale = new Vector3(size.X != 0 ? size.X : smax.X - smin.X, size.Y != 0 ? size.Y : smax.Y - smin.Y, size.Z != 0 ? size.Z : smax.Z - smin.Z) / 65535.0f;

            var verts = new List<Vector3>();
            foreach (int o in ListItems(b, Ptr(b, 0x70), 6))
                verts.Add(smin + new Vector3(BitConverter.ToUInt16(b, o), BitConverter.ToUInt16(b, o + 2), BitConverter.ToUInt16(b, o + 4)) * scale);
            var indices = new List<ushort>();
            foreach (int o in ListItems(b, Ptr(b, 0x80), 2)) indices.Add(BitConverter.ToUInt16(b, o));

            var ynv = new YnvFile(entry) { Name = name };
            ynv.Nav = new NavMesh
            {
                AABBSize = size,
                AreaID = info.Area,
                ContentFlags = (NavMeshFlags)info.Flags,
                SectorTree = new NavMeshSector { AABBMin = new Vector4(smin, 0.0f), AABBMax = new Vector4(smax, 0.0f) },
            };
            ynv.Vertices = verts;
            ynv.Indices = indices;
            ynv.Edges = new List<YnvEdge>();
            ynv.Polys = new List<YnvPoly>();
            ynv.Portals = new List<YnvPortal>();
            ynv.Points = new List<YnvPoint>();

            var tris = new List<EditorVertex>();
            var flagColour = new Dictionary<uint, uint>();
            int pi = 0;
            foreach (int o in ListItems(b, Ptr(b, 0x98), 0x2C))
            {
                uint run = BitConverter.ToUInt32(b, o + 4);
                uint flags = BitConverter.ToUInt32(b, o + 8);
                int first = (int)(run >> 4), count = (int)(run & 15);
                var pv = new Vector3[Math.Max(0, Math.Min(count, indices.Count - first))];
                for (int k = 0; k < pv.Length; k++)
                {
                    int vi = indices[first + k];
                    pv[k] = vi < verts.Count ? verts[vi] : Vector3.Zero;
                }
                var yp = new YnvPoly { Ynv = ynv, Index = pi++, Vertices = pv };
                yp.CalculatePosition();
                ynv.Polys.Add(yp);
                if (!flagColour.TryGetValue(flags, out uint col))
                {
                    col = Palette[flagColour.Count % Palette.Length];
                    flagColour[flags] = col;
                }
                // EditorVertex colours are ABGR; alpha 0xC0
                uint abgr = 0xC0000000 | ((col & 0xFF) << 16) | (col & 0xFF00) | ((col >> 16) & 0xFF);
                for (int t = 0; t + 2 < pv.Length; t++)
                {
                    tris.Add(new EditorVertex { Position = pv[0], Colour = abgr });
                    tris.Add(new EditorVertex { Position = pv[t + 1], Colour = abgr });
                    tris.Add(new EditorVertex { Position = pv[t + 2], Colour = abgr });
                }
            }
            info.FlagKinds = flagColour.Count;

            // portals: drawn as lines from -> to, with a node at the start
            var lines = new List<EditorVertex>();
            var nodes = new List<Vector4>();
            int pp = Ptr(b, 0xA8);
            for (int i = 0; i < nportals && pp >= 0; i++)
            {
                int o = pp + 32 * i;
                var from = smin + new Vector3(BitConverter.ToUInt16(b, o), BitConverter.ToUInt16(b, o + 2), BitConverter.ToUInt16(b, o + 4)) * scale;
                var to = smin + new Vector3(BitConverter.ToUInt16(b, o + 6), BitConverter.ToUInt16(b, o + 8), BitConverter.ToUInt16(b, o + 10)) * scale;
                lines.Add(new EditorVertex { Position = from, Colour = 0xFF0000FF });
                lines.Add(new EditorVertex { Position = to, Colour = 0xFF0000FF });
                nodes.Add(new Vector4(from, 1.0f));
            }
            ynv.TriangleVerts = tris.Count > 0 ? tris.ToArray() : null;
            ynv.PathVertices = lines.Count > 0 ? lines.ToArray() : null;
            ynv.NodePositions = nodes.Count > 0 ? nodes.ToArray() : null;
            ynv.Loaded = true;

            info.Vertices = verts.Count;
            info.Polygons = ynv.Polys.Count;
            info.Portals = nodes.Count;
            return ynv;
        }
    }
}
