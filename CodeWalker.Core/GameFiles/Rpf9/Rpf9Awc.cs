using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>GTA VI audio wave container ('ADAT'). GTA V's AwcFile can't read it: the format chunk is 0x76
    /// (GTA V: 0xFA), the flags mean other things, and most sounds are Opus. Port of vps_carver\awc.py; the
    /// layout is written up in data\audio_format.md.
    ///   +0 'ADAT'  +4 u16 version 1  +6 u16 flags  +8 u32 stream count  +12 u32 data offset
    ///   flags bit0: u16 per stream; bit5: (streams + 1) u32; bit6: one u32 (all before the stream table)
    ///   u32 per stream: id (low 29 bits, joaat of the sound name) | chunk count &lt;&lt; 29
    ///   u64 per chunk: type &lt;&lt; 56 | size &lt;&lt; 28 | offset (from the container start)
    /// Chunks: 0x55 data, 0x76 format (u32 samples, s32 loop, u16 rate @8, s16 headroom @10, u8 codec @28,
    /// u8 channels @29), 0xA3 seek table, 0x36 peak, 0x48 stream format (streamed banks), 0xBD markers, 0x68 MIDI.
    /// Codecs: 0x0B Opus, 0x05 XMA2, 0x00 PCM16, 0x02 float32 (convolution impulse responses).</summary>
    public static class Rpf9Awc
    {
        public const int OPUS = 0x0B, XMA2 = 0x05, PCM16 = 0x00, F32 = 0x02;
        const int PRESKIP = 312;

        public class Chunk { public int Type, Size, Offset; }

        public class AwcStream
        {
            public uint Id;
            public List<Chunk> Chunks = new List<Chunk>();
            public int Codec = -1, Channels, Rate, Headroom, Loop;
            public uint Samples;
            public Chunk Get(int t) { foreach (var c in Chunks) if (c.Type == t) return c; return null; }
        }

        public class Container
        {
            public int Version, Flags, DataOffset, Extent;
            public List<AwcStream> Streams = new List<AwcStream>();
        }

        /// <summary>One sound of a streamed bank: a channel of the 0x48 stream-format chunk.</summary>
        public class StreamedChannel
        {
            public uint Id, Samples;
            public int Rate, Codec, PacketChannels;
            public List<byte[]> Packets = new List<byte[]>();
            public long Bytes;
        }

        public static bool IsAwc(byte[] d)
        {
            return d != null && d.Length >= 16 && d[0] == 'A' && d[1] == 'D' && d[2] == 'A' && d[3] == 'T';
        }

        static ushort U16(byte[] d, int o) { return BitConverter.ToUInt16(d, o); }
        static uint U32(byte[] d, int o) { return BitConverter.ToUInt32(d, o); }

        public static Container Parse(byte[] d)
        {
            if (!IsAwc(d)) throw new InvalidDataException("not an ADAT container");
            var c = new Container { Version = U16(d, 4), Flags = U16(d, 6) };
            int ns = (int)U32(d, 8);
            c.DataOffset = (int)U32(d, 12);
            if (c.Version != 1 || ns < 0 || ns > 100000) throw new InvalidDataException("version " + c.Version + ", " + ns + " streams");
            int o = 16 + ((c.Flags & 1) != 0 ? 2 * ns : 0) + ((c.Flags & 0x20) != 0 ? 4 * (ns + 1) : 0) + ((c.Flags & 0x40) != 0 ? 4 : 0);
            if (o + 4 * ns > d.Length) throw new InvalidDataException("stream table past the end");
            var ids = new uint[ns];
            for (int i = 0; i < ns; i++) ids[i] = U32(d, o + 4 * i);
            o += 4 * ns;
            int end = o;
            foreach (var v in ids)
            {
                var s = new AwcStream { Id = v & 0x1FFFFFFF };
                for (int k = 0; k < (int)(v >> 29); k++)
                {
                    if (o + 8 > d.Length) throw new InvalidDataException("chunk table past the end");
                    ulong q = BitConverter.ToUInt64(d, o); o += 8;
                    var ch = new Chunk { Type = (int)(q >> 56), Size = (int)((q >> 28) & 0xFFFFFFF), Offset = (int)(q & 0xFFFFFFF) };
                    if ((long)ch.Offset + ch.Size > d.Length) throw new InvalidDataException(string.Format("stream {0:x7} chunk {1:x2} past the end", s.Id, ch.Type));
                    s.Chunks.Add(ch);
                    end = Math.Max(end, ch.Offset + ch.Size);
                }
                var f = s.Get(0x76);
                if (f != null && f.Size >= 30)
                {
                    s.Samples = U32(d, f.Offset);
                    s.Loop = BitConverter.ToInt32(d, f.Offset + 4);
                    s.Rate = U16(d, f.Offset + 8);
                    s.Headroom = BitConverter.ToInt16(d, f.Offset + 10);
                    s.Codec = d[f.Offset + 28];
                    s.Channels = d[f.Offset + 29];
                }
                c.Streams.Add(s);
            }
            c.Extent = end;
            return c;
        }

        // ------------------------------------------------------------------ Opus packets
        /// <summary>Samples (48 kHz encoder units) in one Opus packet, from its TOC byte (RFC 6716 3.1).</summary>
        public static int OpusSamples(byte[] p)
        {
            if (p == null || p.Length == 0) return 0;
            int c = p[0] >> 3, fs;
            if (c < 12) fs = new[] { 480, 960, 1920, 2880 }[c & 3];
            else if (c < 16) fs = new[] { 480, 960 }[c & 1];
            else fs = new[] { 120, 240, 480, 960 }[c & 3];
            int code = p[0] & 3;
            int n = code == 0 ? 1 : code < 3 ? 2 : (p.Length > 1 ? p[1] & 0x3F : 0);
            return fs * n;
        }

        /// <summary>Packets of each block: [u16 length + packet]..., zero-padded tail.</summary>
        static List<List<byte[]>> Blocks(byte[] d, int b0, int size, int bs)
        {
            var o = new List<List<byte[]>>();
            for (int k = 0; k < (size + bs - 1) / bs; k++)
            {
                int q = b0 + bs * k, qe = b0 + Math.Min(size, bs * (k + 1));
                var pk = new List<byte[]>();
                while (q + 2 <= qe)
                {
                    int ln = U16(d, q);
                    if (ln == 0 || q + 2 + ln > qe) break;
                    var p = new byte[ln];
                    Buffer.BlockCopy(d, q + 2, p, 0, ln);
                    pk.Add(p);
                    q += 2 + ln;
                }
                o.Add(pk);
            }
            return o;
        }

        /// <summary>Opus packets of a 0x55 chunk. Blocks are 2 KB in dialogue banks, larger in others: the block
        /// size whose per-block durations match the seek table is used (seek positions count every channel).</summary>
        public static List<byte[]> OpusPackets(byte[] d, Chunk data, uint[] seek, int channels)
        {
            if (seek == null || seek.Length < 2) return Blocks(d, data.Offset, data.Size, 2048).SelectMany(b => b).ToList();
            channels = Math.Max(1, channels);
            var want = new long[seek.Length - 1];
            for (int k = 0; k < want.Length; k++) want[k] = ((long)seek[k + 1] - seek[k]) / channels;
            for (int bs = 2048; ; bs *= 2)
            {
                var blk = Blocks(d, data.Offset, data.Size, bs);
                if (blk.Count == want.Length)
                {
                    bool ok = true;
                    for (int k = 0; k < blk.Count && ok; k++)
                    {
                        long got = blk[k].Sum(p => (long)OpusSamples(p));
                        ok = k < blk.Count - 1 ? got == want[k] : got - want[k] >= 0 && got - want[k] <= 2880;
                    }
                    if (ok) return blk.SelectMany(b => b).ToList();
                }
                if (bs >= data.Size) break;
            }
            throw new InvalidDataException("no Opus block size fits the seek table");
        }

        /// <summary>Streamed bank: stream 0's 0x48 chunk = u32 blocks, u32 block size, u32 channels, then 16 B per
        /// channel (u32 id, u32 samples, u16 headroom, u16 rate, u8 codec, ...). Each block: per channel 6 u32
        /// (-1, frames, samples to skip, samples, 0, 0), per channel a seek table, pad to 2 KB, then each channel's
        /// 2 KB frames. A block repeats the last `skip` samples of the one before: those packets are dropped.</summary>
        public static List<StreamedChannel> StreamChannels(byte[] d, Chunk sf, Chunk data)
        {
            if (sf.Size < 12) throw new InvalidDataException("stream format chunk too small");
            int nb = (int)U32(d, sf.Offset), bs = (int)U32(d, sf.Offset + 4), nc = (int)U32(d, sf.Offset + 8);
            if (nc <= 0 || nc >= 256 || sf.Size < 12 + 16 * nc || bs < 0x800 || bs % 0x800 != 0)
                throw new InvalidDataException(string.Format("stream format: {0} channels, block size {1:x}", nc, bs));
            var chans = new List<StreamedChannel>();
            for (int k = 0; k < nc; k++)
            {
                int o = sf.Offset + 12 + 16 * k;
                chans.Add(new StreamedChannel { Id = U32(d, o) & 0x1FFFFFFF, Samples = U32(d, o + 4), Rate = U16(d, o + 10), Codec = d[o + 12] });
            }
            var bad = chans.FirstOrDefault(c => c.Codec != OPUS);
            if (bad != null) throw new InvalidDataException(string.Format("streamed codec {0:x2} not handled", bad.Codec));
            if ((data.Size + bs - 1) / bs != nb) throw new InvalidDataException(string.Format("{0} blocks of {1:x} in {2:x} bytes, header says {3}", (data.Size + bs - 1) / bs, bs, data.Size, nb));
            for (int b = 0; b < nb; b++)
            {
                int b0 = data.Offset + b * bs, end = data.Offset + Math.Min(data.Size, (b + 1) * bs);
                var frames = new int[nc]; var skip = new int[nc]; long seekLen = 0;
                for (int k = 0; k < nc; k++) { frames[k] = (int)U32(d, b0 + 24 * k + 4); skip[k] = (int)U32(d, b0 + 24 * k + 8); seekLen += 4L * frames[k]; }
                int q = b0 + (int)((24 * nc + seekLen + 0x7FF) & ~0x7FFL);
                for (int k = 0; k < nc; k++)
                {
                    int n = frames[k];
                    if (q + 0x800L * n > end) throw new InvalidDataException(string.Format("block {0}: channel {1} frames run past the block", b, k));
                    var got = Blocks(d, q, 0x800 * n, 0x800).SelectMany(x => x).ToList();
                    q += 0x800 * n;
                    chans[k].Bytes += 0x800 * n;
                    int pc = got.Count > 0 && (got[0][0] & 0x04) != 0 ? 2 : 1;
                    int drop = -1;
                    foreach (var want in new[] { skip[k] / pc, skip[k] }.Distinct())
                    {
                        int dr = 0; long acc = 0;
                        while (acc < want && dr < got.Count) acc += OpusSamples(got[dr++]);
                        if (acc == want) { drop = dr; break; }
                    }
                    if (drop < 0) throw new InvalidDataException(string.Format("block {0}: channel {1} skip {2} is not on a packet boundary", b, k, skip[k]));
                    chans[k].Packets.AddRange(got.Skip(drop));
                }
            }
            foreach (var c in chans) c.PacketChannels = c.Packets.Count > 0 && (c.Packets[0][0] & 0x04) != 0 ? 2 : 1;
            return chans;
        }

        // ------------------------------------------------------------------ writers
        static readonly uint[] CrcTable = MakeCrc();
        static uint[] MakeCrc()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint r = i << 24;
                for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
                t[i] = r;
            }
            return t;
        }

        static void Page(Stream o, List<byte[]> packets, long granule, uint serial, int seq, byte flags)
        {
            var lace = new List<byte>();
            foreach (var p in packets) { for (int i = 0; i < p.Length / 255; i++) lace.Add(255); lace.Add((byte)(p.Length % 255)); }
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("OggS")); w.Write((byte)0); w.Write(flags); w.Write(granule);
            w.Write(serial); w.Write(seq); w.Write(0u); w.Write((byte)lace.Count); w.Write(lace.ToArray());
            foreach (var p in packets) w.Write(p);
            w.Flush();
            var b = ms.ToArray();
            uint c = 0;
            foreach (var x in b) c = (c << 8) ^ CrcTable[(c >> 24) ^ x];
            BitConverter.GetBytes(c).CopyTo(b, 22);
            o.Write(b, 0, b.Length);
        }

        /// <summary>Lossless Ogg Opus wrap of the game's packets. The game encodes its 32 kHz sounds as if they were
        /// 48 kHz, so these play 1.5x fast and high (the input rate in OpusHead is set to the real rate).</summary>
        public static byte[] OggOpus(List<byte[]> packets, int channels, int rate, long samples, uint serial)
        {
            var o = new MemoryStream();
            var head = new MemoryStream();
            var hw = new BinaryWriter(head);
            hw.Write(Encoding.ASCII.GetBytes("OpusHead")); hw.Write((byte)1); hw.Write((byte)channels); hw.Write((ushort)PRESKIP);
            hw.Write((uint)rate); hw.Write((short)0); hw.Write((byte)0); hw.Flush();
            Page(o, new List<byte[]> { head.ToArray() }, 0, serial, 0, 2);
            var tags = new MemoryStream();
            var tw = new BinaryWriter(tags);
            tw.Write(Encoding.ASCII.GetBytes("OpusTags")); tw.Write(3u); tw.Write(Encoding.ASCII.GetBytes("awc")); tw.Write(0u); tw.Flush();
            Page(o, new List<byte[]> { tags.ToArray() }, 0, serial, 1, 0);
            long end = samples + PRESKIP, t = 0;
            int keep = 0;
            foreach (var p in packets) { if (keep > 0 && t >= end) break; t += OpusSamples(p); keep++; }
            int seq = 2, i = 0; long gran = 0;
            while (i < keep)
            {
                var grp = new List<byte[]>(); int segs = 0;
                while (i < keep && grp.Count < 50 && segs + packets[i].Length / 255 + 1 <= 255)
                {
                    grp.Add(packets[i]); segs += packets[i].Length / 255 + 1; i++;
                }
                gran += grp.Sum(p => (long)OpusSamples(p));
                bool last = i >= keep;
                Page(o, grp, last ? Math.Min(gran, end) : gran, serial, seq++, (byte)(last ? 4 : 0));
            }
            return o.ToArray();
        }

        static byte[] Riff(byte[] fmt, byte[] d, int off, int len)
        {
            var o = new MemoryStream();
            var w = new BinaryWriter(o);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(4 + 8 + fmt.Length + 8 + len);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(fmt.Length); w.Write(fmt);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(len); w.Write(d, off, len); w.Flush();
            return o.ToArray();
        }

        public static byte[] Wav(byte[] d, int off, int len, int channels, int rate)
        {
            var f = new MemoryStream(); var w = new BinaryWriter(f);
            w.Write((ushort)1); w.Write((ushort)channels); w.Write(rate); w.Write(rate * 2 * channels); w.Write((ushort)(2 * channels)); w.Write((ushort)16); w.Flush();
            return Riff(f.ToArray(), d, off, len);
        }

        /// <summary>XMA2 packets copied into a RIFF WAVE with an XMA2WAVEFORMATEX header (tag 0x166), as ffmpeg and
        /// vgmstream read it.</summary>
        public static byte[] Xma(byte[] d, int off, int len, int channels, int rate, uint samples)
        {
            const int BLOCK = 0x10000;
            var f = new MemoryStream(); var w = new BinaryWriter(f);
            double secs = samples > 0 && rate > 0 ? (double)samples / rate : 1;
            w.Write((ushort)0x166); w.Write((ushort)channels); w.Write(rate); w.Write((int)(len / secs)); w.Write((ushort)(2 * channels)); w.Write((ushort)16);
            w.Write((ushort)34);
            w.Write((ushort)((channels + 1) / 2)); w.Write(channels == 1 ? 4u : 3u); w.Write(samples); w.Write((uint)BLOCK);
            w.Write(0u); w.Write(samples); w.Write(0u); w.Write(0u); w.Write((byte)0); w.Write((byte)4); w.Write((ushort)((len + BLOCK - 1) / BLOCK));
            w.Flush();
            return Riff(f.ToArray(), d, off, len);
        }

        // ------------------------------------------------------------------ text + export
        public static string CodecName(int c)
        {
            switch (c)
            {
                case OPUS: return "Opus";
                case XMA2: return "XMA2";
                case PCM16: return "PCM16";
                case F32: return "float32 (impulse response)";
                case -1: return "-";
                default: return string.Format("codec {0:x2}", c);
            }
        }

        public static string ChunkName(int t)
        {
            switch (t)
            {
                case 0x55: return "data";
                case 0x76: return "format";
                case 0xA3: return "seek";
                case 0x36: return "peak";
                case 0x48: return "streamformat";
                case 0xBD: return "markers";
                case 0x68: return "midi";
                default: return t.ToString("x2");
            }
        }

        /// <summary>Sound name for a 29-bit stream id, when CodeWalker's hash index knows a string for it.</summary>
        public static string IdName(uint id)
        {
            for (uint k = 0; k < 8; k++)
            {
                var s = Rpf9Strings.Get(id | (k << 29));     // loads strings_gta6.txt (sound names) on first use
                if (s != null) return s;
            }
            return null;
        }

        static string Fmt(uint id)
        {
            var n = IdName(id);
            return n != null ? string.Format("{0:x7} ({1})", id, n) : id.ToString("x7");
        }

        public static string Summary(byte[] d, string name)
        {
            var sb = new StringBuilder();
            var c = Parse(d);
            sb.AppendFormat("{0}: GTA VI audio wave container (ADAT)\r\n", name);
            sb.AppendFormat("version {0}, flags {1:x4}, {2} streams, data at {3:x}, {4:n0} bytes\r\n", c.Version, c.Flags, c.Streams.Count, c.DataOffset, d.Length);
            var streamed = new Dictionary<uint, StreamedChannel>();
            foreach (var s in c.Streams)
            {
                var sf = s.Get(0x48); var dt = s.Get(0x55);
                if (sf == null || dt == null) continue;
                try
                {
                    int nb = (int)U32(d, sf.Offset), bs = (int)U32(d, sf.Offset + 4);
                    var ch = StreamChannels(d, sf, dt);
                    sb.AppendFormat("streamed: stream {0} holds {1} blocks of {2:x} bytes, interleaving {3} sound(s)\r\n", Fmt(s.Id), nb, bs, ch.Count);
                    foreach (var x in ch) streamed[x.Id] = x;
                }
                catch (Exception ex) { sb.AppendFormat("streamed: stream {0}: {1}\r\n", Fmt(s.Id), ex.Message); }
            }
            var codecs = c.Streams.Where(s => s.Codec >= 0).GroupBy(s => s.Codec).Select(g => CodecName(g.Key) + " x" + g.Count());
            if (codecs.Any()) sb.AppendLine("codecs: " + string.Join(", ", codecs));
            sb.AppendLine();
            sb.AppendLine("stream id          codec   rate  ch     samples   seconds  chunks");
            foreach (var s in c.Streams)
            {
                int codec = s.Codec, rate = s.Rate, chn = s.Channels; long frames = s.Samples;
                StreamedChannel sc;
                if (s.Get(0x76) == null && streamed.TryGetValue(s.Id, out sc))
                {
                    codec = sc.Codec; rate = sc.Rate; chn = sc.PacketChannels; frames = sc.Samples / chn;
                }
                else if (codec == OPUS && chn > 0) frames = s.Samples / chn;
                string secs = frames > 0 && rate > 0 ? ((double)frames / rate).ToString("0.00") : "";
                var chunks = string.Join(" ", s.Chunks.Select(x => ChunkName(x.Type) + ":" + x.Size));
                string id = Fmt(s.Id);
                sb.AppendFormat("{0,-18} {1,-6} {2,6} {3,3} {4,11} {5,9}  {6}\r\n", id, codec >= 0 ? CodecName(codec).Split(' ')[0] : "-",
                    rate > 0 ? rate.ToString() : "", chn > 0 ? chn.ToString() : "", frames > 0 ? frames.ToString() : "", secs, chunks);
            }
            sb.AppendLine();
            sb.AppendLine("Right-click > Export XML writes every sound to a folder: Opus as lossless .opus (Ogg), XMA2 as .xma,");
            sb.AppendLine("PCM16 as .wav, float32 as .f32, other chunks raw (<id>.<chunk type>.bin).");
            var slow = c.Streams.Where(s => s.Codec == OPUS && s.Rate > 0 && s.Rate != 48000).Select(s => s.Rate).Distinct().ToList();
            if (slow.Count > 0)
                sb.AppendFormat("Opus at {0} Hz: the game encodes it as if it were 48 kHz, so those .opus files play too fast;\r\n" +
                    "fix with e.g. ffmpeg -i x.opus -af asetrate={0} x.wav\r\n", string.Join("/", slow));
            return sb.ToString();
        }

        /// <summary>Write every sound of the container into dir: &lt;id&gt;.opus/.xma/.wav/.f32 (+ unused chunks as
        /// &lt;id&gt;.&lt;type&gt;.bin, .mid for MIDI). Returns a per-sound index (tab separated).</summary>
        public static string Export(byte[] d, string dir)
        {
            var c = Parse(d);
            Directory.CreateDirectory(dir);
            var idx = new StringBuilder("stream_id\tname\tcodec\trate\tchannels\tsamples\tseconds\tfile\r\n");
            var streamed = new Dictionary<uint, StreamedChannel>();
            var master = new HashSet<uint>();
            var errors = new StringBuilder();
            foreach (var s in c.Streams)
            {
                var sf = s.Get(0x48); var dt = s.Get(0x55);
                if (sf == null || dt == null) continue;
                try { foreach (var x in StreamChannels(d, sf, dt)) streamed[x.Id] = x; master.Add(s.Id); }
                catch (Exception ex) { errors.AppendFormat("{0:x7}: {1}\r\n", s.Id, ex.Message); }
            }
            foreach (var s in c.Streams)
            {
                string stem = Path.Combine(dir, s.Id.ToString("x7")), ok = null;
                int codec = s.Codec, rate = s.Rate, chn = s.Channels; long frames = s.Samples;
                if (master.Contains(s.Id))
                {
                    foreach (var x in s.Chunks)
                        if (x.Type != 0x48 && x.Type != 0x55 && x.Type != 0xA3) WriteChunk(d, x, stem + "." + x.Type.ToString("x2") + ".bin");
                    continue;
                }
                var dt = s.Get(0x55);
                StreamedChannel sc;
                try
                {
                    if (s.Get(0x76) == null && streamed.TryGetValue(s.Id, out sc))
                    {
                        codec = sc.Codec; rate = sc.Rate; chn = sc.PacketChannels; frames = sc.Samples / chn;
                        File.WriteAllBytes(stem + ".opus", OggOpus(sc.Packets, chn, rate > 0 ? rate : 48000, frames, s.Id));
                        ok = ".opus";
                    }
                    else if (codec == OPUS && dt != null)
                    {
                        frames = s.Samples / Math.Max(1, chn);
                        uint[] seek = null;
                        var sk = s.Get(0xA3);
                        if (sk != null) { seek = new uint[sk.Size / 4]; for (int i = 0; i < seek.Length; i++) seek[i] = U32(d, sk.Offset + 4 * i); }
                        File.WriteAllBytes(stem + ".opus", OggOpus(OpusPackets(d, dt, seek, chn), Math.Max(1, chn), rate > 0 ? rate : 48000, frames, s.Id));
                        ok = ".opus";
                    }
                    else if (codec == XMA2 && dt != null && s.Samples > 0 && rate > 0)
                    {
                        File.WriteAllBytes(stem + ".xma", Xma(d, dt.Offset, dt.Size, Math.Max(1, chn), rate, s.Samples));
                        ok = ".xma";
                    }
                    else if (codec == PCM16 && dt != null && rate > 0)
                    {
                        int n = s.Samples > 0 ? (int)Math.Min(dt.Size, 2L * Math.Max(1, chn) * s.Samples) : dt.Size;
                        File.WriteAllBytes(stem + ".wav", Wav(d, dt.Offset, n, Math.Max(1, chn), rate));
                        ok = ".wav";
                    }
                    else if (codec == F32 && dt != null)
                    {
                        WriteChunk(d, dt, stem + ".f32");
                        ok = ".f32";
                    }
                }
                catch (Exception ex) { errors.AppendFormat("{0:x7}: {1}\r\n", s.Id, ex.Message); ok = null; }
                foreach (var x in s.Chunks)
                {
                    if (ok != null && (x.Type == 0x55 || x.Type == 0x76 || x.Type == 0xA3 || x.Type == 0x36)) continue;
                    bool midi = x.Type == 0x68 && x.Size >= 4 && d[x.Offset] == 'M' && d[x.Offset + 1] == 'T' && d[x.Offset + 2] == 'h' && d[x.Offset + 3] == 'd';
                    WriteChunk(d, x, stem + (midi ? ".mid" : "." + x.Type.ToString("x2") + ".bin"));
                }
                idx.AppendFormat("{0:x7}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\r\n", s.Id, IdName(s.Id) ?? "", codec >= 0 ? CodecName(codec) : "", rate > 0 ? rate.ToString() : "",
                    chn > 0 ? chn.ToString() : "", frames > 0 ? frames.ToString() : "", frames > 0 && rate > 0 ? ((double)frames / rate).ToString("0.00") : "",
                    ok != null ? s.Id.ToString("x7") + ok : "(chunks only)");
            }
            if (errors.Length > 0) idx.Append("\r\nerrors:\r\n").Append(errors);
            return idx.ToString();
        }

        // ------------------------------------------------------------------ playback
        /// <summary>One sound of a container, for a player list.</summary>
        public class Sound
        {
            public uint Id;
            public string Name;
            public int Codec = -1, Rate, Channels;
            public long Frames;
            public int Bytes;
            internal AwcStream Stream;
            internal List<byte[]> Packets;     // streamed banks: already split out of the blocks
            public double Seconds => Frames > 0 && Rate > 0 ? (double)Frames / Rate : 0;
            public bool Playable => Codec == OPUS || Codec == PCM16;
            public string CodecText => Codec >= 0 ? CodecName(Codec).Split(' ')[0] : "-";
        }

        /// <summary>The sounds of a container (streamed banks: one per channel of the stream format; streams with
        /// no data, e.g. markers only, are left out).</summary>
        public static List<Sound> Sounds(byte[] d)
        {
            var c = Parse(d);
            var streamed = new Dictionary<uint, StreamedChannel>();
            var master = new HashSet<uint>();
            foreach (var s in c.Streams)
            {
                var sf = s.Get(0x48); var dt = s.Get(0x55);
                if (sf == null || dt == null) continue;
                foreach (var x in StreamChannels(d, sf, dt)) streamed[x.Id] = x;
                master.Add(s.Id);
            }
            var o = new List<Sound>();
            foreach (var s in c.Streams)
            {
                if (master.Contains(s.Id)) continue;
                var snd = new Sound { Id = s.Id, Name = IdName(s.Id) ?? s.Id.ToString("x7"), Stream = s };
                StreamedChannel sc;
                if (s.Get(0x76) == null && streamed.TryGetValue(s.Id, out sc))
                {
                    snd.Codec = sc.Codec; snd.Rate = sc.Rate > 0 ? sc.Rate : 48000; snd.Channels = sc.PacketChannels;
                    snd.Frames = sc.Samples / sc.PacketChannels; snd.Packets = sc.Packets; snd.Bytes = (int)sc.Bytes;
                }
                else
                {
                    var dt = s.Get(0x55);
                    if (dt == null) continue;
                    snd.Codec = s.Codec; snd.Rate = s.Rate; snd.Channels = Math.Max(1, s.Channels); snd.Bytes = dt.Size;
                    snd.Frames = s.Codec == OPUS ? s.Samples / snd.Channels : s.Samples;
                }
                o.Add(snd);
            }
            return o;
        }

        /// <summary>Decode a sound to a 16-bit PCM .wav (Opus through Concentus). The WAV is labelled with the
        /// stream's own rate: 32 kHz sounds were encoded as if they were 48 kHz, so this plays them at the right
        /// speed.</summary>
        public static byte[] DecodeWav(byte[] d, Sound s)
        {
            int ch = Math.Max(1, s.Channels), rate = s.Rate > 0 ? s.Rate : 48000;
            var pcm = new MemoryStream();
            foreach (var b in DecodePcm(d, s)) pcm.Write(b, 0, b.Length);
            return Wav(pcm.GetBuffer(), 0, (int)pcm.Length, ch, rate);
        }

        /// <summary>A sound's 16-bit interleaved PCM in pieces of about a quarter second, decoded as they are asked
        /// for (lets a player start before a long Opus track is fully decoded).</summary>
        public static IEnumerable<byte[]> DecodePcm(byte[] d, Sound s)
        {
            var st = s.Stream;
            var dt = st?.Get(0x55);
            int ch = Math.Max(1, s.Channels), rate = s.Rate > 0 ? s.Rate : 48000;
            int piece = Math.Max(4096, rate * ch * 2 / 4) & ~3;
            if (s.Codec == PCM16 && dt != null)
            {
                int n = s.Frames > 0 ? (int)Math.Min(dt.Size, 2L * ch * s.Frames) : dt.Size;
                for (int q = 0; q < n; q += piece)
                {
                    var b = new byte[Math.Min(piece, n - q)];
                    Buffer.BlockCopy(d, dt.Offset + q, b, 0, b.Length);
                    yield return b;
                }
                yield break;
            }
            if (s.Codec != OPUS) throw new NotSupportedException(CodecName(s.Codec) + " can't be played here (use Export XML)");
            var packets = s.Packets;
            if (packets == null)
            {
                uint[] seek = null;
                var sk = st.Get(0xA3);
                if (sk != null) { seek = new uint[sk.Size / 4]; for (int i = 0; i < seek.Length; i++) seek[i] = U32(d, sk.Offset + 4 * i); }
                packets = OpusPackets(d, dt, seek, ch);
            }
            var dec = Concentus.Structs.OpusDecoder.Create(48000, ch);
            var buf = new short[5760 * ch];
            long want = s.Frames > 0 ? s.Frames : long.MaxValue, got = 0, skip = PRESKIP;
            var pcm = new MemoryStream();
            foreach (var p in packets)
            {
                if (got >= want) break;
                int n;
                try { n = dec.Decode(p, 0, p.Length, buf, 0, 5760, false); }
                catch (Exception) { n = Math.Min(5760, OpusSamples(p)); Array.Clear(buf, 0, n * ch); }   // damaged packet: silence
                int from = (int)Math.Min(skip, n);
                skip -= from;
                int take = (int)Math.Min(n - from, want - got);
                if (take > 0)
                {
                    var bytes = new byte[take * ch * 2];
                    Buffer.BlockCopy(buf, from * ch * 2, bytes, 0, bytes.Length);
                    pcm.Write(bytes, 0, bytes.Length);
                }
                got += Math.Max(0, take);
                if (pcm.Length >= piece) { yield return pcm.ToArray(); pcm.SetLength(0); }
            }
            if (pcm.Length > 0) yield return pcm.ToArray();
        }

        static void WriteChunk(byte[] d, Chunk x, string path)
        {
            using (var f = File.Create(path)) f.Write(d, x.Offset, x.Size);
        }
    }
}
