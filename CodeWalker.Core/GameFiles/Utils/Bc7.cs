using System;

namespace CodeWalker.Utils
{
    /// <summary>BC7 (BPTC) block decoder -> RGBA8. Written for the GTA VI texture viewer (most of its textures are BC7).</summary>
    public static class Bc7
    {
        // per mode: subsets, partition bits, rotation bits, index selection bits, color bits, alpha bits,
        // endpoint p-bits, shared p-bits, index bits, secondary index bits
        static readonly int[,] Modes =
        {
            { 3, 4, 0, 0, 4, 0, 1, 0, 3, 0 },
            { 2, 6, 0, 0, 6, 0, 0, 1, 3, 0 },
            { 3, 6, 0, 0, 5, 0, 0, 0, 2, 0 },
            { 2, 6, 0, 0, 7, 0, 1, 0, 2, 0 },
            { 1, 0, 2, 1, 5, 6, 0, 0, 2, 3 },
            { 1, 0, 2, 0, 7, 8, 0, 0, 2, 2 },
            { 1, 0, 0, 0, 7, 7, 1, 0, 4, 0 },
            { 2, 6, 0, 0, 5, 5, 1, 0, 2, 0 },
        };

        static readonly ushort[] Part2 =
        {
            0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
            0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
            0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A, 0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
            0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C, 0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
        };

        static readonly string[] Part3 =
        {
            "0011001102212222", "0001001122112221", "0000200122112211", "0222002200110111", "0000000011221122", "0011001100220022", "0022002211111111", "0011001122112211",
            "0000000011112222", "0000111111112222", "0000111122222222", "0012001200120012", "0112011201120112", "0122012201220122", "0011011211221222", "0011200122002220",
            "0001001101121122", "0111001120012200", "0000112211221122", "0022002200221111", "0111011102220222", "0001000122212221", "0000001101220122", "0000110022102210",
            "0122012200110000", "0012001211222222", "0110122112210110", "0000011012211221", "0022110211020022", "0110011020022222", "0011012201220011", "0000200022112221",
            "0000000211221222", "0222002200120011", "0011001200220222", "0120012001200120", "0000111122220000", "0120120120120120", "0120201212010120", "0011220011220011",
            "0011112222000011", "0101010122222222", "0000000021212121", "0022112200221122", "0022001100220011", "0220122102201221", "0101222222220101", "0000212121212121",
            "0101010101012222", "0222011102220111", "0002111200021112", "0000211221122112", "0222011101110222", "0002111211120002", "0110011001102222", "0000000021122112",
            "0110011022222222", "0022001100110022", "0022112211220022", "0000000000002112", "0002000100020001", "0222122202221222", "0101222222222222", "0111201122012220",
        };

        static readonly byte[] Anchor2 =
        {
            15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
            15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6, 6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
        };
        static readonly byte[] Anchor3a =
        {
            3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3, 3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
            8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15, 3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
        };
        static readonly byte[] Anchor3b =
        {
            15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8, 15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
            15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8, 15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
        };

        static readonly int[] W2 = { 0, 21, 43, 64 };
        static readonly int[] W3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        static readonly int[] W4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        static int[] Weights(int bits) => bits == 2 ? W2 : bits == 3 ? W3 : W4;

        static int Subset(int ns, int part, int i)
        {
            if (ns == 1) return 0;
            if (ns == 2) return (Part2[part] >> i) & 1;
            return Part3[part][i] - '0';
        }

        static bool IsAnchor(int ns, int part, int i)
        {
            if (i == 0) return true;
            if (ns == 2) return i == Anchor2[part];
            if (ns == 3) return i == Anchor3a[part] || i == Anchor3b[part];
            return false;
        }

        /// <summary>checks the tables against each other: every anchor lies in its subset</summary>
        public static string SelfCheck()
        {
            for (int p = 0; p < 64; p++)
            {
                if (Subset(2, p, Anchor2[p]) != 1) return "2-subset partition " + p + ": anchor not in subset 1";
                if (Subset(3, p, Anchor3a[p]) != 1) return "3-subset partition " + p + ": anchor A not in subset 1";
                if (Subset(3, p, Anchor3b[p]) != 2) return "3-subset partition " + p + ": anchor B not in subset 2";
                if (Part3[p].Length != 16) return "3-subset partition " + p + ": length";
            }
            return null;
        }

        struct Bits
        {
            ulong lo, hi;
            int pos;
            public Bits(byte[] b, int o) { lo = BitConverter.ToUInt64(b, o); hi = BitConverter.ToUInt64(b, o + 8); pos = 0; }
            public int Get(int n)
            {
                if (n == 0) return 0;
                int v;
                if (pos + n <= 64) v = (int)((lo >> pos) & ((1UL << n) - 1));
                else if (pos >= 64) v = (int)((hi >> (pos - 64)) & ((1UL << n) - 1));
                else
                {
                    int a = 64 - pos;
                    v = (int)((lo >> pos) | ((hi & ((1UL << (n - a)) - 1)) << a));
                }
                pos += n;
                return v;
            }
        }

