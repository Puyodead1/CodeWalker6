using System;
using System.Collections.Generic;
using System.IO;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI (RPF9) cryptography. Nothing key-related is stored here: the white-box AES tables and key blobs are read
    /// from the game executable (game_scarlett_master.exe, Xbox Series master build, or the PS5 build's decrypted
    /// eboot.bin) at run time.
    /// TOC: white-box AES-256 decryption (cipher type 16) in CBC mode. File data: white-box AES-256 encryption
    /// (cipher type 17) used as a CTR keystream. See rpf9toc.py / rpf9x.py for the reverse-engineering notes.
    /// </summary>
    public class Rpf9Pe
    {
        public readonly byte[] Data;
        public readonly ulong ImageBase;
        /// <summary>not a PE (the PS5 eboot.bin, a decrypted fSELF whose segment data is plain): file offsets are addresses</summary>
        public readonly bool Raw;
        private readonly List<uint[]> Sections = new List<uint[]>(); // va, size, raw, rawsize

        public Rpf9Pe(string path)
        {
            Data = File.ReadAllBytes(path);
            if (Data.Length < 0x40 || Data[0] != 'M' || Data[1] != 'Z')
            {
                Raw = true;
                Sections.Add(new[] { 0u, (uint)Data.Length, 0u, (uint)Data.Length });
                return;
            }
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

        /// <summary>
        /// The address of the first 16-byte aligned region of len bytes whose first 16 bytes have FNV-1a hash fnv and whose
        /// SHA-1 is sha1, or 0 (the same idea as GTA5Keys' hash search of GTA5.exe, so a rebuilt exe works as long as the
        /// tables themselves are unchanged)
        /// </summary>
        public ulong FindHashed(uint fnv, byte[] sha1, int len)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                foreach (var sec in Sections)
                {
                    long end = Math.Min((long)sec[2] + sec[3], Data.Length) - len;
                    for (long i = sec[2]; i <= end; i += 16)
                    {
                        uint h = 2166136261;
                        for (int j = 0; j < 16; j++) h = (h ^ Data[i + j]) * 16777619;
                        if (h != fnv) continue;
                        var s = sha.ComputeHash(Data, (int)i, len);
                        bool same = true;
                        for (int j = 0; j < 20 && same; j++) same = s[j] == sha1[j];
                        if (same) return ImageBase + sec[0] + (ulong)(i - sec[2]);
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// The key table {u32 type &lt;&lt; 24, u32 key id, u64 blob address, u64 length}: the longest run of such records
        /// that includes one pointing at a known key blob. Returns its address and row count, or 0.
        /// </summary>
        public ulong FindKeyTable(ICollection<ulong> blobVas, out int rows)
        {
            var set = new HashSet<ulong>(blobVas);
            ulong best = 0;
            rows = 0;
            foreach (var sec in Sections)
            {
                long lo = sec[2], hi = Math.Min((long)sec[2] + sec[3], Data.Length);
                for (long i = lo + 8; i + 16 <= hi; i += 8)
                {
                    if (!set.Contains(BitConverter.ToUInt64(Data, (int)i))) continue;
                    long r = i - 8;
                    if (!IsKeyRecord(r, lo, hi)) continue;
                    while (IsKeyRecord(r - 24, lo, hi)) r -= 24;
                    int n = 0;
                    while (IsKeyRecord(r + 24L * n, lo, hi)) n++;
                    if (n > rows) { rows = n; best = ImageBase + sec[0] + (ulong)(r - lo); }
                    i = r + 24L * n; // past this run
                }
            }
            return best;
        }

        bool IsKeyRecord(long r, long lo, long hi)
        {
            if (r < lo || r + 24 > hi) return false;
            if ((BitConverter.ToUInt32(Data, (int)r) & 0xFFFFFF) != 0) return false;
            ulong ptr = BitConverter.ToUInt64(Data, (int)r + 8), len = BitConverter.ToUInt64(Data, (int)r + 16);
            return ptr >= ImageBase && ptr < ImageBase + 0x10000000 && len > 0 && len <= 0x10000;
        }
    }

    /// <summary>An AES-256 block function (one direction) with the CBC-decrypt and CTR modes the archives use.</summary>
    public abstract class Rpf9Cipher
    {
        public abstract void Block(byte[] b, int bo, byte[] o, int oo);

        /// <summary>CBC decryption (type 16); a trailing partial block is copied as-is</summary>
        public virtual byte[] DecryptCbc(byte[] data, int off, int len, byte[] iv)
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
        public virtual void XorCtr(byte[] data, int off, int len, byte[] iv, ulong block)
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

        protected static void AddBE(byte[] c, ulong v)
        {
            for (int i = 15; i >= 0 && v != 0; i--)
            {
                ulong s = c[i] + (v & 0xff);
                c[i] = (byte)s;
                v = (v >> 8) + (s >> 8);
            }
        }
    }

    /// <summary>Plain AES-256 with a key recovered from the white box (tools\gta6_keys.py); same results, much faster.</summary>
    public class Rpf9Aes : Rpf9Cipher
    {
        public readonly byte[] Key;
        public readonly bool Decryptor;
        private readonly System.Threading.ThreadLocal<System.Security.Cryptography.ICryptoTransform> Tf;

        public Rpf9Aes(byte[] key, bool decryptor)
        {
            Key = (byte[])key.Clone();
            Decryptor = decryptor;
            Tf = new System.Threading.ThreadLocal<System.Security.Cryptography.ICryptoTransform>(() =>
            {
                var a = System.Security.Cryptography.Aes.Create();
                a.Mode = System.Security.Cryptography.CipherMode.ECB;
                a.Padding = System.Security.Cryptography.PaddingMode.None;
                a.Key = Key;
                return Decryptor ? a.CreateDecryptor() : a.CreateEncryptor();
            });
        }

        public override void Block(byte[] b, int bo, byte[] o, int oo) => Tf.Value.TransformBlock(b, bo, 16, o, oo);

        public override byte[] DecryptCbc(byte[] data, int off, int len, byte[] iv)
        {
            var res = new byte[len];
            int full = len & ~15;
            if (full > 0)
            {
                Tf.Value.TransformBlock(data, off, full, res, 0);
                for (int j = 0; j < 16; j++) res[j] ^= iv[j];
                for (int i = 16; i < full; i++) res[i] ^= data[off + i - 16];
            }
            Buffer.BlockCopy(data, off + full, res, full, len - full);
            return res;
        }

        public override void XorCtr(byte[] data, int off, int len, byte[] iv, ulong block)
        {
            int nb = (len + 15) / 16;
            if (nb == 0) return;
            var ctr = new byte[nb * 16];
            var c = (byte[])iv.Clone();
            AddBE(c, block);
            for (int i = 0; i < nb; i++) { Buffer.BlockCopy(c, 0, ctr, 16 * i, 16); AddBE(c, 1); }
            var ks = new byte[nb * 16];
            Tf.Value.TransformBlock(ctr, 0, ctr.Length, ks, 0);
            for (int i = 0; i < len; i++) data[off + i] ^= ks[i];
        }
    }

    /// <summary>White-box AES-256 block function (13 table rounds + final per-byte tables from the key blob).</summary>
    public class Rpf9WhiteBox : Rpf9Cipher
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

        public override void Block(byte[] b, int bo, byte[] o, int oo)
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
    }

    /// <summary>Keys, names and extension ids for GTA VI archives. Load once with the game exe before scanning.</summary>
    public static class Rpf9Keys
    {
        // Found in the exe by hash, like GTA5Keys does for GTA5.exe: FNV-1a of the first 16 bytes (quick filter) and SHA-1
        // of the whole region. Values from game_scarlett_master.exe (Xbox Series master build, SHA256 E95020B2...8993),
        // where the TOC IV is at 0x1462FD470, the type-16 tables (IN, T = IN + 0x1000) at 0x146036F30 and the type-17
        // tables at 0x146040F30. The key blobs (magic) and the key table (record layout) are found by their structure.
        const uint IV_FNV = 0x62EA022E, TOC_TABLES_FNV = 0x203F0A3D, CTR_TABLES_FNV = 0xFA0F86AD;
        static readonly byte[] IV_SHA1 = Hex("5fed0c3bf7a4c1df050297e6e5a7467302c7374f");
        static readonly byte[] TOC_TABLES_SHA1 = Hex("b1cf50a18eee7caf594bc7d7c4c7ef4f5921314e");
        static readonly byte[] CTR_TABLES_SHA1 = Hex("98bd056264b3d73a97ed932c925079fc353131e9");
        const int TABLES_SIZE = 0x5000; // 16 x 256-byte input tables, then 16 x 256 u32 round tables
        static readonly byte[] MAGIC = { 0x8E, 0x9B, 0x3B, 0x4E }, CTR_MAGIC = { 0x5D, 0xCE, 0x18, 0x27 };
        // PS5 eboot.bin (decrypted fSELF, 195555354 bytes): the same white-box tables as the Xbox exe (found by the hashes
        // above, at file offsets 0x9B81130 / 0x9B8B130), but its own TOC IV (0x9B0DE30) and RPF9 header key blob
        // (0x9EBD240). Its key table has no blob addresses (relocations), so the header key is found by hash too.
        const uint PS5_IV_FNV = 0xCC522800, PS5_HEADER_FNV = 0xD024E3FA;
        static readonly byte[] PS5_IV_SHA1 = Hex("ed52c4a1ff17520bd2fdaad64c6bd290a178b485");
        static readonly byte[] PS5_HEADER_SHA1 = Hex("13a195e6689d7b9d1f9fb3ddeda67c21c77f3f71");
        const int BLOB_SIZE = 4340;

        static byte[] Hex(string s)
        {
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(2 * i, 2), 16);
            return b;
        }

        public const string KeyFileName = "gta6_keys.dat";
        public static bool Loaded { get; private set; }
        public static string ExePath { get; private set; }
        public static string KeyFilePath { get; private set; }
        public static string NamesPath { get; private set; }
        public static byte[] FixedIV;
        public static byte[] LsnsIV;
        public static Dictionary<uint, Rpf9Cipher> TocCiphers = new Dictionary<uint, Rpf9Cipher>(); // by key id
        public static List<Rpf9Cipher> AllTocCiphers = new List<Rpf9Cipher>();
        public static Dictionary<uint, Rpf9Cipher> DataCiphers = new Dictionary<uint, Rpf9Cipher>(); // type-17 key id
        public static Dictionary<uint, Rpf9Cipher> LsnsCiphers = new Dictionary<uint, Rpf9Cipher>(); // type-19 key id (decrypt)
        public static Dictionary<int, uint[]> RelKeys = new Dictionary<int, uint[]>(); // audio .rel type id -> XXTEA key
        public static Dictionary<ulong, string> Names = new Dictionary<ulong, string>();

        /// <summary>the key file next to the application, if there is one</summary>
        public static string DefaultKeyFile()
        {
            var p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, KeyFileName);
            return File.Exists(p) ? p : null;
        }

        public const string Ps5KeyFileName = "gta6_keys_ps5.dat";
        /// <summary>header key and IV of other builds (PS5): tried when the main header key finds no key descriptor</summary>
        public static List<KeyValuePair<Rpf9Cipher, byte[]>> AltHeaders = new List<KeyValuePair<Rpf9Cipher, byte[]>>();

        /// <summary>
        /// Add another build's keys (same file format) to the loaded ones: its header key (id 0x10000001 in its file)
        /// and IV become an alternative header, its other keys join the tables under their ids (ids already there stay).
        /// </summary>
        static void MergeKeyFile(string keyPath)
        {
            var d = File.ReadAllBytes(keyPath);
            if (d.Length < 0x2C || d[0] != 'G' || d[1] != '6' || d[2] != 'K' || d[3] != 'Y' || BitConverter.ToInt32(d, 4) != 1) return;
            int n = BitConverter.ToInt32(d, 0x28), p = 0x2C;
            Rpf9Cipher header = null;
            byte[] iv = null;
            for (int i = 0; i < n && p + 8 <= d.Length; i++)
            {
                int kind = BitConverter.ToUInt16(d, p), size = BitConverter.ToUInt16(d, p + 2);
                uint id = BitConverter.ToUInt32(d, p + 4);
                int q = p + 8;
                p = q + size;
                if (p > d.Length) break;
                if (kind == 1 && size >= 36)
                {
                    int cipher = d[q + 1];
                    bool dec = d[q + 2] == 0;
                    var key = new byte[32];
                    Buffer.BlockCopy(d, q + 4, key, 0, 32);
                    var c = new Rpf9Aes(key, dec);
                    if (cipher == 16 && dec)
                    {
                        AllTocCiphers.Add(c);
                        if (id == 0x10000001) header = c;
                        else if (!TocCiphers.ContainsKey(id)) TocCiphers[id] = c;
                    }
                    else if (cipher == 17 && !dec && !DataCiphers.ContainsKey(id)) DataCiphers[id] = c;
                }
                else if (kind == 2 && size == 16 && id == 16)
                {
                    iv = new byte[16];
                    Buffer.BlockCopy(d, q, iv, 0, 16);
                }
            }
            if (header != null && iv != null) AltHeaders.Add(new KeyValuePair<Rpf9Cipher, byte[]>(header, iv));
        }

        /// <summary>
        /// Load the plain AES-256 keys written by tools\gta6_keys.py (format in that script's docstring): no game exe needed.
        /// </summary>
        public static void LoadKeyFile(string keyPath, string namesPath)
        {
            var d = File.ReadAllBytes(keyPath);
            if (d.Length < 0x2C || d[0] != 'G' || d[1] != '6' || d[2] != 'K' || d[3] != 'Y') throw new InvalidDataException("not a GTA VI key file: " + keyPath);
            int ver = BitConverter.ToInt32(d, 4);
            if (ver != 1) throw new InvalidDataException("unsupported key file version " + ver);
            int n = BitConverter.ToInt32(d, 0x28), p = 0x2C;
            var toc = new Dictionary<uint, Rpf9Cipher>();
            var data = new Dictionary<uint, Rpf9Cipher>();
            var lsns = new Dictionary<uint, Rpf9Cipher>();
            var all = new List<Rpf9Cipher>();
            var seen = new Dictionary<string, Rpf9Cipher>();
            var rel = new Dictionary<int, uint[]>();
            byte[] tocIv = null, lsnsIv = null;
            for (int i = 0; i < n; i++)
            {
                if (p + 8 > d.Length) throw new InvalidDataException("key file truncated");
                int kind = BitConverter.ToUInt16(d, p), size = BitConverter.ToUInt16(d, p + 2);
                uint id = BitConverter.ToUInt32(d, p + 4);
                int q = p + 8;
                p = q + size;
                if (p > d.Length) throw new InvalidDataException("key file truncated");
                if (kind == 1 && size >= 36)
                {
                    int cipher = d[q + 1];
                    bool dec = d[q + 2] == 0;
                    var key = new byte[32];
                    Buffer.BlockCopy(d, q + 4, key, 0, 32);
                    string sk = (dec ? "d" : "e") + BitConverter.ToString(key);
                    if (!seen.TryGetValue(sk, out var c)) { c = new Rpf9Aes(key, dec); seen[sk] = c; if (cipher == 16 && dec) all.Add(c); }
                    if (cipher == 16 && dec) toc[id] = c;
                    else if (cipher == 17 && !dec) data[id] = c;
                    else if (cipher == 19 && dec) lsns[id] = c;
                }
                else if (kind == 2 && size == 16)
                {
                    var iv = new byte[16];
                    Buffer.BlockCopy(d, q, iv, 0, 16);
                    if (id == 16) tocIv = iv; else if (id == 19) lsnsIv = iv;
                }
                else if (kind == 3 && size == 16)
                {
                    var k = new uint[4];
                    for (int j = 0; j < 4; j++) k[j] = BitConverter.ToUInt32(d, q + 4 * j);
                    rel[(int)id] = k;
                }
            }
            if (tocIv == null || !toc.ContainsKey(0x10000001)) throw new InvalidDataException("key file has no RPF9 header key / IV");
            FixedIV = tocIv; LsnsIV = lsnsIv;
            TocCiphers = toc; DataCiphers = data; LsnsCiphers = lsns; AllTocCiphers = all; RelKeys = rel;
            AltHeaders = new List<KeyValuePair<Rpf9Cipher, byte[]>>();
            // the PS5 build's keys (tools\ps5_keys.py), beside the key file: its archives open in the same session
            var ps5 = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(keyPath)), Ps5KeyFileName);
            if (File.Exists(ps5) && !string.Equals(Path.GetFullPath(ps5), Path.GetFullPath(keyPath), StringComparison.OrdinalIgnoreCase)) MergeKeyFile(ps5);
            LoadNames(namesPath);
            KeyFilePath = keyPath;
            ExePath = null;
            NamesPath = namesPath;
            Loaded = true;
        }

        /// <summary>
        /// archive entry names: every name in strings_gta6.txt under its RPF9 name hash (Rpf9Strings, like GTA V's
        /// strings.txt), plus an optional extra "hash TAB name" list (--names=)
        /// </summary>
        static void LoadNames(string namesPath)
        {
            foreach (var kv in Rpf9Strings.Names64) Names[kv.Key] = kv.Value;
            if (string.IsNullOrEmpty(namesPath) || !File.Exists(namesPath)) return;
            foreach (var line in File.ReadLines(namesPath))
            {
                int t = line.IndexOf('\t');
                if (t <= 0 || t == line.Length - 1) continue;
                if (ulong.TryParse(line.Substring(0, t), System.Globalization.NumberStyles.HexNumber, null, out ulong h))
                    Names[h] = line.Substring(t + 1);
            }
        }

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

        /// <summary>
        /// Lift the RPF9 TOC and file data ciphers from the game exe: the Xbox game_scarlett_master.exe or the PS5
        /// eboot.bin (decrypted). The white-box tables are used as they are. Throws when the file doesn't hold the known
        /// tables. The PSIN string (type 19) and audio .rel keys come only from a key file.
        /// </summary>
        public static void Load(string exePath, string namesPath)
        {
            var pe = new Rpf9Pe(exePath);
            string what = pe.Raw ? "PS5 eboot.bin" : "GTA VI exe";
            ulong vaIv = pe.Raw ? pe.FindHashed(PS5_IV_FNV, PS5_IV_SHA1, 16) : pe.FindHashed(IV_FNV, IV_SHA1, 16);
            ulong vaIn = pe.FindHashed(TOC_TABLES_FNV, TOC_TABLES_SHA1, TABLES_SIZE);
            ulong vaCin = pe.FindHashed(CTR_TABLES_FNV, CTR_TABLES_SHA1, TABLES_SIZE);
            ulong vaHeader = pe.Raw ? pe.FindHashed(PS5_HEADER_FNV, PS5_HEADER_SHA1, BLOB_SIZE) : 0;
            var missing = new List<string>();
            if (vaIv == 0) missing.Add("TOC IV");
            if (vaIn == 0) missing.Add("TOC cipher tables");
            if (vaCin == 0) missing.Add("file data cipher tables");
            if (pe.Raw && vaHeader == 0) missing.Add("RPF9 header key");
            if (missing.Count > 0) throw new InvalidDataException("not a supported " + what + ", not found: " + string.Join(", ", missing));

            var blobs = pe.Blobs(MAGIC);
            var toc = new Dictionary<uint, Rpf9Cipher>();
            var data = new Dictionary<uint, Rpf9Cipher>();
            var all = new List<Rpf9Cipher>();
            var byVa = new Dictionary<ulong, Rpf9WhiteBox>();
            foreach (var kv in blobs)
            {
                var c = new Rpf9WhiteBox(pe, kv.Value, vaIn, vaIn + 0x1000, false);
                byVa[kv.Key] = c;
                all.Add(c);
            }
            if (pe.Raw)
            {
                // the other TOC keys have no known ids here: Rpf9Toc.Decrypt finds an archive's TOC key by trial
                if (byVa.TryGetValue(vaHeader, out var hc)) toc[0x10000001] = hc;
                // file data keys: the key table's ids 0x10000021.. in the order the blobs lie in the file
                var ctr = new List<ulong>(pe.Blobs(CTR_MAGIC).Keys);
                ctr.Sort();
                var ids = Ps5DataKeyIds(pe, ctr.Count == 0 ? 0 : ctr[ctr.Count - 1]);
                for (int i = 0; i < ctr.Count; i++)
                {
                    uint kid = i < ids.Count ? ids[i] : 0x10000021u + (uint)i;
                    data[kid] = new Rpf9WhiteBox(pe, pe.Read(ctr[i], BLOB_SIZE), vaCin, vaCin + 0x1000, true);
                }
            }
            else
            {
                ulong keyTable = pe.FindKeyTable(blobs.Keys, out int rows);
                if (keyTable == 0) throw new InvalidDataException("not a supported GTA VI exe: no key table");
                for (int i = 0; i < rows; i++)
                {
                    var e = pe.Read(keyTable + 24UL * (ulong)i, 24);
                    uint ty = BitConverter.ToUInt32(e, 0) >> 24, kid = BitConverter.ToUInt32(e, 4);
                    ulong ptr = BitConverter.ToUInt64(e, 8), len = BitConverter.ToUInt64(e, 16);
                    if (ty != 2) continue;
                    if (byVa.TryGetValue(ptr, out var tc)) { if (!toc.ContainsKey(kid)) toc[kid] = tc; continue; }
                    var blob = pe.Read(ptr, (int)len);
                    if (StartsWith(blob, CTR_MAGIC)) data[kid] = new Rpf9WhiteBox(pe, blob, vaCin, vaCin + 0x1000, true);
                }
            }
            if (!toc.ContainsKey(0x10000001)) throw new InvalidDataException("not a supported " + what + ": no RPF9 header key");

            FixedIV = pe.Read(vaIv, 16);
            TocCiphers = toc; DataCiphers = data; AllTocCiphers = all; AltHeaders = new List<KeyValuePair<Rpf9Cipher, byte[]>>();
            LsnsCiphers = new Dictionary<uint, Rpf9Cipher>(); LsnsIV = null; RelKeys = new Dictionary<int, uint[]>();
            LoadNames(namesPath);
            KeyFilePath = null;
            ExePath = exePath;
            NamesPath = namesPath;
            Loaded = true;
        }

        /// <summary>
        /// PS5: the file data key ids (0x10000021..) of the key table {u32 type &lt;&lt; 24, u32 key id, u64 0 (a relocation),
        /// u64 length}, which follows the key blobs and starts with the type-2 id 1 row; empty when it isn't found
        /// </summary>
        static List<uint> Ps5DataKeyIds(Rpf9Pe pe, ulong after)
        {
            var ids = new List<uint>();
            var d = pe.Data;
            for (long r = (long)after & ~7L; r + 24 <= d.Length; r += 8)
            {
                if (BitConverter.ToUInt32(d, (int)r) != 2u << 24 || BitConverter.ToUInt32(d, (int)r + 4) != 1 ||
                    BitConverter.ToUInt64(d, (int)r + 8) != 0 || BitConverter.ToUInt64(d, (int)r + 16) != BLOB_SIZE) continue;
                for (long o = r; o + 24 <= d.Length; o += 24)
                {
                    uint tw = BitConverter.ToUInt32(d, (int)o), kid = BitConverter.ToUInt32(d, (int)o + 4);
                    ulong len = BitConverter.ToUInt64(d, (int)o + 16);
                    if ((tw & 0xFFFFFF) != 0 || len == 0 || len > 0x10000) break;
                    if (tw >> 24 == 2 && len == BLOB_SIZE && kid >> 4 == 0x1000002) ids.Add(kid);
                }
                break;
            }
            return ids;
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
