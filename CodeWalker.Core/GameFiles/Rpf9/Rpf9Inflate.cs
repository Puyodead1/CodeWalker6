using System;
using System.IO;

namespace CodeWalker.GameFiles
{
    /// <summary>Inflate output: in memory, or spilled to a stream keeping the last 32 KB as the window.</summary>
    internal class Rpf9Out
    {
        public byte[] Buf;
        public int Len;
        public long Total;
        public long Want = long.MaxValue;
        private readonly Stream Spill;

        public Rpf9Out(long capacity, Stream spill = null)
        {
            Spill = spill;
            Buf = new byte[spill != null ? 8 << 20 : (int)Math.Max(256, Math.Min(capacity, 0x7FFFFFC7))];
        }

        public bool Full => Total >= Want;

        public void Reset()
        {
            Len = 0;
            Total = 0;
            if (Spill != null) { Spill.SetLength(0); Spill.Position = 0; }
        }

        private void Ensure(int extra)
        {
            if (Len + extra <= Buf.Length) return;
            if (Spill != null)
            {
                int keep = Math.Min(Len, 0x8000);
                Spill.Write(Buf, 0, Len - keep);
                Buffer.BlockCopy(Buf, Len - keep, Buf, 0, keep);
                Len = keep;
                if (Len + extra <= Buf.Length) return;
            }
            long n = Math.Max((long)Buf.Length * 2, (long)Len + extra);
            if (n > 0x7FFFFFC7) n = 0x7FFFFFC7;
            if (n < Len + extra) throw new InvalidDataException("file too large to extract into memory");
            Array.Resize(ref Buf, (int)n);
        }

        public void Put(byte b)
        {
            if (Len == Buf.Length) Ensure(1);
            Buf[Len++] = b;
            Total++;
        }

        public void Copy(int dist, int len)
        {
            if (dist > Len || dist <= 0) throw new InvalidDataException("distance too far back");
            Ensure(len);
            int s = Len - dist;
            if (dist >= len) Buffer.BlockCopy(Buf, s, Buf, Len, len);
            else for (int i = 0; i < len; i++) Buf[Len + i] = Buf[s + i];
            Len += len;
            Total += len;
        }

        public void Write(byte[] src, int off, int n)
        {
            while (n > 0)
            {
                int k = Math.Min(n, 0x10000);
                Ensure(k);
                Buffer.BlockCopy(src, off, Buf, Len, k);
                Len += k;
                Total += k;
                off += k;
                n -= k;
            }
        }

        public void Flush()
        {
            if (Spill != null && Len > 0)
            {
                Spill.Write(Buf, 0, Len);
                Len = 0;
            }
        }

        public byte[] ToArray()
        {
            if (Spill != null) throw new InvalidOperationException();
            if (Len == Buf.Length) return Buf;
            var r = new byte[Len];
            Buffer.BlockCopy(Buf, 0, r, 0, Len);
            return r;
        }
    }

    /// <summary>
    /// Deflate decoder (RFC 1951, optional zlib wrapper) that knows exactly how much input it consumed, which the
    /// framework's DeflateStream can't tell: RPF9 files are consecutive zlib streams (one per 64 KB block).
    /// </summary>
    internal class Rpf9Inflater
    {
        private readonly Rpf9View V;
        private readonly Rpf9Out O;
        private long InBase;
        private byte[] Ib = new byte[0];
        private int IbPos;
        private ulong Bb;
        private int Bc;

        public Rpf9Inflater(Rpf9View v, long pos, Rpf9Out o)
        {
            V = v;
            InBase = pos;
            O = o;
        }

        /// <summary>position of the first unconsumed byte (valid after Run returned true)</summary>
        public long Pos => InBase + IbPos - (Bc >> 3);

        private int NextByte()
        {
            if (IbPos >= Ib.Length)
            {
                InBase += Ib.Length;
                Ib = V.Read(InBase, 0x10000);
                IbPos = 0;
                if (Ib.Length == 0) return -1;
            }
            return Ib[IbPos++];
        }

        private bool Fill(int n)
        {
            while (Bc < n)
            {
                int b = NextByte();
                if (b < 0) return false;
                Bb |= (ulong)b << Bc;
                Bc += 8;
            }
            return true;
        }

        private int Bits(int n)
        {
            if (n == 0) return 0;
            if (!Fill(n)) throw new EndOfStreamException();
            int v = (int)(Bb & ((1UL << n) - 1));
            Bb >>= n;
            Bc -= n;
            return v;
        }

        private class Huff
        {
            public readonly short[] Count = new short[16];
            public readonly short[] Sym = new short[320];
            public readonly ushort[] Fast = new ushort[512];

            public void Build(byte[] lens, int off, int n)
            {
                Array.Clear(Count, 0, 16);
                Array.Clear(Fast, 0, 512);
                for (int i = 0; i < n; i++) Count[lens[off + i]]++;
                var offs = new short[16];
                for (int l = 1; l < 15; l++) offs[l + 1] = (short)(offs[l] + Count[l]);
                for (int i = 0; i < n; i++) if (lens[off + i] != 0) Sym[offs[lens[off + i]]++] = (short)i;
                int code = 0, idx = 0;
                for (int l = 1; l <= 15; l++)
                {
                    for (int k = 0; k < Count[l]; k++, code++)
                    {
                        int s = Sym[idx++];
                        if (l > 9) continue;
                        int rev = 0;
                        for (int b = 0; b < l; b++) rev |= ((code >> b) & 1) << (l - 1 - b);
                        for (int j = rev; j < 512; j += 1 << l) Fast[j] = (ushort)(l << 12 | s);
                    }
                    code <<= 1;
                }
            }
        }

