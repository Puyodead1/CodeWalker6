using System;
using System.Collections.Generic;
using System.IO;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI (RPF9) cryptography. Nothing key-related is stored here: the white-box AES tables and key blobs are read
    /// from the game executable (game_scarlett_master.exe, Xbox Series master build) at run time.
    /// TOC: white-box AES-256 decryption (cipher type 16) in CBC mode. File data: white-box AES-256 encryption
    /// (cipher type 17) used as a CTR keystream. See rpf9toc.py / rpf9x.py for the reverse-engineering notes.
    /// </summary>
    public class Rpf9Pe
    {
        public readonly byte[] Data;
        public readonly ulong ImageBase;
        private readonly List<uint[]> Sections = new List<uint[]>(); // va, size, raw, rawsize

        public Rpf9Pe(string path)
        {
            Data = File.ReadAllBytes(path);
            int pe = BitConverter.ToInt32(Data, 0x3c);
            int nsec = BitConverter.ToUInt16(Data, pe + 6);
            int opt = BitConverter.ToUInt16(Data, pe + 20);
            ImageBase = BitConverter.ToUInt64(Data, pe + 48);
            int s = pe + 24 + opt;
            for (int i = 0; i < nsec; i++)
            {
                uint vsize = BitConverter.ToUInt32(Data, s + 40 * i + 8);
                uint va = BitConverter.ToUInt32(Data, s + 40 * i + 12);
                uint rsize = BitConverter.ToUInt32(Data, s + 40 * i + 16);
                uint raw = BitConverter.ToUInt32(Data, s + 40 * i + 20);
                Sections.Add(new[] { va, Math.Max(vsize, rsize), raw, rsize });
            }
        }

        public byte[] Read(ulong va, int n)
        {
            ulong rva = va - ImageBase;
            foreach (var sec in Sections)
            {
                if (rva >= sec[0] && rva < (ulong)sec[0] + sec[1])
                {
                    long o = sec[2] + (long)(rva - sec[0]);
                    var b = new byte[n];
                    Buffer.BlockCopy(Data, (int)o, b, 0, (int)Math.Max(0, Math.Min(n, Data.Length - o)));
                    return b;
                }
            }
            throw new Exception("address not in image: " + va.ToString("X"));
        }

        /// <summary>every 16-byte aligned occurrence of magic inside a section's raw data: va -> 4340-byte blob</summary>
        public Dictionary<ulong, byte[]> Blobs(byte[] magic)
        {
            var res = new Dictionary<ulong, byte[]>();
            foreach (var sec in Sections)
            {
                for (long i = sec[2]; i + 4340 <= sec[2] + sec[3] && i + 4340 <= Data.Length; i += 16)
                {
                    if (Data[i] == magic[0] && Data[i + 1] == magic[1] && Data[i + 2] == magic[2] && Data[i + 3] == magic[3])
                    {
                        var b = new byte[4340];
                        Buffer.BlockCopy(Data, (int)i, b, 0, 4340);
                        res[ImageBase + sec[0] + (ulong)(i - sec[2])] = b;
                    }
                }
            }
            return res;
        }
    }

    /// <summary>White-box AES-256 block function (13 table rounds + final per-byte tables from the key blob).</summary>
    public class Rpf9WhiteBox
    {
        private readonly byte[][] IN = new byte[16][];
        private readonly uint[][] T = new uint[16][];
        private readonly uint[] K = new uint[60];
        private readonly byte[][] F = new byte[16][];
        private readonly bool Ctr;

        public Rpf9WhiteBox(Rpf9Pe pe, byte[] blob, ulong vaIn, ulong vaT, bool ctr)
        {
            Ctr = ctr;
            for (int i = 0; i < 16; i++)
            {
                IN[i] = pe.Read(vaIn + 256u * (uint)i, 256);
                var t = pe.Read(vaT + 0x400u * (uint)i, 0x400);
                T[i] = new uint[256];
                Buffer.BlockCopy(t, 0, T[i], 0, 0x400);
                F[i] = new byte[256];
                Buffer.BlockCopy(blob, 244 + 256 * i, F[i], 0, 256);
            }
            Buffer.BlockCopy(blob, 4, K, 0, 240);
        }

        public void Block(byte[] b, int bo, byte[] o, int oo)
        {
            uint s0 = W(b, bo, 0), s1 = W(b, bo, 1), s2 = W(b, bo, 2), s3 = W(b, bo, 3);
            var T = this.T;
            if (!Ctr)
            {
                for (int r = 4; r < 56; r += 4)
                {
                    uint t0 = K[r] ^ T[7][s1 & 255] ^ T[0][s0 >> 24] ^ T[13][(s3 >> 16) & 255] ^ T[10][(s2 >> 8) & 255];
                    uint t1 = K[r + 1] ^ T[11][s2 & 255] ^ T[4][s1 >> 24] ^ T[14][(s3 >> 8) & 255] ^ T[1][(s0 >> 16) & 255];
                    uint t2 = K[r + 2] ^ T[15][s3 & 255] ^ T[8][s2 >> 24] ^ T[2][(s0 >> 8) & 255] ^ T[5][(s1 >> 16) & 255];
                    uint t3 = K[r + 3] ^ T[3][s0 & 255] ^ T[12][s3 >> 24] ^ T[6][(s1 >> 8) & 255] ^ T[9][(s2 >> 16) & 255];
                    s0 = t0; s1 = t1; s2 = t2; s3 = t3;
                }
                o[oo + 0] = F[0][s0 >> 24]; o[oo + 1] = F[1][(s3 >> 16) & 255]; o[oo + 2] = F[2][(s2 >> 8) & 255]; o[oo + 3] = F[3][s1 & 255];
                o[oo + 4] = F[4][s1 >> 24]; o[oo + 5] = F[5][(s0 >> 16) & 255]; o[oo + 6] = F[6][(s3 >> 8) & 255]; o[oo + 7] = F[7][s2 & 255];
                o[oo + 8] = F[8][s2 >> 24]; o[oo + 9] = F[9][(s1 >> 16) & 255]; o[oo + 10] = F[10][(s0 >> 8) & 255]; o[oo + 11] = F[11][s3 & 255];
                o[oo + 12] = F[12][s3 >> 24]; o[oo + 13] = F[13][(s2 >> 16) & 255]; o[oo + 14] = F[14][(s1 >> 8) & 255]; o[oo + 15] = F[15][s0 & 255];
            }
            else
            {
                for (int r = 4; r < 56; r += 4)
                {
                    uint t0 = K[r] ^ T[15][s3 & 255] ^ T[0][s0 >> 24] ^ T[10][(s2 >> 8) & 255] ^ T[5][(s1 >> 16) & 255];
                    uint t1 = K[r + 1] ^ T[3][s0 & 255] ^ T[4][s1 >> 24] ^ T[9][(s2 >> 16) & 255] ^ T[14][(s3 >> 8) & 255];
                    uint t2 = K[r + 2] ^ T[7][s1 & 255] ^ T[8][s2 >> 24] ^ T[2][(s0 >> 8) & 255] ^ T[13][(s3 >> 16) & 255];
                    uint t3 = K[r + 3] ^ T[11][s2 & 255] ^ T[12][s3 >> 24] ^ T[1][(s0 >> 16) & 255] ^ T[6][(s1 >> 8) & 255];
                    s0 = t0; s1 = t1; s2 = t2; s3 = t3;
                }
                o[oo + 0] = F[0][s0 >> 24]; o[oo + 1] = F[1][(s1 >> 16) & 255]; o[oo + 2] = F[2][(s2 >> 8) & 255]; o[oo + 3] = F[3][s3 & 255];
                o[oo + 4] = F[4][s1 >> 24]; o[oo + 5] = F[5][(s2 >> 16) & 255]; o[oo + 6] = F[6][(s3 >> 8) & 255]; o[oo + 7] = F[7][s0 & 255];
                o[oo + 8] = F[8][s2 >> 24]; o[oo + 9] = F[9][(s3 >> 16) & 255]; o[oo + 10] = F[10][(s0 >> 8) & 255]; o[oo + 11] = F[11][s1 & 255];
                o[oo + 12] = F[12][s3 >> 24]; o[oo + 13] = F[13][(s0 >> 16) & 255]; o[oo + 14] = F[14][(s1 >> 8) & 255]; o[oo + 15] = F[15][s2 & 255];
            }
        }

        private uint W(byte[] b, int bo, int w)
        {
            int i = bo + 4 * w;
            return ((uint)IN[4 * w][b[i]] << 24 | (uint)IN[4 * w + 1][b[i + 1]] << 16 |
                    (uint)IN[4 * w + 2][b[i + 2]] << 8 | IN[4 * w + 3][b[i + 3]]) ^ K[w];
        }

        /// <summary>CBC decryption (type 16); a trailing partial block is copied as-is</summary>
        public byte[] DecryptCbc(byte[] data, int off, int len, byte[] iv)
        {
            var res = new byte[len];
            var prev = (byte[])iv.Clone();
            var blk = new byte[16];
            int full = len & ~15;
            for (int i = 0; i < full; i += 16)
            {
                Block(data, off + i, blk, 0);
                for (int j = 0; j < 16; j++) res[i + j] = (byte)(blk[j] ^ prev[j]);
                Buffer.BlockCopy(data, off + i, prev, 0, 16);
            }
            Buffer.BlockCopy(data, off + full, res, full, len - full);
            return res;
        }

        /// <summary>CTR (type 17): xor data with the keystream starting at counter IV + block (128-bit big-endian)</summary>
        public void XorCtr(byte[] data, int off, int len, byte[] iv, ulong block)
        {
            var ctr = (byte[])iv.Clone();
            AddBE(ctr, block);
            var ks = new byte[16];
            for (int i = 0; i < len; i += 16)
            {
                Block(ctr, 0, ks, 0);
                int n = Math.Min(16, len - i);
                for (int j = 0; j < n; j++) data[off + i + j] ^= ks[j];
                AddBE(ctr, 1);
            }
        }

        private static void AddBE(byte[] c, ulong v)
        {
            for (int i = 15; i >= 0 && v != 0; i--)
            {
                ulong s = c[i] + (v & 0xff);
                c[i] = (byte)s;
                v = (v >> 8) + (s >> 8);
            }
        }
    }

    /// <summary>Keys, names and extension ids for GTA VI archives. Load once with the game exe before scanning.</summary>
    public static class Rpf9Keys
    {
        // addresses in game_scarlett_master.exe (Xbox Series master build, SHA256 E95020B2...8993)
        const ulong VA_IV = 0x1462FD470, VA_IN = 0x146036F30, VA_T = 0x146037F30;
        const ulong VA_CIN = 0x146040F30, VA_CT = 0x146041F30, KEYTABLE = 0x1466E5900;
        static readonly byte[] MAGIC = { 0x8E, 0x9B, 0x3B, 0x4E }, CTR_MAGIC = { 0x5D, 0xCE, 0x18, 0x27 };
        static readonly Dictionary<uint, ulong> KnownKeys = new Dictionary<uint, ulong>
        {
            { 0x10000001, 0x1466EE360 }, { 0x10000002, 0x1466EF460 }, { 0x10000015, 0x1466E7D60 },
            { 0x10000018, 0x1466EB060 }, { 0x10000019, 0x1466EC160 }, { 0x1000001A, 0x1466ED260 },
        };

        public static bool Loaded { get; private set; }
        public static string ExePath { get; private set; }
        public static string NamesPath { get; private set; }
        public static byte[] FixedIV;
        public static Dictionary<uint, Rpf9WhiteBox> TocCiphers = new Dictionary<uint, Rpf9WhiteBox>(); // by key id
        public static List<Rpf9WhiteBox> AllTocCiphers = new List<Rpf9WhiteBox>();
        public static Dictionary<uint, Rpf9WhiteBox> DataCiphers = new Dictionary<uint, Rpf9WhiteBox>(); // type-17 key id
        public static Dictionary<ulong, string> Names = new Dictionary<ulong, string>();

        /// <summary>
        /// Known extensions are cut off before a name is hashed and kept as an id in the row's record (resource record
        /// byte +0x10, file record byte +5). Ids 0-18 from sub_14056EE50, 64-115 from sub_144EBBCF0 (platform 'z').
        /// 255 = none (the name was hashed with its extension).
        /// </summary>
        public static readonly Dictionary<int, string> Extensions = BuildExtensions();

        static Dictionary<int, string> BuildExtensions()
        {
            var d = new Dictionary<int, string>();
            var a = "rpf zmf zdr zft zdd ztd zbn zbd zpd zbs zsd zmt zsc zcs zdatb zbdr zbdd zbft zvert".Split(' ');
            for (int i = 0; i < a.Length; i++) d[i] = a[i];
            var b = ("mrf cut gfx zcd zld zpmd zpm zed zpt zmap ztyp zch zldb zjd zad znv znvs zpl znd zvr zwr znh zfd zas zsm " +
                     "zscript zascript zsma iasf zfc zlfc zcg zasd rdb zfbs zcps zsp ztsl zmmd zfid zmsk zhdat znmd zsmm zascd zrd " +
                     "zsmam zldbmt znmd zkit zcgc zcov").Split(' ');
            for (int i = 0; i < b.Length; i++) d[64 + i] = b[i];
            return d;
        }

        public static void Load(string exePath, string namesPath)
        {
            var pe = new Rpf9Pe(exePath);
            FixedIV = pe.Read(VA_IV, 16);
            var blobs = pe.Blobs(MAGIC);
            var byVa = new Dictionary<ulong, Rpf9WhiteBox>();
            foreach (var kv in blobs)
            {
                var c = new Rpf9WhiteBox(pe, kv.Value, VA_IN, VA_T, false);
                byVa[kv.Key] = c;
                AllTocCiphers.Add(c);
            }
            foreach (var kv in KnownKeys)
            {
                if (byVa.TryGetValue(kv.Value, out var c)) TocCiphers[kv.Key] = c;
            }
            // key table: {u32 type << 24, u32 key id, u64 address, u64 length}
            for (ulong o = KEYTABLE; ; o += 24)
            {
                var e = pe.Read(o, 24);
                uint ty = BitConverter.ToUInt32(e, 0) >> 24, kid = BitConverter.ToUInt32(e, 4);
                ulong ptr = BitConverter.ToUInt64(e, 8), len = BitConverter.ToUInt64(e, 16);
                if (ptr < pe.ImageBase || ptr >= pe.ImageBase + 0x10000000 || len > 0x10000) break;
                var blob = pe.Read(ptr, (int)len);
                if (StartsWith(blob, CTR_MAGIC) && ty == 2)
                {
                    DataCiphers[kid] = new Rpf9WhiteBox(pe, blob, VA_CIN, VA_CT, true);
                }
                else if (StartsWith(blob, MAGIC) && !TocCiphers.ContainsKey(kid))
                {
                    foreach (var kv in blobs)
                    {
                        if (SameStart(kv.Value, blob, 16)) { TocCiphers[kid] = byVa[kv.Key]; break; }
                    }
                }
            }
            if (!string.IsNullOrEmpty(namesPath) && File.Exists(namesPath))
            {
                foreach (var line in File.ReadLines(namesPath))
                {
                    int t = line.IndexOf('\t');
                    if (t <= 0 || t == line.Length - 1) continue;
                    if (ulong.TryParse(line.Substring(0, t), System.Globalization.NumberStyles.HexNumber, null, out ulong h))
                        Names[h] = line.Substring(t + 1);
                }
            }
            ExePath = exePath;
            NamesPath = namesPath;
            Loaded = true;
        }

        static bool StartsWith(byte[] a, byte[] m) => a.Length >= m.Length && SameStart(a, m, m.Length);
        static bool SameStart(byte[] a, byte[] b, int n)
        {
            if (a.Length < n || b.Length < n) return false;
            for (int i = 0; i < n; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>the name as the game knows it: name + "." + extension for a known extension id</summary>
        public static string WithExtension(string name, int extId)
        {
            if (Extensions.TryGetValue(extId, out var e) && !name.EndsWith("." + e, StringComparison.OrdinalIgnoreCase))
                return name + "." + e;
            return name;
        }
    }
}