        static int Expand(int v, int bits) => bits >= 8 ? v : (v << (8 - bits)) | (v >> (2 * bits - 8));

        /// <summary>decode one 16-byte block to 16 RGBA pixels (64 bytes)</summary>
        public static void DecodeBlock(byte[] src, int so, byte[] px)
        {
            int mode = 0;
            while (mode < 8 && ((src[so + mode / 8] >> (mode % 8)) & 1) == 0) mode++;
            if (mode == 8) { Array.Clear(px, 0, 64); return; }
            var r = new Bits(src, so);
            r.Get(mode + 1);
            int ns = Modes[mode, 0], pb = Modes[mode, 1], rb = Modes[mode, 2], isb = Modes[mode, 3], cb = Modes[mode, 4], ab = Modes[mode, 5];
            int epb = Modes[mode, 6], spb = Modes[mode, 7], ib = Modes[mode, 8], ib2 = Modes[mode, 9];
            int part = r.Get(pb), rot = r.Get(rb), idxSel = r.Get(isb);
            int ne = ns * 2;
            var ep = new int[6, 4];
            for (int c = 0; c < 3; c++) for (int e = 0; e < ne; e++) ep[e, c] = r.Get(cb);
            for (int e = 0; e < ne; e++) ep[e, 3] = ab > 0 ? r.Get(ab) : 255;
            int cbits = cb, abits = ab;
            if (epb != 0)
            {
                for (int e = 0; e < ne; e++)
                {
                    int p = r.Get(1);
                    for (int c = 0; c < 3; c++) ep[e, c] = (ep[e, c] << 1) | p;
                    if (ab > 0) ep[e, 3] = (ep[e, 3] << 1) | p;
                }
                cbits++;
                if (ab > 0) abits++;
            }
            else if (spb != 0)
            {
                for (int s = 0; s < ns; s++)
                {
                    int p = r.Get(1);
                    for (int e = 2 * s; e < 2 * s + 2; e++)
                    {
                        for (int c = 0; c < 3; c++) ep[e, c] = (ep[e, c] << 1) | p;
                        if (ab > 0) ep[e, 3] = (ep[e, 3] << 1) | p;
                    }
                }
                cbits++;
                if (ab > 0) abits++;
            }
            for (int e = 0; e < ne; e++)
            {
                for (int c = 0; c < 3; c++) ep[e, c] = Expand(ep[e, c], cbits);
                if (ab > 0) ep[e, 3] = Expand(ep[e, 3], abits);
            }
            var i1 = new int[16];
            var i2 = new int[16];
            for (int i = 0; i < 16; i++) i1[i] = r.Get(IsAnchor(ns, part, i) ? ib - 1 : ib);
            if (ib2 > 0) for (int i = 0; i < 16; i++) i2[i] = r.Get(i == 0 ? ib2 - 1 : ib2);
            for (int i = 0; i < 16; i++)
            {
                int s = Subset(ns, part, i);
                int e0 = 2 * s, e1 = 2 * s + 1;
                int ci, ai, cbw, abw;
                if (ib2 == 0) { ci = ai = i1[i]; cbw = abw = ib; }
                else if (idxSel == 0) { ci = i1[i]; cbw = ib; ai = i2[i]; abw = ib2; }
                else { ci = i2[i]; cbw = ib2; ai = i1[i]; abw = ib; }
                int wc = Weights(cbw)[ci], wa = Weights(abw)[ai];
                int o = i * 4;
                for (int c = 0; c < 3; c++) px[o + c] = (byte)(((64 - wc) * ep[e0, c] + wc * ep[e1, c] + 32) >> 6);
                px[o + 3] = (byte)(((64 - wa) * ep[e0, 3] + wa * ep[e1, 3] + 32) >> 6);
                if (rot != 0)
                {
                    byte t = px[o + 3];
                    px[o + 3] = px[o + rot - 1];
                    px[o + rot - 1] = t;
                }
            }
        }

        /// <summary>decode a BC7 surface to RGBA8 (width x height)</summary>
        public static byte[] Decode(byte[] data, int width, int height)
        {
            var res = new byte[width * height * 4];
            int bw = (width + 3) / 4, bh = (height + 3) / 4;
            var px = new byte[64];
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    int so = (by * bw + bx) * 16;
                    if (so + 16 > data.Length) return res;
                    DecodeBlock(data, so, px);
                    for (int y = 0; y < 4; y++)
                    {
                        int yy = by * 4 + y;
                        if (yy >= height) break;
                        for (int x = 0; x < 4; x++)
                        {
                            int xx = bx * 4 + x;
                            if (xx >= width) break;
                            Buffer.BlockCopy(px, (y * 4 + x) * 4, res, (yy * width + xx) * 4, 4);
                        }
                    }
                }
            }
            return res;
        }
    }
}
