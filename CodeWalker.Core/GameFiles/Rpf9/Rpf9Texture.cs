using System;
using System.Collections.Generic;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI texture dictionaries (.ztd; also embedded ones). Port of rsc_tex.py: the resource is the virtual (CPU)
    /// part followed by the physical (GPU) part; CPU pointers are 0x5xxxxxxx, GPU pointers 0x6xxxxxxx. GPU data uses
    /// the AMD GFX10 4 KB swizzle, mips stored smallest first with a shared tail tile.
    /// </summary>
    public static class Rpf9Texture
    {
        const ulong P_CPU = 0x50000000, P_GPU = 0x60000000;
        internal static readonly Dictionary<int, string> PAT = new Dictionary<int, string>
        {
            { 1, "xxxxyyyyyxyx" }, { 2, "xxxyyyxyxyx" }, { 4, "xxyyyxyxyx" }, { 8, "xyyxxyxyx" }, { 16, "yyxxyxyx" },
        };
        // DXGI format -> (bytes per element, block size)
        internal static readonly Dictionary<int, int[]> FMT = new Dictionary<int, int[]>
        {
            { 2, new[] { 16, 1 } }, { 10, new[] { 8, 1 } }, { 11, new[] { 8, 1 } }, { 24, new[] { 4, 1 } }, { 26, new[] { 4, 1 } },
            { 28, new[] { 4, 1 } }, { 29, new[] { 4, 1 } }, { 34, new[] { 4, 1 } }, { 35, new[] { 4, 1 } }, { 41, new[] { 4, 1 } },
            { 49, new[] { 2, 1 } }, { 54, new[] { 2, 1 } }, { 56, new[] { 2, 1 } }, { 61, new[] { 1, 1 } }, { 65, new[] { 1, 1 } },
            { 71, new[] { 8, 4 } }, { 72, new[] { 8, 4 } }, { 74, new[] { 16, 4 } }, { 75, new[] { 16, 4 } }, { 77, new[] { 16, 4 } },
            { 78, new[] { 16, 4 } }, { 80, new[] { 8, 4 } }, { 81, new[] { 8, 4 } }, { 83, new[] { 16, 4 } }, { 84, new[] { 16, 4 } },
            { 87, new[] { 4, 1 } }, { 88, new[] { 4, 1 } }, { 91, new[] { 4, 1 } }, { 94, new[] { 16, 4 } }, { 95, new[] { 16, 4 } },
            { 96, new[] { 16, 4 } }, { 98, new[] { 16, 4 } }, { 99, new[] { 16, 4 } },
        };
        static readonly int[][] TAIL = { new[] { 2, 0, 1 }, new[] { 4, 2, 1 }, new[] { 0, 4, 3 }, new[] { 0, 2, 1 }, new[] { 4, 4, 1 }, new[] { 4, 0, 1 } };

        public class Info
        {
            public uint Hash;
            public string Name;
            public long Gpu, Size;
            public int Width, Height, Depth, Dim, Format, Mips;
            public string Note;
        }

        static int Count(string p, char c) { int n = 0; foreach (var x in p) if (x == c) n++; return n; }
        static void TileDims(int bpe, out int tw, out int th) { var p = PAT[bpe]; tw = 1 << Count(p, 'x'); th = 1 << Count(p, 'y'); }
        static long Padded(int bw, int bh, int bpe) { TileDims(bpe, out int tw, out int th); return (long)((bw + tw - 1) / tw) * ((bh + th - 1) / th) * 4096; }

        internal static void MipDims(int w, int h, int m, int blk, out int bw, out int bh)
        {
            int mw = Math.Max(w >> m, 1), mh = Math.Max(h >> m, 1);
            bw = (mw + blk - 1) / blk;
            bh = (mh + blk - 1) / blk;
        }

        static long ChainSize(int w, int h, int mips, int bpe, int blk)
        {
            TileDims(bpe, out int tw, out int th);
            long tot = 0;
            bool tail = false;
            for (int m = 0; m < mips; m++)
            {
                MipDims(w, h, m, blk, out int bw, out int bh);
                if (mips > 1 && bw <= tw / 2 && bh <= th) tail = true;
                else tot += Padded(bw, bh, bpe);
            }
            return tot + (tail ? 4096 : 0);
        }

        /// <summary>linear bw x bh elements from the tiled surface at buf[off]; out-of-range elements stay zero</summary>
        static byte[] Untile(byte[] buf, long off, int bw, int bh, int bpe)
        {
            var p = PAT[bpe];
            TileDims(bpe, out int tw, out int th);
            int pw = (bw + tw - 1) / tw;
            var xo = new int[tw];
            var yo = new int[th];
            int xi = 0, yi = 0;
            for (int bit = 0; bit < p.Length; bit++)
            {
                if (p[bit] == 'x') { for (int x = 0; x < tw; x++) xo[x] |= ((x >> xi) & 1) << bit; xi++; }
                else { for (int y = 0; y < th; y++) yo[y] |= ((y >> yi) & 1) << bit; yi++; }
            }
            var res = new byte[(long)bw * bh * bpe];
            for (int y = 0; y < bh; y++)
            {
                long rowTile = (long)(y / th) * pw;
                for (int x = 0; x < bw; x++)
                {
                    long idx = ((rowTile + x / tw) << p.Length) | (uint)(xo[x & (tw - 1)] | yo[y & (th - 1)]);
                    long src = off + idx * bpe;
                    if (src + bpe <= buf.Length) Buffer.BlockCopy(buf, (int)src, res, (y * bw + x) * bpe, bpe);
                }
            }
            return res;
        }

        static byte[] Crop(byte[] tile, int tw, int x0, int y0, int bw, int bh, int bpe)
        {
            var r = new byte[bw * bh * bpe];
            for (int y = 0; y < bh; y++)
                Buffer.BlockCopy(tile, ((y0 + y) * tw + x0) * bpe, r, y * bw * bpe, bw * bpe);
            return r;
        }

        /// <summary>linear data per mip, mip 0 first</summary>
        static List<byte[]> DecodeChain(byte[] buf, long off, int w, int h, int mips, int bpe, int blk)
        {
            TileDims(bpe, out int tw, out int th);
            var bws = new int[mips];
            var bhs = new int[mips];
            var intail = new bool[mips];
            bool any = false;
            for (int m = 0; m < mips; m++)
            {
                MipDims(w, h, m, blk, out bws[m], out bhs[m]);
                intail[m] = mips > 1 && bws[m] <= tw / 2 && bhs[m] <= th; // a single-mip surface has no mip tail
                any |= intail[m];
            }
            var outp = new byte[mips][];
            long o = off + (any ? 4096 : 0);
            for (int m = mips - 1; m >= 0; m--)
            {
                if (intail[m]) continue;
                outp[m] = Untile(buf, o, bws[m], bhs[m], bpe);
                o += Padded(bws[m], bhs[m], bpe);
            }
            if (any)
            {
                var tile = Untile(buf, off, tw, th, bpe);
                int i = 0;
                for (int m = 0; m < mips; m++)
                {
                    if (!intail[m]) continue;
                    int x0 = 0, y0 = 0;
                    if (i < TAIL.Length)
                    {
                        x0 = TAIL[i][0] != 0 ? tw / TAIL[i][0] : 0;
                        y0 = TAIL[i][1] != 0 ? th * TAIL[i][2] / TAIL[i][1] : 0;
                    }
                    int cw = Math.Min(bws[m], tw - x0), ch = Math.Min(bhs[m], th - y0);
                    var c = Crop(tile, tw, x0, y0, cw, ch, bpe);
                    if (cw != bws[m] || ch != bhs[m])
                    {
                        var full = new byte[bws[m] * bhs[m] * bpe];
                        for (int y = 0; y < ch; y++) Buffer.BlockCopy(c, y * cw * bpe, full, y * bws[m] * bpe, cw * bpe);
                        c = full;
                    }
                    outp[m] = c;
                    i++;
                }
            }
            return new List<byte[]>(outp);
        }

        static ulong U64(byte[] b, long o) => o >= 0 && o + 8 <= b.Length ? BitConverter.ToUInt64(b, (int)o) : ulong.MaxValue;

        /// <summary>texture records of a texture dictionary's CPU part, or an empty list if it isn't one</summary>
        public static List<Info> ReadInfos(byte[] cpu, long at = 0)
        {
            var res = new List<Info>();
            if (at < 0 || at + 0x40 > cpu.Length) return res;
            if (at == 0) for (int i = 0; i < 8; i++) if (cpu[i] != 0) return res;
            ulong v8 = U64(cpu, at + 8), v10 = U64(cpu, at + 0x10), v18 = U64(cpu, at + 0x18);
            ulong len = (ulong)cpu.Length;
            if (!(v8 >= P_CPU && v8 < P_CPU + len && v10 == 0 && v18 == 1)) return res;
            ulong hp = U64(cpu, at + 0x20), vp = U64(cpu, at + 0x30);
            int n = BitConverter.ToUInt16(cpu, (int)at + 0x28);
            if (!(hp >= P_CPU && hp < P_CPU + len && vp >= P_CPU && vp < P_CPU + len) || n == 0) return res;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    uint h = BitConverter.ToUInt32(cpu, (int)(hp - P_CPU) + 4 * i);
                    long o = (long)(U64(cpu, (long)(vp - P_CPU) + 8 * i) - P_CPU);
                    ulong g = U64(cpu, o + 0x18);
                    if (!(g >= P_GPU && g < P_GPU + (1UL << 28))) return res;
                    uint size = BitConverter.ToUInt32(cpu, (int)o + 8);
                    int es = BitConverter.ToUInt16(cpu, (int)o + 12);
                    int w = BitConverter.ToUInt16(cpu, (int)o + 0x38), ht = BitConverter.ToUInt16(cpu, (int)o + 0x3A);
                    int d = cpu[o + 0x3C], dim = cpu[o + 0x3E], fmt = cpu[o + 0x3F], mips = cpu[o + 0x42];
                    long np = (long)(U64(cpu, o + 0x30) - P_CPU) + 8;
                    int e = (int)np;
                    while (e < cpu.Length && cpu[e] != 0) e++;
                    string name = np >= 0 && np < cpu.Length ? Encoding.UTF8.GetString(cpu, (int)np, e - (int)np) : "";
                    res.Add(new Info
                    {
                        Hash = h, Name = name.Length > 0 ? name : h.ToString("x8"), Gpu = (long)(g - P_GPU), Size = (long)size * es,
                        Width = w, Height = ht, Depth = d, Dim = dim, Format = fmt, Mips = Math.Max(mips, 1)
                    });
                }
                catch (Exception) { }
            }
            return res;
        }

        internal static TextureFormat LegacyFormat(int f)
        {
            switch (f)
            {
                case 71: case 72: return TextureFormat.D3DFMT_DXT1;
                case 74: case 75: return TextureFormat.D3DFMT_DXT3;
                case 77: case 78: return TextureFormat.D3DFMT_DXT5;
                case 80: case 81: return TextureFormat.D3DFMT_ATI1;
                case 83: case 84: return TextureFormat.D3DFMT_ATI2;
                case 98: case 99: return TextureFormat.D3DFMT_BC7;
                case 28: case 29: return TextureFormat.D3DFMT_A8B8G8R8;
                case 87: case 91: return TextureFormat.D3DFMT_A8R8G8B8;
                case 88: return TextureFormat.D3DFMT_X8R8G8B8;
                case 61: return TextureFormat.D3DFMT_L8;
                case 65: return TextureFormat.D3DFMT_A8;
                default: return 0;
            }
        }

        /// <summary>A CodeWalker texture from linear mip data (mip 0 first).</summary>
        internal static Texture MakeTexture(string name, int w, int h, int fmt, List<byte[]> chain)
        {
            int total = 0;
            foreach (var c in chain) total += c.Length;
            var payload = new byte[total];
            int p = 0;
            foreach (var c in chain) { Buffer.BlockCopy(c, 0, payload, p, c.Length); p += c.Length; }
            var tex = new Texture
            {
                Name = name,
                NameHash = JenkHash.GenHash(name.ToLowerInvariant()),
                Width = (ushort)w,
                Height = (ushort)h,
                Depth = 1,
                Levels = (byte)chain.Count,
                Format = LegacyFormat(fmt),
                G9_Format = (TextureFormatG9)fmt,
                Data = new TextureData { FullData = payload },
            };
            tex.Stride = tex.CalculateStride();
            return tex;
        }

        /// <summary>DXGI sRGB formats = colour (albedo) textures.</summary>
        public static bool IsSrgb(int fmt) => fmt == 29 || fmt == 72 || fmt == 75 || fmt == 78 || fmt == 91 || fmt == 93 || fmt == 99;

        /// <summary>CodeWalker textures (first array slice, all mips) from a GTA VI texture dictionary resource.
        /// virtualSize = size of the CPU part (Rpf9FileEntry.VirtualSize). notes gets one line per texture skipped.</summary>
        public static TextureDictionary ReadDictionary(byte[] data, long virtualSize, List<string> notes = null)
        {
            var cpu = new byte[Math.Min(virtualSize, data.Length)];
            Buffer.BlockCopy(data, 0, cpu, 0, cpu.Length);
            var gpu = new byte[data.Length - cpu.Length];
            Buffer.BlockCopy(data, cpu.Length, gpu, 0, gpu.Length);
            var infos = ReadInfos(cpu);
            if (infos.Count == 0) return null;
            var list = new List<Texture>();
            foreach (var t in infos)
            {
                var lf = LegacyFormat(t.Format);
                if (!FMT.TryGetValue(t.Format, out var fb) || lf == 0) { notes?.Add(t.Name + ": format " + t.Format + " not supported"); continue; }
                if (t.Dim == 3) { notes?.Add(t.Name + ": 3D texture not supported"); continue; }
                if (t.Gpu + t.Size > gpu.Length) { notes?.Add(t.Name + ": GPU data missing"); continue; }
                int bpe = fb[0], blk = fb[1];
                long one = ChainSize(t.Width, t.Height, t.Mips, bpe, blk);
                if (one * Math.Max(t.Depth, 1) != t.Size) { notes?.Add(t.Name + ": size mismatch"); continue; }
                var chain = DecodeChain(gpu, t.Gpu, t.Width, t.Height, t.Mips, bpe, blk);
                list.Add(MakeTexture(t.Name, t.Width, t.Height, t.Format, chain));
            }
            var td = new TextureDictionary();
            td.BuildFromTextureList(list);
            return td;
        }
    }
}
