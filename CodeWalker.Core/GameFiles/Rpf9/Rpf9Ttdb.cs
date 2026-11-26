using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI TTDB "tiled texture database" packs (*.ttpf, magic RDS5): the streamed model textures, keyed by the
    /// 64-bit key of a drawable's texture reference. Port of tools/ttdb.py (see its docstring for the layout).
    /// Chunks are zlib, stored, or Xbox BCPack; BCPack needs Microsoft's BCPack.dll (GTA V gen9 tools), looked for
    /// next to the app and in <see cref="BcPackSearchPaths"/>. Without it those textures are skipped.
    /// </summary>
    public class Rpf9Ttdb : IDisposable
    {
        public static List<string> BcPackSearchPaths = new List<string> { @"X:\gta5\tools_ng\bin\gen9\RPFChecker\BCPack.dll" };

        static readonly Dictionary<int, string> PAT64 = new Dictionary<int, string> { { 8, "xyxxyyxyxyxyx" }, { 16, "xyxyyxyxyxyx" } };

        public readonly string Path;
        FileStream fs;
        readonly long[] off;      // chunk file offsets, + end sentinel
        readonly int[] codec;     // 0 stored, 1-7 BCPack BC1..BC7, 8 zlib
        readonly int[] pad;       // bytes at the chunk end that are not data
        readonly long[] page;     // first 4 KB virtual page of each chunk, + end
        readonly ulong[] q0, q1, hashes;
        public readonly Dictionary<ulong, int> Index = new Dictionary<ulong, int>();
        readonly Dictionary<int, byte[]> cache = new Dictionary<int, byte[]>();
        readonly LinkedList<int> lru = new LinkedList<int>();

        public Rpf9Ttdb(string path)
        {
            Path = path;
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // +0x04 u32 n, +0x08 u64 (bits 0-39 virtual size), +0x10 n x u64 chunk entries (exe 0x140F435F0):
            // bits 0-25 offset >> 8, 26-29 codec, 30-51 first 4 KB page, 52-63 pad; the last entry is the end sentinel
            var head = ReadAt(0, 0x10);
            if (head[0] != 'R' || head[1] != 'D' || head[2] != 'S' || head[3] != '5') throw new InvalidDataException("not RDS5");
            int n = BitConverter.ToInt32(head, 4);
            if (n < 2) throw new InvalidDataException("empty chunk table");
            var tab = ReadAt(0x10, 8 * n);
            int nc = n - 1;
            off = new long[n]; page = new long[n]; codec = new int[nc]; pad = new int[nc];
            for (int i = 0; i < n; i++)
            {
                ulong x = BitConverter.ToUInt64(tab, 8 * i);
                off[i] = (long)(x & 0x3ffffff) << 8;
                page[i] = (long)((x >> 30) & 0x3fffff);
                if (i < nc) { codec[i] = (int)((x >> 26) & 15); pad[i] = (int)(x >> 52); }
            }
            // the ETPF index is at virtual offset 0 (chunk 0, stored)
            var eh = Read(0, 16);
            if (eh[0] != 'E' || eh[1] != 'T' || eh[2] != 'P' || eh[3] != 'F') throw new InvalidDataException("no ETPF table");
            int cnt = BitConverter.ToInt32(eh, 8);
            var hdr = Read(0, 16 + 24L * cnt);
            q0 = new ulong[cnt]; q1 = new ulong[cnt]; hashes = new ulong[cnt];
            for (int i = 0; i < cnt; i++)
            {
                q0[i] = BitConverter.ToUInt64(hdr, 16 + 16 * i);
                q1[i] = BitConverter.ToUInt64(hdr, 24 + 16 * i);
                hashes[i] = BitConverter.ToUInt64(hdr, 16 + 16 * cnt + 8 * i);
                Index[hashes[i]] = i;
            }
        }

        public void Dispose() { fs?.Dispose(); fs = null; }

        byte[] ReadAt(long o, int n)
        {
            var b = new byte[n];
            fs.Seek(o, SeekOrigin.Begin);
            int got = 0;
            while (got < n) { int r = fs.Read(b, got, n - got); if (r <= 0) break; got += r; }
            return b;
        }

        // ------------------------------------------------------------------ BCPack

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int BcPackDecompressFn(ulong bc, IntPtr dst, ulong dstBytes, IntPtr src);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr mod, string name);
        static BcPackDecompressFn bcpack;
        static bool bcpackTried;
        public static string BcPackPath { get; private set; }

        static BcPackDecompressFn BcPack
        {
            get
            {
                if (bcpackTried) return bcpack;
                bcpackTried = true;
                var paths = new List<string> { System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BCPack.dll") };
                paths.AddRange(BcPackSearchPaths);
                foreach (var p in paths)
                {
                    try
                    {
                        if (!File.Exists(p)) continue;
                        var mod = LoadLibrary(p);
                        if (mod == IntPtr.Zero) continue;
                        var fn = GetProcAddress(mod, "BCPackDecompress");
                        if (fn == IntPtr.Zero) continue;
                        bcpack = Marshal.GetDelegateForFunctionPointer<BcPackDecompressFn>(fn);
                        BcPackPath = p;
                        break;
                    }
                    catch { }
                }
                return bcpack;
            }
        }

        // ------------------------------------------------------------------ chunks

        byte[] Chunk(int i)
        {
            if (cache.TryGetValue(i, out var d)) { lru.Remove(i); lru.AddFirst(i); return d; }
            int c = codec[i];
            int nbytes = (int)((page[i + 1] - page[i]) << 12);
            int csize = (int)(off[i + 1] - off[i] - pad[i]);   // compressed size (exe 0x1455B23E0)
            if (c == 8)
            {
                var src = ReadAt(off[i] + 2, csize - 2);   // skip the zlib header
                using (var ms = new MemoryStream(src))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (var o = new MemoryStream(nbytes))
                {
                    try { ds.CopyTo(o); } catch (InvalidDataException) { if (o.Length == 0) throw; }   // trailing padding
                    d = o.ToArray();
                }
            }
            else if (c >= 1 && c <= 7)   // BCPack BC1..BC7
            {
                var fn = BcPack ?? throw new BcPackMissingException();
                var src = new byte[csize + 4096];   // slack: the decoder may read ahead
                Buffer.BlockCopy(ReadAt(off[i], csize), 0, src, 0, csize);
                d = new byte[nbytes];
                var hs = GCHandle.Alloc(src, GCHandleType.Pinned);
                var hd = GCHandle.Alloc(d, GCHandleType.Pinned);
                int hr;
                try { hr = fn((ulong)c, hd.AddrOfPinnedObject(), (ulong)nbytes, hs.AddrOfPinnedObject()); }
                finally { hs.Free(); hd.Free(); }
                if (hr != 0) throw new InvalidDataException("BCPack error " + hr.ToString("x8"));
            }
            else if (c == 0) d = ReadAt(off[i], csize);
            else throw new NotSupportedException("chunk codec " + c);
            cache[i] = d;
            lru.AddFirst(i);
            while (lru.Count > 8) { cache.Remove(lru.Last.Value); lru.RemoveLast(); }
            return d;
        }

        byte[] Read(long pg, long nbytes)
        {
            var res = new byte[nbytes];
            long p = pg, end = pg + (nbytes + 4095) / 4096, w = 0;
            while (p < end && w < nbytes)
            {
                int i = Array.BinarySearch(page, p);
                if (i < 0) i = ~i - 1;
                if (i < 0 || i >= codec.Length) throw new InvalidDataException("page outside the pack");
                var data = Chunk(i);
                long s = (p - page[i]) << 12;
                long take = Math.Min(data.Length - s, (end - p) << 12);
                if (take <= 0) throw new InvalidDataException("short chunk");
                long copy = Math.Min(take, nbytes - w);
                Buffer.BlockCopy(data, (int)s, res, (int)w, (int)copy);
                w += copy;
                p += take >> 12;
            }
            return res;
        }

        // ------------------------------------------------------------------ textures

        /// <summary>Texture record (exe decoder 0x1451FB680). Cube: 6 faces (not decoded); Flags: q0 bits 42-43,
        /// data with zero pages filled from elsewhere (not decoded).</summary>
        public class Record { public int W, H, Fmt, Mips, Flags; public bool Big, Cube; public long Page; }

        public Record GetRecord(int i)
        {
            ulong a = q0[i];
            int w = (int)(a & 0x3fff) + 1, h = (int)((a >> 14) & 0x3fff) + 1;
            int mips = (int)((a >> 28) & 15) + (int)((a >> 32) & 7);
            int full = 0;
            for (int m = Math.Max(w, h); m > 0; m >>= 1) full++;
            return new Record
            {
                W = w, H = h, Fmt = (int)((a >> 35) & 0x7f), Mips = Math.Max(1, Math.Min(mips, full)),
                Big = ((a >> 56) & 7) == 0,          // tiling 0 = 64 KB tiles, 1-4 = 4 KB tiles
                Cube = ((a >> 59) & 1) != 0, Flags = (int)((a >> 42) & 3),
                Page = (long)(q1[i] & 0x1fffff),
            };
        }

        static int Count(string p, char c) { int n = 0; foreach (var x in p) if (x == c) n++; return n; }

        /// <summary>Decoded texture for a key, or null if the pack doesn't have it. Throws on undecodable data.</summary>
        public Texture GetTexture(ulong key)
        {
            if (!Index.TryGetValue(key, out int i)) return null;
            var t = GetRecord(i);
            try { return Decode(key, t); }
            catch (BcPackMissingException)
            {
                // mips are stored smallest first: without BCPack.dll, show the largest reduced texture that is zlib/stored
                for (int m0 = 1; m0 < t.Mips; m0++)
                {
                    var t2 = new Record { W = Math.Max(1, t.W >> m0), H = Math.Max(1, t.H >> m0), Fmt = t.Fmt, Mips = t.Mips - m0, Big = t.Big, Cube = t.Cube, Flags = t.Flags, Page = t.Page };
                    try { return Decode(key, t2); } catch (BcPackMissingException) { }
                }
                throw;
            }
        }

        /// <summary>A texture with size/format only (no pixel data), named by its key: for listing a pack.
        /// Fill it with <see cref="Load"/>.</summary>
        public Texture Placeholder(ulong key)
        {
            var t = GetRecord(Index[key]);
            var tex = new Texture
            {
                Name = key.ToString("x16"),
                NameHash = (uint)key,
                Width = (ushort)t.W,
                Height = (ushort)t.H,
                Depth = 1,
                Levels = (byte)t.Mips,
                Format = Rpf9Texture.LegacyFormat(t.Fmt),
                G9_Format = (TextureFormatG9)t.Fmt,
            };
            tex.Stride = tex.CalculateStride();
            return tex;
        }

        /// <summary>Decodes a <see cref="Placeholder"/> in place (size may shrink when BCPack.dll is missing).</summary>
        public void Load(Texture tex)
        {
            if (tex.Data != null) return;
            var full = GetTexture(ulong.Parse(tex.Name, System.Globalization.NumberStyles.HexNumber));
            if (full == null) throw new InvalidDataException("texture not in this pack");
            tex.Width = full.Width; tex.Height = full.Height; tex.Levels = full.Levels; tex.Stride = full.Stride;
            tex.Data = full.Data;
        }

        /// <summary>Just the small mips (largest side at most maxDim, at least 1 mip): cheap, for classifying textures.</summary>
        public Texture GetTextureMip(ulong key, int maxDim)
        {
            if (!Index.TryGetValue(key, out int i)) return null;
            var t = GetRecord(i);
            int m0 = 0;
            while (m0 < t.Mips - 1 && Math.Max(t.W >> m0, t.H >> m0) > maxDim) m0++;
            var t2 = new Record { W = Math.Max(1, t.W >> m0), H = Math.Max(1, t.H >> m0), Fmt = t.Fmt, Mips = t.Mips - m0, Big = t.Big, Cube = t.Cube, Flags = t.Flags, Page = t.Page };
            return Decode(key, t2);
        }

        class BcPackMissingException : NotSupportedException { public BcPackMissingException() : base("BCPack chunk (BCPack.dll not found)") { } }

        Texture Decode(ulong key, Record t)
        {
            if (t.Cube) throw new NotSupportedException("cube map");
            if (t.Flags != 0) throw new NotSupportedException("format flags " + t.Flags);
            if (!Rpf9Texture.FMT.TryGetValue(t.Fmt, out var fb) || Rpf9Texture.LegacyFormat(t.Fmt) == 0) throw new NotSupportedException("DXGI format " + t.Fmt);
            int bpe = fb[0], blk = fb[1];
            string p = t.Big ? (PAT64.TryGetValue(bpe, out var p64) ? p64 : null) : Rpf9Texture.PAT[bpe];
            if (p == null) throw new NotSupportedException("no 64 KB pattern for " + bpe + "-byte elements");
            int lb = 0; for (int x = bpe; x > 1; x >>= 1) lb++;
            long tile = 1L << (p.Length + lb);
            int tw = 1 << Count(p, 'x'), th = 1 << Count(p, 'y');
            var bws = new int[t.Mips]; var bhs = new int[t.Mips]; var intail = new bool[t.Mips];
            long size = 0; bool any = false;
            for (int m = 0; m < t.Mips; m++)
            {
                Rpf9Texture.MipDims(t.W, t.H, m, blk, out bws[m], out bhs[m]);
                intail[m] = bws[m] <= tw / 2 && bhs[m] <= th;
                if (intail[m]) any = true;
                else size += (long)((bws[m] + tw - 1) / tw) * ((bhs[m] + th - 1) / th) * tile;
            }
            if (any) size += tile;
            var data = Read(t.Page, size);

            var xo = new int[tw]; var yo = new int[th];
            int xi = 0, yi = 0;
            for (int bit = 0; bit < p.Length; bit++)
            {
                if (p[bit] == 'x') { for (int x = 0; x < tw; x++) xo[x] |= ((x >> xi) & 1) << bit; xi++; }
                else { for (int y = 0; y < th; y++) yo[y] |= ((y >> yi) & 1) << bit; yi++; }
            }
            var outp = new byte[t.Mips][];
            long o = any ? tile : 0;
            for (int m = t.Mips - 1; m >= 0; m--)
            {
                if (intail[m]) continue;
                int bw = bws[m], bh = bhs[m], pw = (bw + tw - 1) / tw;
                var r = new byte[(long)bw * bh * bpe];
                long eb = o / bpe;
                for (int y = 0; y < bh; y++)
                {
                    long rowTile = (long)(y / th) * pw;
                    int yy = yo[y & (th - 1)];
                    for (int x = 0; x < bw; x++)
                    {
                        long idx = eb + (((rowTile + x / tw) << p.Length) | (uint)(xo[x & (tw - 1)] | yy));
                        long src = idx * bpe;
                        if (src + bpe <= data.Length) Buffer.BlockCopy(data, (int)src, r, (y * bw + x) * bpe, bpe);
                    }
                }
                outp[m] = r;
                o += (long)pw * ((bh + th - 1) / th) * tile;
            }
            if (any)
            {
                var offs = new List<long>();
                for (long s = tile / 2; s >= 2048; s /= 2) offs.Add(s);
                offs.AddRange(new long[] { 1536, 1280, 1024, 768, 512, 256, 0 });
                int k = 0;
                for (int m = 0; m < t.Mips; m++)
                {
                    if (!intail[m]) continue;
                    int bw = bws[m], bh = bhs[m];
                    var r = new byte[(long)bw * bh * bpe];
                    if (k < offs.Count)
                    {
                        long eb = offs[k] / bpe;
                        for (int y = 0; y < bh; y++)
                            for (int x = 0; x < bw; x++)
                            {
                                long src = (eb + (xo[x] | yo[y])) * bpe;
                                if (src + bpe <= data.Length) Buffer.BlockCopy(data, (int)src, r, (y * bw + x) * bpe, bpe);
                            }
                    }
                    outp[m] = r;
                    k++;
                }
            }
            return Rpf9Texture.MakeTexture(key.ToString("x16"), t.W, t.H, t.Fmt, new List<byte[]>(outp));
        }
    }
}
