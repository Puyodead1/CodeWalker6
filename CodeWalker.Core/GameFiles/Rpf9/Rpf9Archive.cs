using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeWalker.GameFiles
{
    /// <summary>A file row of a GTA VI (RPF9) archive.</summary>
    public class Rpf9FileEntry : RpfBinaryFileEntry
    {
        public int Row { get; set; }
        public ulong NameHash64 { get; set; }
        public bool IsNamed { get; set; }
        public bool IsResource { get; set; }
        public int ExtensionId { get; set; } = 255;
        public string Extension => Rpf9Keys.Extensions.TryGetValue(ExtensionId, out var e) ? e : "";
        public long DataOffset { get; set; }        // bytes, from the start of the archive
        public long CompressedSize { get; set; }    // bytes
        public long UncompressedSize { get; set; }  // resources: virtual + physical part
        public long VirtualSize { get; set; }       // resources: size of the virtual (CPU) part
        public int DataStart { get; set; }          // resources: 16 (16 zero bytes precede the data)
        public uint ResourceVersion { get; set; }
        public uint KeyId { get; set; }
        public int CipherType { get; set; }
        public byte[] IV { get; set; }
        public long[] EncParams { get; set; }
        /// <summary>info chunk table (column e0c12d7a5d4884c5): per chunk {exact stored size, padding, output size,
        /// method (3 = zlib, else stored)}; null when the row has none (stored, or the content heuristic)</summary>
        public long[][] Chunks { get; set; }

        public override long GetFileSize() => UncompressedSize;
        public override string ToString() => (IsResource ? "RPF9 resource: " : "RPF9 file: ") + Path;
    }

    internal class Rpf9Toc
    {
        public const ulong H_FILE = 0x33f70dfaf2c50989, H_ENC = 0x286030a358a789fb, H_NAME = 0x73bcb72dce74f11f,
            H_H32 = 0xb7076b6245d35fb5, H_INFO = 0xe0c12d7a5d4884c5, H_RES = 0xba180afb232d1b4b;

        public readonly byte[] T;
        public readonly int Lo, Hi, N, Nm, W, D, E, L, LE;
        public readonly uint None;
        private readonly ulong[] ColHash;
        private readonly int[] ColOff, ColEs;
        private readonly Dictionary<ulong, int> ByHash = new Dictionary<ulong, int>();

        public Rpf9Toc(byte[] t)
        {
            T = t;
            int end = BitConverter.ToInt32(t, 4), lk = BitConverter.ToInt32(t, 8);
            int na = BitConverter.ToUInt16(t, 0x1A);
            int bsz = BitConverter.ToInt32(t, 0x1C);
            Lo = BitConverter.ToUInt16(t, 0x20);
            Hi = BitConverter.ToUInt16(t, 0x22);
            N = BitConverter.ToInt32(t, 0x24);
            int s = 0x40 + ((8 * na + 15) & ~15) + bsz;
            ColHash = new ulong[Lo];
            ColOff = new int[Lo];
            ColEs = new int[Lo];
            for (int i = 0; i < Lo; i++)
            {
                ColHash[i] = BitConverter.ToUInt64(t, s + 24 * i);
                ColOff[i] = BitConverter.ToInt32(t, s + 24 * i + 12);
                ColEs[i] = BitConverter.ToInt32(t, s + 24 * i + 16);
                ByHash[ColHash[i]] = i;
            }
            Nm = Lo - Hi;
            D = s + 24 * Lo;
            W = t[0x2C];
            None = W >= 4 ? 0xFFFFFFFFu : (1u << (8 * W)) - 1;
            E = (D + W * N * Nm + 7) & ~7;
            L = lk;
            LE = (W * N * Hi + 15) & ~15;
        }

        private uint Index(int at)
        {
            switch (W)
            {
                case 1: return T[at];
                case 2: return BitConverter.ToUInt16(T, at);
                default: return BitConverter.ToUInt32(T, at);
            }
        }

        /// <summary>position of row r's record in column h within T, or -1 (es = record size)</summary>
        public int Pos(ulong h, int r, out int es)
        {
            es = 0;
            if (!ByHash.TryGetValue(h, out int ci)) return -1;
            uint k = ci < Nm ? Index(D + W * (r * Nm + ci)) : Index(L + W * (r * Hi + ci - Nm));
            if (k == None) return -1;
            int off = ColOff[ci], at;
            es = ColEs[ci];
            if ((es >> 16) != 0)
            {
                es &= 0xffff;
                at = L + LE + off + (int)k * es;
            }
            else at = E + off + (int)k * es;
            if (at < 0 || at + es > T.Length) return -1;
            return at;
        }

        public byte[] Get(ulong h, int r)
        {
            int at = Pos(h, r, out int es);
            if (at < 0) return null;
            var b = new byte[es];
            Buffer.BlockCopy(T, at, b, 0, es);
            return b;
        }

        /// <summary>row r's chunk table from its info record {u32 table offset from the record, u16 count, u8 8,
        /// u8 entry format} (decoder sub_14550DB40); null when absent or of an unknown format</summary>
        public long[][] Chunks(int r)
        {
            int p = Pos(H_INFO, r, out int es);
            if (p < 0 || es < 8) return null;
            long off = BitConverter.ToUInt32(T, p);
            int n = BitConverter.ToUInt16(T, p + 4), fmt = T[p + 7];
            if (fmt < 2 || fmt > 4 || n == 0 || p + off + 8L * n > T.Length) return null;
            var res = new long[n][];
            for (int i = 0; i < n; i++)
            {
                ulong e = BitConverter.ToUInt64(T, (int)(p + off + 8 * i));
                long pad, al, outn, m;
                if (fmt == 2) { pad = (long)((e >> 58) & 0xF); al = (long)((e >> 28) & 0xFFFFFF0); outn = (long)((e << 4) & 0xFFFFFF0); m = (long)(e >> 62); }
                else if (fmt == 3) { long w1 = (long)((e >> 16) & 0xFFFF); pad = w1 & 0xF; al = 0x10000 - w1 + pad; outn = 0x10000 - (long)(e & 0xFFFF); m = (long)((e >> 60) & 3); }
                else { pad = (long)((e >> 52) & 0xF); al = (long)((e >> 24) & 0xFFFFFF0); outn = (long)(e & 0xFFFFFFF); m = (long)((e >> 56) & 3); }
                res[i] = new[] { al - pad, pad, outn, m };
            }
            return res;
        }

        /// <summary>raw = the archive's first `end` bytes (u32 at +4); returns them with the header blocks and TOC
        /// decrypted, at the same offsets (port of rpf9toc.decrypt)</summary>
        public static byte[] Decrypt(byte[] raw)
        {
            if (raw.Length < 0x40 || BitConverter.ToUInt32(raw, 0) != 0x52504639) throw new InvalidDataException("not RPF9");
            int end = BitConverter.ToInt32(raw, 4), lk = BitConverter.ToInt32(raw, 8);
            if (raw[0x2E] == 0) return (byte[])raw.Clone();      // no header descriptor: the TOC is not encrypted (sub_140A349A0)
            if ((raw[0x2E] & 1) == 0) throw new InvalidDataException("RPF9 header +0x2E = " + raw[0x2E] + ": single-region layout (exe only, no archive uses it) not handled");
            int na = BitConverter.ToUInt16(raw, 0x1A);
            int bsz = BitConverter.ToInt32(raw, 0x1C), tail = BitConverter.ToInt32(raw, 0x30);
            int arr = (8 * na + 15) & ~15;
            int s = 0x40 + arr + bsz;
            if (!Rpf9Keys.TocCiphers.TryGetValue(0x10000001, out var hc)) throw new Exception("RPF9 header key not loaded");
            var ab = hc.DecryptCbc(raw, 0x40, s - tail - 0x40, Rpf9Keys.FixedIV);
            // another build's archive (PS5): its own header key and IV give the key descriptor
            if (!(arr + 22 <= ab.Length && (BitConverter.ToUInt32(ab, arr) >> 8) == 0x100000))
            {
                foreach (var alt in Rpf9Keys.AltHeaders)
                {
                    var t = alt.Key.DecryptCbc(raw, 0x40, s - tail - 0x40, alt.Value);
                    if (arr + 22 <= t.Length && (BitConverter.ToUInt32(t, arr) >> 8) == 0x100000) { ab = t; break; }
                }
            }
            uint kid = 0;
            byte[] iv0 = null;
            for (int o = arr; o + 22 <= ab.Length && (BitConverter.ToUInt32(ab, o) >> 8) == 0x100000; o += 24)
            {
                kid = BitConverter.ToUInt32(ab, o);
                iv0 = new byte[16];
                Buffer.BlockCopy(ab, o + 6, iv0, 0, 16);
                break;
            }
            if (iv0 == null) throw new InvalidDataException("no key descriptors in RPF9 header block");
            var ivToc = new byte[16];
            Buffer.BlockCopy(raw, s - 16, ivToc, 0, 16);
            if (!Rpf9Keys.TocCiphers.TryGetValue(kid, out var c))
            {
                int best = -1;
                foreach (var cand in Rpf9Keys.AllTocCiphers)
                {
                    var t = cand.DecryptCbc(raw, s, Math.Min(0x90, lk - s), ivToc);
                    int z = t.Count(x => x == 0);
                    if (z > best) { best = z; c = cand; }
                }
            }
            var toc = c.DecryptCbc(raw, s, lk - s, ivToc);
            var look = c.DecryptCbc(raw, lk, end - lk, iv0);
            var res = new byte[end];
            Buffer.BlockCopy(raw, 0, res, 0, 0x40);
            Buffer.BlockCopy(ab, 0, res, 0x40, ab.Length);
            Buffer.BlockCopy(raw, s - tail, res, s - tail, tail);
            Buffer.BlockCopy(toc, 0, res, s, toc.Length);
            Buffer.BlockCopy(look, 0, res, lk, look.Length);
            return res;
        }
    }

    /// <summary>Read-only window [off, off+len) of another stream (a plain nested archive inside its parent).
    /// Owns the base stream.</summary>
    internal class Rpf9SubStream : Stream
    {
        private readonly Stream B;
        private readonly long Off, Len;
        private long Pos;
        public Rpf9SubStream(Stream b, long off, long len) { B = b; Off = off; Len = len; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => Len;
        public override long Position { get => Pos; set => Pos = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Pos >= Len || count <= 0) return 0;
            count = (int)Math.Min(count, Len - Pos);
            B.Position = Off + Pos;
            int n = B.Read(buffer, offset, count);
            if (n > 0) Pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? Pos + offset : Len + offset;
            return Pos;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) B.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>A file's stored bytes with its encrypted parts decrypted (port of rpf9x.View).</summary>
    internal class Rpf9View
    {
        private readonly Stream S;
        private readonly long Off, Base;
        public readonly long Cs;
        private readonly Rpf9Cipher Ctr;
        private readonly byte[] Iv;
        private readonly List<long[]> Segs = new List<long[]>();

        public Rpf9View(Stream s, long off, long cs, Rpf9Cipher ctr = null, byte[] iv = null, long[] p = null, long bas = 0)
        {
            S = s; Off = off; Cs = cs; Ctr = ctr; Iv = iv; Base = bas;
            if (ctr == null) return;
            long a = p[0], b = p[1], c = p[2], d = p[3];
            long n = cs - bas;
            var segs = new List<long[]> { new[] { 0, Math.Min(a, n) } };
            for (long st = a; st < n && b != 0; st += b + c) segs.Add(new[] { st, Math.Min(st + b, n) });
            if (d != 0) segs.Add(new[] { Math.Max(n - d, 0), n });
            foreach (var sg in segs) if (sg[1] > sg[0]) Segs.Add(new[] { sg[0] + bas, sg[1] + bas });
        }

        public byte[] Read(long p, long n, long slack = 0)
        {
            n = Math.Max(0, Math.Min(n, Cs + slack - p));
            var buf = new byte[n];
            S.Position = Off + p;
            int got = 0;
            while (got < n)
            {
                int k = S.Read(buf, got, (int)n - got);
                if (k <= 0) break;
                got += k;
            }
            if (got < n) Array.Resize(ref buf, got);
            n = got;
            if (Ctr == null || n == 0) return buf;
            foreach (var sg in Segs)
            {
                long s = sg[0], e = sg[1];
                if (e <= p || s >= p + n) continue;
                long lo = Math.Max(s, p), hi = Math.Min(e, p + n);
                long j0 = (lo - s) & ~15L;
                var tmp = new byte[hi - s - j0];
                Buffer.BlockCopy(buf, (int)(lo - p), tmp, (int)(lo - s - j0), (int)(hi - lo));
                Ctr.XorCtr(tmp, 0, tmp.Length, Iv, (ulong)((s - Base) / 16 + j0 / 16));
                Buffer.BlockCopy(tmp, (int)(lo - s - j0), buf, (int)(lo - p), (int)(hi - lo));
            }
            return buf;
        }
    }

    /// <summary>Reads GTA VI (RPF9) archives into CodeWalker's RpfFile structure, and extracts their files.</summary>
    public static class Rpf9Archive
    {
        const int WIN = 0x10000, BLOCK = 0x10000;
        public static string CacheFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeWalker.Rpf9");

        public static bool IsRpf9(uint magic) => magic == 0x52504639;

        static bool ZHeader(byte[] h) => h.Length == 2 && h[0] == 0x78 && ((h[0] << 8) | h[1]) % 31 == 0;

        /// <summary>consecutive deflate streams from byte p until us bytes (port of rpf9x.inflate)</summary>
        static long Inflate(Rpf9View view, long us, bool zlib, Rpf9Out o, long p)
        {
            long n0 = o.Total, n = 0;
            while (n < us && p < view.Cs && !o.Full)
            {
                long left = us - n;
                bool zhdr = ZHeader(view.Read(p, 2));
                long rest = view.Cs - p - left;
                if (zlib && !zhdr && !(-16 < rest && rest < 16))
                {
                    // a single 64 KB block that didn't compress, stored as-is between zlib streams
                    int k = (int)Math.Min(BLOCK, left);
                    var b = view.Read(p, k);
                    if (b.Length < k) return n;
                    o.Write(b, 0, b.Length);
                    p += k;
                    n += k;
                    continue;
                }
                if (zlib && !zhdr)
                {
                    // the stored rest; the record's size is in 16-byte units and can fall up to 15 bytes short
                    while (left > 0 && !o.Full)
                    {
                        var b = view.Read(p, Math.Min(WIN, left), 15);
                        if (b.Length == 0) break;
                        o.Write(b, 0, b.Length);
                        p += b.Length;
                        n += b.Length;
                        left -= b.Length;
                    }
                    return n;
                }
                var inf = new Rpf9Inflater(view, p, o);
                bool done;
                try { done = inf.Run(zlib); }
                catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException) { return o.Total - n0; }
                n = o.Total - n0;
                if (!done) return n;
                p = inf.Pos;
                // streams start 16-byte aligned; skip the zero padding, but don't eat zeros that begin a raw block
                long q = (p + 15) & ~15L;
                if (q > p)
                {
                    var pad = view.Read(p, q - p);
                    if (pad.Length == q - p && pad.All(x => x == 0)) p = q;
                }
                for (;;)
                {
                    var b = view.Read(p, 4096);
                    int z = 0;
                    while (z < b.Length && b[z] == 0) z++;
                    if (b.Length == 0 || z == 0 || (z < b.Length && b[z] != 0x78)) break;
                    p += z;
                }
            }
            return n;
        }

        /// <summary>decompress by the info chunk table (port of rpf9x.inflate_chunks): each chunk is a zlib stream
        /// (method 3) or stored, 16-byte aligned. Returns the output length, or -1 when a chunk doesn't give its size.</summary>
        static long InflateChunks(Rpf9View view, long[][] chunks, Rpf9Out o, long p)
        {
            long n = 0;
            foreach (var c in chunks)
            {
                if (o.Full) return o.Total;
                long exact = c[0], pad = c[1], outn = c[2], m = c[3], t0 = o.Total;
                if (m == 3)
                {
                    var inf = new Rpf9Inflater(view, p, o);
                    try { inf.Run(true); }
                    catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException) { return -1; }
                }
                else
                {
                    for (long q = 0; q < outn && !o.Full; q += WIN)
                    {
                        var b = view.Read(p + q, Math.Min(WIN, outn - q), 15);
                        if (b.Length == 0) break;
                        o.Write(b, 0, b.Length);
                    }
                }
                long got = o.Total - t0;
                if (o.Full) return o.Total;
                if (got != outn) return -1;
                n += outn;
                p += exact + pad;
            }
            return n;
        }

        static Rpf9View View(Stream s, Rpf9FileEntry e)
        {
            Rpf9Cipher ctr = null;
            if (e.IV != null)
            {
                if (e.CipherType != 17 || !Rpf9Keys.DataCiphers.TryGetValue(e.KeyId, out ctr))
                    throw new Exception(string.Format("unknown key 0x{0:x} type {1}", e.KeyId, e.CipherType));
            }
            return new Rpf9View(s, e.DataOffset, e.CompressedSize, ctr, e.IV, e.EncParams, e.DataStart);
        }

        /// <summary>decompress the entry from s (the archive's own bytes) into o; want = stop after this many bytes</summary>
        static void Extract(Stream s, Rpf9FileEntry e, Rpf9Out o, long want)
        {
            if (e.DataOffset + e.CompressedSize > s.Length) throw new InvalidDataException("past end of archive");
            var view = View(s, e);
            long us = e.UncompressedSize, cs = e.CompressedSize;
            o.Want = want;
            bool Ok(long n) => want >= us ? n == us : n >= want;
            string comp = "";
            long got = 0;
            if (e.Chunks != null)
            {
                // the info record's chunk table gives each chunk's stored size and method (exact, from the exe)
                got = InflateChunks(view, e.Chunks, o, e.DataStart);
                if (Ok(got)) return;
                o.Reset();
                got = 0;
            }
            if (ZHeader(view.Read(e.DataStart, 2)))
            {
                comp = "zlib";
                got = Inflate(view, us, true, o, e.DataStart);
            }
            if (!Ok(got) && cs >= us && cs - us < 16)
            {
                o.Reset();
                comp = "stored";
                got = 0;
                for (long q = 0; q < us && !o.Full; q += WIN)
                {
                    var b = view.Read(e.DataStart + q, Math.Min(WIN, us - q));
                    o.Write(b, 0, b.Length);
                    got += b.Length;
                }
            }
            if (!Ok(got) && comp != "zlib")
            {
                o.Reset();
                comp = "deflate";
                got = Inflate(view, us, false, o, e.DataStart);
            }
            if (!Ok(got)) throw new InvalidDataException(string.Format("{0} gave {1} of {2} bytes", comp == "" ? "no method" : comp, got, us));
        }

        /// <summary>the file holding an archive's own (decompressed) bytes; a nested archive is decompressed to the
        /// cache folder the first time it's needed</summary>
        public static string GetDataPath(RpfFile f)
        {
            if (f.Parent == null) return f.FilePath;
            lock (f)
            {
                if (f.Rpf9DataPath != null && File.Exists(f.Rpf9DataPath)) return f.Rpf9DataPath;
                var pe = (Rpf9FileEntry)f.ParentFileEntry;
                var top = f.GetTopParent();
                var fi = new FileInfo(top.FilePath);
                string dir = System.IO.Path.Combine(CacheFolder, Sanitize(top.Name) + "_" + fi.Length.ToString("x") + "_" + fi.LastWriteTimeUtc.Ticks.ToString("x"));
                string fn = System.IO.Path.Combine(dir, Sanitize(f.Name) + "_" + Fnv64(f.Path).ToString("x16") + ".rpf");
                if (!File.Exists(fn) || new FileInfo(fn).Length != pe.UncompressedSize)
                {
                    Directory.CreateDirectory(dir);
                    string part = fn + ".part";
                    using (var src = OpenData(f.Parent))
                    using (var dst = new FileStream(part, FileMode.Create, FileAccess.ReadWrite))
                    {
                        var o = new Rpf9Out(0, dst);
                        Extract(src, pe, o, long.MaxValue);
                        o.Flush();
                    }
                    if (File.Exists(fn)) File.Delete(fn);
                    File.Move(part, fn);
                }
                f.Rpf9DataPath = fn;
                return fn;
            }
        }

        /// <summary>A stream of an archive's own (decompressed) bytes. A nested archive stored plain (no
        /// compression, no encryption, e.g. the audio.rpf packs) is read in place from its parent; any other is
        /// decompressed to the cache folder first (GetDataPath).</summary>
        public static Stream OpenData(RpfFile f)
        {
            if (f.Parent == null) return File.OpenRead(f.FilePath);
            if ((f.Rpf9DataPath == null) && (f.ParentFileEntry is Rpf9FileEntry pe) && (pe.IV == null)
                && (pe.CompressedSize >= pe.UncompressedSize) && (pe.CompressedSize - pe.UncompressedSize < 16))
            {
                var ps = OpenData(f.Parent);
                try
                {
                    long at = pe.DataOffset + pe.DataStart;
                    if (at + pe.UncompressedSize <= ps.Length)
                    {
                        var h = new byte[4];
                        ps.Position = at;
                        if (ps.Read(h, 0, 4) == 4 && IsRpf9(BitConverter.ToUInt32(h, 0)))
                            return new Rpf9SubStream(ps, at, pe.UncompressedSize);
                    }
                }
                catch { ps.Dispose(); throw; }
                ps.Dispose();
            }
            return File.OpenRead(GetDataPath(f));
        }

        /// <summary>the decompressed (and decrypted) contents of a file</summary>
        public static byte[] ExtractFile(Rpf9FileEntry e)
        {
            using (var s = OpenData(e.File))
            {
                var o = new Rpf9Out(e.UncompressedSize);
                Extract(s, e, o, long.MaxValue);
                return o.ToArray();
            }
        }

        /// <summary>the stored (compressed) bytes of a file, decrypted</summary>
        public static byte[] ExtractRaw(Rpf9FileEntry e)
        {
            using (var s = OpenData(e.File))
            {
                return View(s, e).Read(0, e.CompressedSize);
            }
        }

        /// <summary>the first n decompressed bytes</summary>
        static byte[] ExtractHead(Stream s, Rpf9FileEntry e, long n)
        {
            var o = new Rpf9Out(Math.Min(n, e.UncompressedSize));
            Extract(s, e, o, Math.Min(n, e.UncompressedSize));
            var r = o.ToArray();
            if (r.Length > n) Array.Resize(ref r, (int)n);
            return r;
        }

        /// <summary>read the archive's TOC from its first bytes (raw: at least the 0x40-byte header)</summary>
        static byte[] ReadHead(Func<long, byte[]> read)
        {
            var h = read(0x40);
            if (h.Length < 0x40 || !IsRpf9(BitConverter.ToUInt32(h, 0))) throw new InvalidDataException("not RPF9");
            uint end = BitConverter.ToUInt32(h, 4);
            var raw = read(end);
            if (raw.Length < end) throw new InvalidDataException("RPF9 TOC truncated");
            return Rpf9Toc.Decrypt(raw);
        }

        static readonly Regex BadChars = new Regex("[<>:\"|?*\\x00-\\x1f]");

        /// <summary>path from the names list -> safe path components (port of rpf9x.safe_parts)</summary>
        public static List<string> SafeParts(string p)
        {
            var parts = p.Replace('\\', '/').Split('/').Select(x => BadChars.Replace(x, "_").TrimEnd(' ', '.'))
                .Where(x => x.Length > 0 && x != "." && x != "..").ToList();
            if (parts.Count == 0) parts.Add("_");
            return parts;
        }

        static string Sanitize(string s) => BadChars.Replace(s, "_").Replace('\\', '_').Replace('/', '_');

        static ulong Fnv64(string s)
        {
            ulong h = 0xcbf29ce484222325;
            foreach (char c in s) { h ^= c; h *= 0x100000001b3; }
            return h;
        }

        /// <summary>Scan a root RPF9 archive (called from RpfFile.ScanStructure).</summary>
        public static void Scan(RpfFile rpf, Action<string> updateStatus, Action<string> errorLog)
        {
            if (!Rpf9Keys.Loaded) throw new Exception("GTA VI keys not loaded (Rpf9Keys.Load with the game exe)");
            byte[] head;
            using (var s = File.OpenRead(rpf.FilePath))
            {
                head = ReadHead(n =>
                {
                    var b = new byte[n];
                    s.Position = 0;
                    int got = 0;
                    while (got < n) { int k = s.Read(b, got, (int)n - got); if (k <= 0) break; got += k; }
                    if (got < n) Array.Resize(ref b, got);
                    return b;
                });
            }
            Build(rpf, head, updateStatus, errorLog);
        }

        static void Build(RpfFile rpf, byte[] head, Action<string> updateStatus, Action<string> errorLog)
        {
            updateStatus?.Invoke("Scanning " + rpf.Path + "...");
            var toc = new Rpf9Toc(head);
            rpf.IsRpf9 = true;
            rpf.Version = 0x52504639;
            rpf.EntryCount = (uint)toc.N;
            rpf.AllEntries = new List<RpfEntry>();
            rpf.Children = new List<RpfFile>();
            rpf.TotalFileCount = rpf.TotalFolderCount = rpf.TotalResourceCount = rpf.TotalBinaryFileCount = 0;
            rpf.GrandTotalRpfCount = 1;
            rpf.GrandTotalFileCount = 1;
            rpf.GrandTotalFolderCount = rpf.GrandTotalResourceCount = rpf.GrandTotalBinaryFileCount = 0;

            var root = new RpfDirectoryEntry { File = rpf, Name = "", NameLower = "", Path = rpf.Path };
            rpf.Root = root;
            rpf.AllEntries.Add(root);
            var dirs = new Dictionary<string, RpfDirectoryEntry>(StringComparer.OrdinalIgnoreCase) { { "", root } };
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nested = new List<Rpf9FileEntry>();

            RpfDirectoryEntry Dir(List<string> parts, int count)
            {
                string key = "";
                var cur = root;
                for (int i = 0; i < count; i++)
                {
                    key = key.Length == 0 ? parts[i] : key + "/" + parts[i];
                    if (!dirs.TryGetValue(key, out var d))
                    {
                        d = new RpfDirectoryEntry { File = rpf, Parent = cur, Name = parts[i], NameLower = parts[i].ToLowerInvariant() };
                        d.Path = cur.Path + "\\" + d.NameLower;
                        d.NameHash = JenkHash.GenHash(d.NameLower);
                        cur.Directories.Add(d);
                        rpf.AllEntries.Add(d);
                        dirs[key] = d;
                        rpf.TotalFolderCount++;
                        rpf.GrandTotalFolderCount++;
                    }
                    cur = d;
                }
                return cur;
            }

            for (int r = 0; r < toc.N; r++)
            {
                var fr = toc.Get(Rpf9Toc.H_FILE, r);
                var rr = toc.Get(Rpf9Toc.H_RES, r);
                var e = new Rpf9FileEntry { File = rpf, Row = r };
                if (fr != null)
                {
                    ulong x = BitConverter.ToUInt32(fr, 0), fl = BitConverter.ToUInt32(fr, 4);
                    e.DataOffset = (long)((x | ((fl & 0xff) << 32)) * 16);
                    e.CompressedSize = (long)BitConverter.ToUInt64(fr, 8) * 16;
                    e.UncompressedSize = (long)BitConverter.ToUInt64(fr, 16);
                }
                else if (rr != null)
                {
                    uint vs = BitConverter.ToUInt32(rr, 0), ps = BitConverter.ToUInt32(rr, 4);
                    e.CompressedSize = (long)BitConverter.ToUInt32(rr, 8) * 16;
                    e.DataOffset = (long)BitConverter.ToUInt32(rr, 12) * 16;
                    e.ResourceVersion = ((vs & 15) << 4) | (ps & 15);   // RSC8 version (sub_1428CED60)
                    e.DataStart = 16;
                    e.VirtualSize = vs & ~15u;
                    e.UncompressedSize = e.VirtualSize + (ps & ~15u);
                    e.IsResource = true;
                }
                else continue;
                e.ExtensionId = rr != null ? rr[16] : fr[5];
                e.Chunks = toc.Chunks(r);
                var er = toc.Get(Rpf9Toc.H_ENC, r);
                if (er != null)
                {
                    e.IsEncrypted = true;
                    e.KeyId = BitConverter.ToUInt32(er, 0);
                    e.CipherType = BitConverter.ToUInt16(er, 4);
                    e.IV = new byte[16];
                    Buffer.BlockCopy(er, 6, e.IV, 0, 16);
                    e.EncParams = new long[5];
                    for (int i = 0; i < 5; i++) e.EncParams[i] = (long)BitConverter.ToUInt64(er, 24 + 8 * i);
                    e.EncryptionType = (uint)e.CipherType;
                }
                var nh = toc.Get(Rpf9Toc.H_NAME, r);
                e.NameHash64 = nh != null && nh.Length >= 8 ? BitConverter.ToUInt64(nh, 0) : 0;
                var h32 = toc.Get(Rpf9Toc.H_H32, r);
                if (h32 != null && h32.Length >= 4) e.ShortNameHash = BitConverter.ToUInt32(h32, 0);
                e.FileSize = (uint)Math.Min(e.CompressedSize, uint.MaxValue);
                e.FileUncompressedSize = (uint)Math.Min(e.UncompressedSize, uint.MaxValue);

                List<string> parts;
                string ext = e.Extension;
                if (nh != null && Rpf9Keys.Names.TryGetValue(e.NameHash64, out var real))
                {
                    parts = SafeParts(Rpf9Keys.WithExtension(real, e.ExtensionId));
                    e.IsNamed = true;
                }
                else
                {
                    string stem = nh != null ? e.NameHash64.ToString("x16") : "row" + r.ToString("d5");
                    parts = new List<string> { "_unnamed", stem + "." + (ext.Length > 0 ? ext : "bin") };
                }
                string rel = string.Join("/", parts);
                if (!used.Add(rel))
                {
                    string last = parts[parts.Count - 1];
                    int dot = last.LastIndexOf('.');
                    parts[parts.Count - 1] = dot > 0 ? last.Substring(0, dot) + "~" + r + last.Substring(dot) : last + "~" + r;
                    used.Add(string.Join("/", parts));
                }
                var dir = Dir(parts, parts.Count - 1);
                e.Parent = dir;
                e.Name = parts[parts.Count - 1];
                e.NameLower = e.Name.ToLowerInvariant();
                e.Path = dir.Path + "\\" + e.NameLower;
                e.NameHash = JenkHash.GenHash(e.NameLower);
                dir.Files.Add(e);
                rpf.AllEntries.Add(e);
                rpf.TotalFileCount++;
                if (e.IsResource) rpf.TotalResourceCount++; else rpf.TotalBinaryFileCount++;
                if (e.ExtensionId == 0 || e.NameLower.EndsWith(".rpf")) nested.Add(e);
                else
                {
                    rpf.GrandTotalFileCount++;
                    if (e.IsResource) rpf.GrandTotalResourceCount++; else rpf.GrandTotalBinaryFileCount++;
                }
            }

            if (nested.Count == 0) return;
            using (var s = OpenData(rpf))
            {
                foreach (var e in nested)
                {
                    try
                    {
                        var h = ExtractHead(s, e, 0x40);
                        if (h.Length < 0x40 || !IsRpf9(BitConverter.ToUInt32(h, 0)))
                        {
                            rpf.GrandTotalFileCount++;
                            continue;
                        }
                        uint end = BitConverter.ToUInt32(h, 4);
                        var raw = ExtractHead(s, e, end);
                        var child = new RpfFile(e.Name, e.Path, e.UncompressedSize) { Parent = rpf, ParentFileEntry = e };
                        Build(child, Rpf9Toc.Decrypt(raw), updateStatus, errorLog);
                        rpf.GrandTotalRpfCount += child.GrandTotalRpfCount;
                        rpf.GrandTotalFileCount += child.GrandTotalFileCount;
                        rpf.GrandTotalFolderCount += child.GrandTotalFolderCount;
                        rpf.GrandTotalResourceCount += child.GrandTotalResourceCount;
                        rpf.GrandTotalBinaryFileCount += child.GrandTotalBinaryFileCount;
                        rpf.Children.Add(child);
                    }
                    catch (Exception ex)
                    {
                        errorLog?.Invoke(e.Path + ": " + ex.Message);
                    }
                }
            }
        }
    }
}