        private int Decode(Huff h)
        {
            Fill(15);
            int e = h.Fast[(int)(Bb & 511)];
            if (e != 0)
            {
                int l = e >> 12;
                if (l > Bc) throw new EndOfStreamException();
                Bb >>= l;
                Bc -= l;
                return e & 0xfff;
            }
            int code = 0, first = 0, index = 0;
            for (int len = 1; len <= 15; len++)
            {
                if (Bc < 1) throw new EndOfStreamException();
                code |= (int)(Bb & 1);
                Bb >>= 1;
                Bc--;
                int count = h.Count[len];
                if (code - count < first) return h.Sym[index + (code - first)];
                index += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }
            throw new InvalidDataException("bad huffman code");
        }

        static readonly short[] LBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        static readonly short[] LExt = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        static readonly short[] DBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        static readonly short[] DExt = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        static readonly byte[] Order = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
        static readonly Huff FixedLit = new Huff(), FixedDist = new Huff();

        static Rpf9Inflater()
        {
            var l = new byte[288];
            for (int i = 0; i < 288; i++) l[i] = (byte)(i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
            FixedLit.Build(l, 0, 288);
            var d = new byte[30];
            for (int i = 0; i < 30; i++) d[i] = 5;
            FixedDist.Build(d, 0, 30);
        }

        private readonly Huff Lit = new Huff(), Dist = new Huff(), LenCode = new Huff();
        private readonly byte[] Lens = new byte[320];

        private bool Codes(Huff lit, Huff dist)
        {
            for (;;)
            {
                int sym = Decode(lit);
                if (sym < 256)
                {
                    O.Put((byte)sym);
                }
                else if (sym == 256)
                {
                    return true;
                }
                else
                {
                    sym -= 257;
                    if (sym >= 29) throw new InvalidDataException("bad length symbol");
                    int len = LBase[sym] + Bits(LExt[sym]);
                    int ds = Decode(dist);
                    if (ds >= 30) throw new InvalidDataException("bad distance symbol");
                    O.Copy(DBase[ds] + Bits(DExt[ds]), len);
                }
                if (O.Full) return false;
            }
        }

        private void Dynamic()
        {
            int nlen = Bits(5) + 257, ndist = Bits(5) + 1, ncode = Bits(4) + 4;
            if (nlen > 286 || ndist > 30) throw new InvalidDataException("bad counts");
            Array.Clear(Lens, 0, 19);
            for (int i = 0; i < ncode; i++) Lens[Order[i]] = (byte)Bits(3);
            LenCode.Build(Lens, 0, 19);
            int idx = 0;
            while (idx < nlen + ndist)
            {
                int sym = Decode(LenCode);
                if (sym < 16) { Lens[idx++] = (byte)sym; continue; }
                byte v = 0;
                int rep;
                if (sym == 16)
                {
                    if (idx == 0) throw new InvalidDataException("repeat with no first length");
                    v = Lens[idx - 1];
                    rep = 3 + Bits(2);
                }
                else if (sym == 17) rep = 3 + Bits(3);
                else rep = 11 + Bits(7);
                if (idx + rep > nlen + ndist) throw new InvalidDataException("too many lengths");
                while (rep-- > 0) Lens[idx++] = v;
            }
            Lit.Build(Lens, 0, nlen);
            Dist.Build(Lens, nlen, ndist);
        }

        /// <summary>one deflate (or zlib) stream. true = stream ended; false = stopped because the output is full.
        /// Throws InvalidDataException / EndOfStreamException on bad or truncated data.</summary>
        public bool Run(bool zlib)
        {
            if (zlib) { Bits(8); Bits(8); }
            int last;
            do
            {
                last = Bits(1);
                int type = Bits(2);
                if (type == 0)
                {
                    Bb >>= Bc & 7;
                    Bc -= Bc & 7;
                    int len = Bits(16), nlen = Bits(16);
                    if (len != (~nlen & 0xffff)) throw new InvalidDataException("bad stored block length");
                    while (len > 0 && Bc >= 8)
                    {
                        O.Put((byte)Bb);
                        Bb >>= 8;
                        Bc -= 8;
                        len--;
                    }
                    while (len > 0)
                    {
                        if (IbPos >= Ib.Length)
                        {
                            InBase += Ib.Length;
                            Ib = V.Read(InBase, 0x10000);
                            IbPos = 0;
                            if (Ib.Length == 0) throw new EndOfStreamException();
                        }
                        int k = Math.Min(len, Ib.Length - IbPos);
                        O.Write(Ib, IbPos, k);
                        IbPos += k;
                        len -= k;
                    }
                    if (O.Full) return false;
                }
                else if (type == 1)
                {
                    if (!Codes(FixedLit, FixedDist)) return false;
                }
                else if (type == 2)
                {
                    Dynamic();
                    if (!Codes(Lit, Dist)) return false;
                }
                else throw new InvalidDataException("bad block type");
            } while (last == 0);
            Bb >>= Bc & 7;
            Bc -= Bc & 7;
            if (zlib) { Bits(16); Bits(16); }
            return true;
        }
    }
}
