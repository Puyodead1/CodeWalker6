using SharpDX;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI (RPF9 gen9 Xbox) clip dictionaries (.zcd, anim.rpf): clips, animations and the keyframe codec.
    /// Port of tools/rsc_anim.py; see that file for the layout notes. <see cref="ToYcd"/> turns a decoded
    /// dictionary into CodeWalker clip objects bound to a skeleton so the model viewer can play them.
    /// </summary>
    public static class Rpf9Anim
    {
        const ulong P_CPU = 0x50000000;
        public const uint TrackBoneRotation = 0x98e1750c, TrackBoneTranslation = 0xf791a95f, TrackBoneScale = 0x7aaa9f60;

        public class BadDataException : Exception { public BadDataException(string m) : base(m) { } }

        public class Track
        {
            public uint Id;           // joaat(bone name), 0 for non-bone tracks
            public uint Type;         // track type hash (TrackBone*)
            public uint Format;       // 0 quaternion, 2 vector3, 4 float
            public int Components;
            public float[] Values;    // frames x Components
            public override string ToString() => Id.ToString("x8") + " " + Type.ToString("x8") + " fmt " + Format;
        }

        public class Anim
        {
            public long Offset;
            public string Name;
            public int Frames;        // total decoded frames
            public int Fps;
            public float Duration;
            public List<Track> Tracks = new List<Track>();
            public string Error;      // decode failure (Tracks empty)
            public override string ToString() => Name;
        }

        public class AnimRef { public Anim Anim; public float Start, End, Rate; }

        public class Clip
        {
            public string Name;       // "pack:/x.clip"
            public uint Type;
            public float Duration;
            public List<AnimRef> Anims = new List<AnimRef>();
            public override string ToString() => Name;
        }

        public class Dict
        {
            public string Name;
            public List<Clip> Clips = new List<Clip>();
            public List<Anim> Anims = new List<Anim>();
            public int Decoded, Failed;
        }

        // ------------------------------------------------------------------ resource helpers

        class Res
        {
            public byte[] C;
            public uint U32(long o) => BitConverter.ToUInt32(C, (int)o);
            public ushort U16(long o) => BitConverter.ToUInt16(C, (int)o);
            public float F32(long o) => BitConverter.ToSingle(C, (int)o);
            public long Ptr(long o)
            {
                if (o < 0 || o + 8 > C.Length) return -1;
                ulong v = BitConverter.ToUInt64(C, (int)o);
                return (v >= P_CPU && v < P_CPU + (ulong)C.Length) ? (long)(v - P_CPU) : -1;
            }
            public string CStr(long o)
            {
                long p = Ptr(o);
                if (p < 0) return null;
                int e = (int)p, end = (int)Math.Min(C.Length, p + 1024);
                while (e < end && C[e] != 0) e++;
                if (e >= end || e == p) return null;
                return Encoding.GetEncoding(28591).GetString(C, (int)p, e - (int)p);
            }
            /// <summary>atMap: (key, value ptr) of every entry</summary>
            public List<KeyValuePair<uint, long>> Map(long o)
            {
                var res = new List<KeyValuePair<uint, long>>();
                long b = Ptr(o);
                if (b < 0 || o + 12 > C.Length) return res;
                uint nb = U32(o + 8);
                if (nb > 1 << 16) return res;
                for (int i = 0; i < nb; i++)
                {
                    long e = Ptr(b + 8 * i);
                    int guard = 0;
                    while (e >= 0 && e + 0x18 <= C.Length && guard++ < 1 << 16)
                    {
                        res.Add(new KeyValuePair<uint, long>(U32(e), Ptr(e + 8)));
                        e = Ptr(e + 0x10);
                    }
                }
                return res;
            }
        }

        static void Bad(string m) { throw new BadDataException(m); }

        /// <summary>CPU part of a resource (data = CPU then GPU part).</summary>
        static byte[] Cpu(byte[] data, long virtualSize)
        {
            if (virtualSize <= 0 || virtualSize >= data.Length) return data;
            var c = new byte[virtualSize];
            Buffer.BlockCopy(data, 0, c, 0, c.Length);
            return c;
        }

        public static bool IsClipDictionary(byte[] data, long virtualSize)
        {
            var r = new Res { C = Cpu(data, virtualSize) };
            return IsClipDictionary(r);
        }

        static bool IsClipDictionary(Res r)
        {
            var c = r.C;
            if (c.Length < 0xb0) return false;
            for (int i = 0; i < 8; i++) if (c[i] != 0) return false;
            var n = r.CStr(0x38);
            return !string.IsNullOrEmpty(n) && r.U16(0x40) == n.Length && r.Ptr(0x48) >= 0 && r.U32(0x50) < 1 << 16;
        }

        // ------------------------------------------------------------------ dictionary

        /// <summary>Read a .zcd (data = CPU part then GPU part, virtualSize = Rpf9FileEntry.VirtualSize) and decode every animation.</summary>
        public static Dict Read(byte[] data, long virtualSize)
        {
            var r = new Res { C = Cpu(data, virtualSize) };
            if (!IsClipDictionary(r)) Bad("not a clip dictionary");
            var d = new Dict { Name = r.CStr(0x38) };
            var anims = new Dictionary<long, Anim>();
            Anim GetAnim(long a)
            {
                if (a < 0 || a + 0x70 > r.C.Length) return null;
                if (anims.TryGetValue(a, out var an)) return an;
                an = new Anim { Offset = a, Name = r.CStr(a + 0x30), Duration = r.F32(a + 0x28) };
                try { DecodeAnimation(r, a, an); d.Decoded++; }
                catch (Exception ex) when (ex is BadDataException || ex is ArgumentException || ex is IndexOutOfRangeException)
                {
                    an.Tracks.Clear(); an.Error = ex.Message; d.Failed++;
                }
                anims[a] = an;
                d.Anims.Add(an);
                return an;
            }
            // animation dictionaries first, so anims nobody's clip uses are listed too
            long ad = r.Ptr(0x70);
            int nad = ad >= 0 ? r.U16(0x78) : 0;
            for (int i = 0; i < nad; i++)
            {
                long a = r.Ptr(ad + 0x10 * i);
                if (a < 0) continue;
                foreach (var kv in r.Map(a + 0x48)) GetAnim(kv.Value);
            }
            foreach (var kv in r.Map(0x48))
            {
                long ca = kv.Value;
                if (ca < 0 || ca + 0x80 > r.C.Length) continue;
                var cl = new Clip { Name = r.CStr(ca + 0x28), Type = r.U32(ca + 0x10), Duration = r.F32(ca + 0x78) };
                long arr = r.Ptr(ca + 0x68);
                int n = arr >= 0 ? r.U16(ca + 0x70) : 0;
                for (int i = 0; i < n; i++)
                {
                    long e = r.Ptr(arr + 8 * i);
                    if (e < 0 || e + 0x34 > r.C.Length) continue;
                    var an = GetAnim(r.Ptr(e));
                    if (an == null) continue;
                    cl.Anims.Add(new AnimRef { Anim = an, Start = r.F32(e + 0x28), End = r.F32(e + 0x2c), Rate = r.F32(e + 0x30) });
                }
                d.Clips.Add(cl);
            }
            d.Clips.Sort((x, y) => string.CompareOrdinal(x.Name ?? "", y.Name ?? ""));
            d.Anims.Sort((x, y) => string.CompareOrdinal(x.Name ?? "", y.Name ?? ""));
            return d;
        }

        // ------------------------------------------------------------------ keyframe codec

        /// <summary>w-bit LSB-first field at bit position pos of buf[start..end) (w &lt;= 57)</summary>
        static long Field(byte[] buf, long start, long end, long pos, int w)
        {
            if (w == 0) return 0;
            long b = start + (pos >> 3);
            int sh = (int)(pos & 7);
            ulong v = 0;
            int nbytes = (sh + w + 7) >> 3;
            for (int i = 0; i < nbytes; i++)
            {
                long o = b + i;
                if (o < end) v |= (ulong)buf[o] << (8 * i);
            }
            return (long)((v >> sh) & ((w >= 64) ? ulong.MaxValue : ((1UL << w) - 1)));
        }

        static long ZigZag(long v) => (v >> 1) ^ -(v & 1);

        /// <summary>type-10 curve bitstream (data = c[start..end), after the 16-byte header) -> numf integers</summary>
        static long[] Curve10(int numf, uint cn, byte[] c, long start, long end)
        {
            int b0 = (int)(cn & 255), b1 = (int)((cn >> 8) & 255);
            long nbits = (end - start) * 8;
            int K = (numf + 63) / 64;
            var offs = new long[K];
            var starts = new long[K];
            for (int k = 0; k < K; k++) offs[k] = Field(c, start, end, (long)k * b0, b0);
            for (int k = 0; k < K; k++) starts[k] = Field(c, start, end, (long)K * b0 + (long)k * b1, b1);
            var nfr = new int[K];
            var rows = new int[K];
            int R = 0;
            for (int k = 0; k < K; k++) { nfr[k] = Math.Min(64, numf - 64 * k); rows[k] = (nfr[k] + 6) / 8; R += rows[k]; }
            long p = (long)K * (b0 + b1);
            var widths = new int[R];
            long q = p, prev = -1;
            int found = 0;
            while (found < R && q < nbits)
            {
                if (((c[start + (q >> 3)] >> (int)(q & 7)) & 1) != 0)
                {
                    long one = q - p;
                    widths[found++] = (int)(one - prev - 1);
                    prev = one;
                }
                q++;
            }
            if (found < R) Bad("curve header truncated");
            long hdr = p + (R > 0 ? prev + 1 : 0);
            var pos = new long[R + 1];
            for (int i = 0; i < R; i++) pos[i + 1] = pos[i] + widths[i];
            var rs = new int[K + 1];
            for (int k = 0; k < K; k++) rs[k + 1] = rs[k] + rows[k];
            for (int k = 0; k < K; k++) if (offs[k] != pos[rs[k]]) Bad("curve chunk offsets");
            long L = (pos[R] + 31) / 32;
            long hw = (end - start) / 4 - 8 * L;
            if (hw < 0 || hw * 32 < hdr) Bad("curve size mismatch");
            var outv = new long[numf];
            for (int f = 0; f < numf; f++) outv[f] = starts[f / 64];
            int mw = 0;
            foreach (var w in widths) mw = Math.Max(mw, w);
            if (mw == 0) return outv;
            if (mw > 57) Bad("curve delta width " + mw);
            // lane j = u32 words hw*4 + (i*8 + j)*4 for i in 0..L-1, bits LSB-first, back to back
            long laneBase = start + 4 * hw;
            long laneBits = 32 * L;
            long LaneField(int j, long bp, int w)
            {
                long v = 0;
                for (int b = 0; b < w; b++)
                {
                    long bi = Math.Min(bp + b, laneBits - 1);
                    long word = laneBase + 4 * ((bi >> 5) * 8 + j);
                    int bit = (int)(bi & 31);
                    if (((c[word + (bit >> 3)] >> (bit & 7)) & 1) != 0) v |= 1L << b;
                }
                return v;
            }
            var D = new long[64];
            for (int k = 0; k < K; k++)
            {
                Array.Clear(D, 0, 64);
                for (int r = rs[k]; r < rs[k + 1]; r++)
                {
                    int w = widths[r];
                    if (w == 0) continue;
                    for (int j = 0; j < 8; j++)
                    {
                        int f = 8 * (r - rs[k]) + j;
                        if (f >= 64) break;
                        D[f] = ZigZag(LaneField(j, pos[r], w));
                    }
                }
                for (int f = Math.Max(nfr[k] - 1, 0); f < 64; f++) D[f] = 0;
                // value[0] = start, value[f+1] = value[f] + sum(delta[0..f])
                long acc = 0, val = 0;
                int baseF = 64 * k;
                for (int f = 1; f < nfr[k]; f++)
                {
                    acc += D[f - 1];
                    val += acc;
                    outv[baseF + f] = starts[k] + val;
                }
            }
            return outv;
        }

        class BlockTracks
        {
            public int NumFrames;
            public Dictionary<int, double[][]> Tracks = new Dictionary<int, double[][]>(); // track -> [component][frame]
        }

        static BlockTracks DecodeBlock(Res r, long blk)
        {
            var c = r.C;
            if (blk < 0 || blk + 0x20 > c.Length) Bad("block");
            int numf = (int)(r.U32(blk + 0x14) >> 16);
            int stride = (int)(r.U32(blk + 0x18) & 0xffff);
            long bas = blk + 0x20;
            long fo = bas + r.U32(blk + 0x0c);
            long tail = fo + (long)stride * numf;
            if (tail + 22 > c.Length || numf <= 0) Bad("block size");
            var counts = new int[11];
            for (int t = 0; t < 11; t++) counts[t] = r.U16(tail + 2 * t);
            long p = tail + 22;
            var ent = new int[11][];
            for (int t = 0; t < 11; t++)
            {
                int n = counts[t];
                if (p + 2 * n > c.Length) Bad("channel list");
                ent[t] = new int[n];
                for (int i = 0; i < n; i++) ent[t][i] = r.U16(p + 2 * i);
                p += 2 * (n + ((4 - n % 4) % 4));
            }
            if (counts[6] != 0) Bad("channel type 6");
            if (counts[9] != 0) Bad("channel type 9");

            var comps = new Dictionary<int, double[]>(); // (track<<2 | comp) -> frames
            void Put(int e, double[] v) { comps[e] = v; }
            double[] Const(double v) { var a = new double[numf]; for (int i = 0; i < numf; i++) a[i] = v; return a; }

            long o = bas;
            var staticQ = new List<KeyValuePair<int, double[]>>();
            var staticV = new List<KeyValuePair<int, double[]>>();
            foreach (var e in ent[0]) { staticQ.Add(new KeyValuePair<int, double[]>(e >> 2, new double[] { r.F32(o), r.F32(o + 4), r.F32(o + 8) })); o += 12; }
            foreach (var e in ent[1]) { staticV.Add(new KeyValuePair<int, double[]>(e >> 2, new double[] { r.F32(o), r.F32(o + 4), r.F32(o + 8) })); o += 12; }
            foreach (var e in ent[2]) { Put(e, Const(r.F32(o))); o += 4; }
            var quant = new List<Tuple<int, int, double, double>>();
            foreach (var e in ent[4]) { quant.Add(Tuple.Create(e, (int)r.U32(o), (double)r.F32(o + 4), (double)r.F32(o + 8))); o += 12; }
            var ind = new List<Tuple<int, int, double[]>>();
            foreach (var e in ent[5])
            {
                int fb = (int)r.U32(o), vb = (int)r.U32(o + 4);
                long ni = r.U32(o + 8);
                double qq = r.F32(o + 12), of = r.F32(o + 16);
                if (o + 20 + 4 * ni > c.Length || fb > 32 || vb > 32) Bad("indirect channel");
                long nv = vb != 0 ? Math.Min(ni * 32 / vb, (1L << fb) - 1) : 0;
                double[] table;
                if (nv > 0)
                {
                    table = new double[nv];
                    for (long i = 0; i < nv; i++) table[i] = Field(c, o + 20, o + 20 + 4 * ni, vb * i, vb) * qq + of;
                }
                else table = new double[1];
                ind.Add(Tuple.Create(e, fb, table));
                o += 20 + 4 * ni;
            }
            foreach (var e in ent[10])
            {
                long ni = r.U32(o);
                uint cn = r.U32(o + 4);
                double qq = r.F32(o + 8), of = r.F32(o + 12);
                if (ni < 4 || o + 4 * ni > c.Length) Bad("curve channel");
                var iv = Curve10(numf, cn, c, o + 16, o + 4 * ni);
                var v = new double[numf];
                for (int i = 0; i < numf; i++) v[i] = iv[i] * qq + of;
                Put(e, v);
                o += 4 * ni;
            }
            if (o != fo) Bad("channel data size " + (o - bas).ToString("x") + " != " + (fo - bas).ToString("x"));
            if (quant.Count > 0 || ind.Count > 0 || ent[3].Length > 0)
            {
                var raw = new List<double[]>();
                foreach (var e in ent[3]) { var a = new double[numf]; raw.Add(a); Put(e, a); }
                var qv = new List<double[]>();
                foreach (var t in quant) { var a = new double[numf]; qv.Add(a); Put(t.Item1, a); }
                var iv = new List<double[]>();
                foreach (var t in ind) { var a = new double[numf]; iv.Add(a); Put(t.Item1, a); }
                for (int f = 0; f < numf; f++)
                {
                    long rs = fo + (long)f * stride, re = rs + stride;
                    long pos = 0;
                    for (int i = 0; i < raw.Count; i++)
                    {
                        raw[i][f] = BitConverter.ToSingle(BitConverter.GetBytes((uint)Field(c, rs, re, pos, 32)), 0);
                        pos += 32;
                    }
                    for (int i = 0; i < quant.Count; i++)
                    {
                        int w = quant[i].Item2;
                        if (w > 57) Bad("quantize width " + w);
                        qv[i][f] = Field(c, rs, re, pos, w) * quant[i].Item3 + quant[i].Item4;
                        pos += w;
                    }
                    for (int i = 0; i < ind.Count; i++)
                    {
                        int w = ind[i].Item2;
                        var table = ind[i].Item3;
                        iv[i][f] = table[Math.Min(Field(c, rs, re, pos, w), table.Length - 1)];
                        pos += w;
                    }
                }
            }

            var res = new BlockTracks { NumFrames = numf };
            foreach (var kv in staticQ)
            {
                var v = kv.Value;
                double w = Math.Sqrt(Math.Max(0.0, 1 - v[0] * v[0] - v[1] * v[1] - v[2] * v[2]));
                res.Tracks[kv.Key] = new[] { Const(v[0]), Const(v[1]), Const(v[2]), Const(w) };
            }
            foreach (var kv in staticV)
                res.Tracks[kv.Key] = new[] { Const(kv.Value[0]), Const(kv.Value[1]), Const(kv.Value[2]) };
            double[] Pop(int key)
            {
                if (comps.TryGetValue(key, out var v)) { comps.Remove(key); return v; }
                return new double[numf];
            }
            foreach (var e in ent[7])
            {
                int t = e >> 2, k = e & 3;
                var s = new List<double[]> { Pop(t << 2), Pop((t << 2) | 1), Pop((t << 2) | 2) };
                var rec = new double[numf];
                for (int i = 0; i < numf; i++) rec[i] = Math.Sqrt(Math.Max(0, 1 - s[0][i] * s[0][i] - s[1][i] * s[1][i] - s[2][i] * s[2][i]));
                s.Insert(Math.Min(k, 3), rec);
                res.Tracks[t] = s.ToArray();
            }
            foreach (var e in ent[8])
            {
                int t = e >> 2;
                var qv = new[] { Pop(t << 2), Pop((t << 2) | 1), Pop((t << 2) | 2), Pop((t << 2) | 3) };
                for (int i = 0; i < numf; i++)
                {
                    double l = Math.Sqrt(qv[0][i] * qv[0][i] + qv[1][i] * qv[1][i] + qv[2][i] * qv[2][i] + qv[3][i] * qv[3][i]);
                    l = Math.Max(l, 1e-12);
                    for (int j = 0; j < 4; j++) qv[j][i] /= l;
                }
                res.Tracks[t] = qv;
            }
            foreach (var g in comps.GroupBy(kv => kv.Key >> 2))
            {
                int ncomp = g.Max(kv => kv.Key & 3) + 1;
                var arr = new double[ncomp][];
                for (int j = 0; j < ncomp; j++) arr[j] = new double[numf];
                foreach (var kv in g) arr[kv.Key & 3] = kv.Value;
                res.Tracks[g.Key] = arr;
            }
            return res;
        }

        static void DecodeAnimation(Res r, long a, Anim an)
        {
            int nt = r.U16(a + 0x68);
            long td = r.Ptr(a + 0x60);
            var desc = new List<uint[]>();
            if (td >= 0)
                for (int i = 0; i < nt; i++)
                {
                    if (td + 12 * i + 12 > r.C.Length) Bad("track descriptors");
                    desc.Add(new[] { r.U32(td + 12 * i), r.U32(td + 12 * i + 4), r.U32(td + 12 * i + 8) });
                }
            long br = r.Ptr(a + 0x50);
            int nb = br >= 0 ? r.U16(a + 0x58) : 0;
            var parts = new Dictionary<int, List<double[][]>>();
            var skip = new Dictionary<int, List<bool>>();
            int total = 0;
            for (int b = 0; b < nb; b++)
            {
                long blk = r.Ptr(br + 8 * b);
                if (blk < 0) Bad("null block");
                var bt = DecodeBlock(r, blk);
                foreach (var kv in bt.Tracks)
                {
                    if (!parts.TryGetValue(kv.Key, out var l)) { parts[kv.Key] = l = new List<double[][]>(); skip[kv.Key] = new List<bool>(); }
                    l.Add(kv.Value);
                    skip[kv.Key].Add(b > 0);
                }
                total += b == 0 ? bt.NumFrames : bt.NumFrames - 1;
            }
            int frames = r.U16(a + 0x24);
            float dur = r.F32(a + 0x28);
            an.Fps = (dur > 0 && frames > 1) ? (int)Math.Round((frames - 1) / dur, MidpointRounding.ToEven) : 30;
            an.Frames = total;
            an.Duration = dur;
            for (int t = 0; t < desc.Count; t++)
            {
                if (!parts.TryGetValue(t, out var l)) continue;
                var sk = skip[t];
                int ncomp = l.Max(x => x.Length);
                int len = 0;
                for (int i = 0; i < l.Count; i++) len += l[i][0].Length - (sk[i] ? 1 : 0);
                uint fm = desc[t][2];
                int outc = fm == 0 ? 4 : ncomp;
                var vals = new float[len * outc];
                int f0 = 0;
                for (int i = 0; i < l.Count; i++)
                {
                    var part = l[i];
                    int n = part[0].Length, s = sk[i] ? 1 : 0;
                    for (int f = s; f < n; f++)
                        for (int j = 0; j < outc; j++)
                            vals[(f0 + f - s) * outc + j] = j < part.Length ? (float)part[j][f] : 0f;
                    f0 += n - s;
                }
                an.Tracks.Add(new Track { Id = desc[t][0], Type = desc[t][1], Format = fm, Components = outc, Values = vals });
            }
        }

        // ------------------------------------------------------------------ CodeWalker objects

        /// <summary>
        /// CodeWalker clip dictionary for the model viewer. Bone tracks are bound to <paramref name="boneIndex"/>
        /// (joaat(bone name) -> Bone.Tag of the target skeleton); tracks of bones it lacks are dropped.
        /// matched/total count the bone tracks.
        /// </summary>
        public static YcdFile ToYcd(Dict d, string name, Dictionary<uint, ushort> boneIndex, out int matched, out int total)
        {
            matched = 0; total = 0;
            var ycd = new YcdFile { Name = name, Loaded = true };
            var animObjs = new Dictionary<Anim, Animation>();
            foreach (var an in d.Anims)
            {
                var ids = new List<AnimationBoneId>();
                var seqs = new List<AnimSequence>();
                foreach (var t in an.Tracks)
                {
                    byte track;
                    if (t.Type == TrackBoneRotation && t.Format == 0) track = 1;
                    else if (t.Type == TrackBoneTranslation && t.Format == 2) track = 0;
                    else if (t.Type == TrackBoneScale && t.Format == 2) track = 2;
                    else continue;
                    total++;
                    if (boneIndex == null || !boneIndex.TryGetValue(t.Id, out var tag)) continue;
                    matched++;
                    int nf = t.Values.Length / t.Components;
                    var chans = new AnimChannel[t.Components];
                    for (int j = 0; j < t.Components; j++)
                    {
                        var v = new float[nf];
                        for (int f = 0; f < nf; f++) v[f] = t.Values[f * t.Components + j];
                        chans[j] = new AnimChannelRawFloat { Values = v };
                    }
                    var id = new AnimationBoneId { BoneId = tag, Track = track };
                    ids.Add(id);
                    seqs.Add(new AnimSequence { Channels = chans, BoneId = id });
                }
                int frames = Math.Max(an.Frames, 1);
                var anim = new Animation
                {
                    Frames = (ushort)Math.Min(frames, ushort.MaxValue),
                    SequenceFrameLimit = (ushort)Math.Min(frames, ushort.MaxValue),
                    Duration = an.Duration > 0 ? an.Duration : Math.Max(frames - 1, 1) / (float)Math.Max(an.Fps, 1),
                    Hash = new MetaHash(JenkHash.GenHash(ShortName(an.Name) ?? "")),
                    Ycd = ycd,
                    BoneIds = new ResourceSimpleList64_s<AnimationBoneId> { data_items = ids.ToArray() },
                    Sequences = new ResourcePointerList64<Sequence>
                    {
                        data_items = new[] { new Sequence { NumFrames = (ushort)Math.Min(frames, ushort.MaxValue), Sequences = seqs.ToArray() } }
                    },
                };
                animObjs[an] = anim;
            }

            var entries = new List<ClipMapEntry>();
            var map = new Dictionary<MetaHash, ClipMapEntry>();
            foreach (var cl in d.Clips)
            {
                var refs = cl.Anims.Where(x => x.Anim != null && animObjs.ContainsKey(x.Anim)).ToList();
                if (refs.Count == 0) continue;
                ClipBase clip;
                float End(AnimRef x) => x.End > x.Start ? x.End : animObjs[x.Anim].Duration;
                if (refs.Count == 1)
                {
                    var x = refs[0];
                    clip = new ClipAnimation { Animation = animObjs[x.Anim], StartTime = x.Start, EndTime = End(x), Rate = x.Rate > 0 ? x.Rate : 1f };
                }
                else
                {
                    var list = new ResourceSimpleArray<ClipAnimationsEntry> { Data = new List<ClipAnimationsEntry>() };
                    foreach (var x in refs)
                        list.Data.Add(new ClipAnimationsEntry { Animation = animObjs[x.Anim], StartTime = x.Start, EndTime = End(x), Rate = x.Rate > 0 ? x.Rate : 1f });
                    float dur = cl.Duration > 0 ? cl.Duration : refs.Max(x => (End(x) - x.Start) / (x.Rate > 0 ? x.Rate : 1f));
                    clip = new ClipAnimationList { Animations = list, Duration = dur };
                }
                clip.Name = cl.Name ?? ("clip" + entries.Count);
                clip.Ycd = ycd;
                var h = new MetaHash(JenkHash.GenHash(clip.ShortName ?? clip.Name));
                if (map.ContainsKey(h)) continue;
                clip.Hash = h;
                var cme = new ClipMapEntry { Hash = h, Clip = clip };
                map[h] = cme;
                entries.Add(cme);
            }
            ycd.ClipMap = map;
            ycd.ClipMapEntries = entries.ToArray();
            ycd.AnimMap = new Dictionary<MetaHash, AnimationMapEntry>();
            return ycd;
        }

        static string ShortName(string n)
        {
            if (string.IsNullOrEmpty(n)) return n;
            n = n.Replace('\\', '/');
            int s = n.LastIndexOf('/');
            if (s >= 0 && s < n.Length - 1) n = n.Substring(s + 1);
            int d = n.IndexOf('.');
            return d > 0 ? n.Substring(0, d) : n;
        }

        // ------------------------------------------------------------------ .zad (external animation dictionaries)

        static readonly Dictionary<uint, string> TRACK_TYPES = new Dictionary<uint, string>
        {
            { 0x98e1750c, "boneRotation" }, { 0xf791a95f, "boneTranslation" }, { 0x7aaa9f60, "boneScale" },
            { 0x2deefadd, "genericControl" }, { 0x60d0644c, "firstPersonCameraWeight" }, { 0x6ddef121, "cameraTranslation" },
            { 0xf5c3a5b6, "genericTranslation" }, { 0x364d3bdb, "facialTranslation" }, { 0xd9ad1a74, "cameraRotation" },
            { 0x4a5a04f4, "cameraFieldOfView" }, { 0x40243fdf, "cameraFocusDistance" },
        };
        static readonly Dictionary<uint, string> FORMATS = new Dictionary<uint, string> { { 0, "quat" }, { 2, "vec3" }, { 3, "vec2" }, { 4, "float" } };

        /// <summary>A .zad: a stand-alone animation dictionary (same header as a .zcd, but the atMap at +0x48 holds
        /// animations, keyed by joaat(anim name); +0x68 u32 joaat(dictionary name)). Cutscene time chunks: the sibling
        /// &lt;stem&gt;.zcd lists it as animation dictionary ref N (= the -N suffix). See data/zad_format.md.</summary>
        public static Dict ReadAnimDictionary(byte[] data, long virtualSize)
        {
            var r = new Res { C = Cpu(data, virtualSize) };
            if (!IsClipDictionary(r)) Bad("not an animation dictionary");
            var d = new Dict { Name = r.CStr(0x38) };
            foreach (var kv in r.Map(0x48))
            {
                long a = kv.Value;
                if (a < 0 || a + 0x70 > r.C.Length) continue;
                var an = new Anim { Offset = a, Name = r.CStr(a + 0x30), Duration = r.F32(a + 0x28) };
                try { DecodeAnimation(r, a, an); d.Decoded++; }
                catch (Exception ex) when (ex is BadDataException || ex is ArgumentException || ex is IndexOutOfRangeException)
                {
                    an.Tracks.Clear(); an.Error = ex.Message; d.Failed++;
                }
                d.Anims.Add(an);
            }
            d.Anims.Sort((x, y) => string.CompareOrdinal(x.Name ?? "", y.Name ?? ""));
            return d;
        }

        /// <summary>Text summary of a .zad: per animation its frames, blocks, flags and tracks grouped by type.</summary>
        public static string AnimDictionarySummary(byte[] data, long virtualSize)
        {
            var r = new Res { C = Cpu(data, virtualSize) };
            var d = ReadAnimDictionary(data, virtualSize);
            var sb = new StringBuilder();
            sb.AppendLine("Animation dictionary (.zad): " + d.Name + $"  (hash {r.U32(0x68):x8})");
            sb.AppendLine($"{d.Anims.Count} animations ({d.Decoded} decoded, {d.Failed} failed)");
            sb.AppendLine("A cutscene time chunk: the sibling <name without -N>.zcd lists it as animation dictionary ref N and streams it in by time (data/zad_format.md).");
            sb.AppendLine();
            foreach (var an in d.Anims)
            {
                long a = an.Offset;
                uint flags = r.U32(a + 0x20);
                int nb = r.Ptr(a + 0x50) >= 0 ? r.U16(a + 0x58) : 0;
                sb.AppendLine($"{an.Name}");
                sb.AppendLine($"    {r.U16(a + 0x24)} frames @ {an.Fps} fps ({an.Duration:0.###} s), {nb} block(s) of <= {r.U16(a + 0x26) + 1} frames, largest block {r.U32(a + 0x48)} B");
                sb.AppendLine($"    flags {flags:x} (ref index {flags >> 16}{((flags & 0x10) != 0 ? ", root motion" : "")}), track set signature {r.U32(a + 0x2c):x8}");
                if (an.Error != null) { sb.AppendLine("    DECODE FAILED: " + an.Error); continue; }
                sb.AppendLine($"    tracks {an.Tracks.Count}, {an.Frames} decoded frames:");
                foreach (var g in an.Tracks.GroupBy(t => (t.Type, t.Format)).OrderByDescending(g => g.Count()))
                {
                    string tn = TRACK_TYPES.TryGetValue(g.Key.Type, out var s) ? s : g.Key.Type.ToString("x8");
                    string fn = FORMATS.TryGetValue(g.Key.Format, out var f) ? f : "fmt " + g.Key.Format;
                    var ids = g.Select(t => t.Id == 0 ? "-" : (Rpf9Strings.Get(t.Id) ?? t.Id.ToString("x8"))).Take(8);
                    sb.AppendLine($"      {tn}/{fn} x{g.Count()}: {string.Join(", ", ids)}{(g.Count() > 8 ? ", ..." : "")}");
                }
            }
            return sb.ToString();
        }

        /// <summary>Text summary for the file viewer: clips, their animations and decode status.</summary>
        public static string Summary(Dict d)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Clip dictionary: " + d.Name);
            sb.AppendLine($"{d.Clips.Count} clips, {d.Anims.Count} animations ({d.Decoded} decoded, {d.Failed} failed)");
            sb.AppendLine();
            foreach (var cl in d.Clips)
            {
                sb.AppendLine($"{cl.Name}  ({cl.Duration:0.###} s)");
                foreach (var x in cl.Anims)
                {
                    var an = x.Anim;
                    int bones = an.Tracks.Count(t => t.Type == TrackBoneRotation || t.Type == TrackBoneTranslation || t.Type == TrackBoneScale);
                    sb.AppendLine($"    {an.Name}  {x.Start:0.###}-{x.End:0.###} s x{x.Rate:0.##}, {an.Frames} frames @ {an.Fps} fps, {an.Tracks.Count} tracks ({bones} bone)"
                        + (an.Error != null ? "  DECODE FAILED: " + an.Error : ""));
                }
            }
            return sb.ToString();
        }
    }
}
