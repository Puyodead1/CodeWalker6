using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI hash names: strings_gta6.txt next to CodeWalker.Core.dll (the GTA VI counterpart of strings.txt), loaded
    /// into <see cref="JenkIndex"/>. Each name goes in under joaat(as written) and joaat(lowercase): schema names are
    /// hashed as written, data values lowercased. Each name also goes into <see cref="Names64"/> under its RPF9 archive
    /// name hash (<see cref="NameHash"/>), which names the archive entries.
    /// </summary>
    public static class Rpf9Strings
    {
        public const string FileName = "strings_gta6.txt";
        static bool loaded;
        static readonly object sync = new object();
        static readonly Dictionary<ulong, string> names64 = new Dictionary<ulong, string>();
        public static int Count { get; private set; }

        /// <summary>RPF9 archive name hash -> name, for every name in the list</summary>
        public static Dictionary<ulong, string> Names64 { get { EnsureLoaded(); return names64; } }

        public static void EnsureLoaded()
        {
            if (loaded) return;
            lock (sync)
            {
                if (loaded) return;
                try
                {
                    var dir = Path.GetDirectoryName(typeof(Rpf9Strings).Assembly.Location);
                    var fpath = Path.Combine(dir, FileName);
                    if (File.Exists(fpath))
                    {
                        foreach (var line in File.ReadLines(fpath, Encoding.UTF8))
                        {
                            var str = line.Trim();
                            if (str.Length == 0 || str.StartsWith("//")) continue;
                            JenkIndex.Ensure(str, JenkHash.GenHash(str));
                            var low = str.ToLowerInvariant();
                            if (low != str) JenkIndex.Ensure(str, JenkHash.GenHash(low));
                            AddName64(str);
                            Count++;
                        }
                    }
                }
                catch (Exception) { }
                loaded = true;
            }
        }

        static void AddName64(string name)
        {
            var h = NameHash(name);
            if (!names64.ContainsKey(h)) names64[h] = name;
            // a known extension is cut off before the game hashes a name (it is kept as an id in the TOC record)
            int dot = name.LastIndexOf('.');
            if (dot > 0 && dot > name.LastIndexOf('/') && Rpf9Keys.Extensions.ContainsValue(name.Substring(dot + 1).ToLowerInvariant()))
            {
                h = NameHash(name.Substring(0, dot));
                if (!names64.ContainsKey(h)) names64[h] = name;
            }
        }

        /// <summary>name for a hash from the hash list (GTA VI names, plus anything else in JenkIndex), or null</summary>
        public static string Get(uint hash)
        {
            EnsureLoaded();
            var s = JenkIndex.TryGetString(hash);
            return string.IsNullOrEmpty(s) ? null : s;
        }

        /// <summary>
        /// RPF9 archive name hash (sub_145305880): MurmurHash3 x64-128 with seed 0, first half, of the path relative to the
        /// archive root, lower case with '\' as '/' (the game's character table). Like the game, only the low byte of the
        /// length goes into the final mix. A known extension must be cut off first (tools\rpf9hash.py).
        /// </summary>
        public static ulong NameHash(string path)
        {
            var d = Encoding.UTF8.GetBytes(path);
            for (int i = 0; i < d.Length; i++)
            {
                byte c = d[i];
                if (c >= (byte)'A' && c <= (byte)'Z') d[i] = (byte)(c + 32);
                else if (c == (byte)'\\') d[i] = (byte)'/';
            }
            const ulong c1 = 0x87c37b91114253d5, c2 = 0x4cf5ad432745937f;
            ulong h1 = 0, h2 = 0;
            int n = d.Length, nb = n / 16;
            for (int i = 0; i < nb; i++)
            {
                ulong k1 = BitConverter.ToUInt64(d, 16 * i), k2 = BitConverter.ToUInt64(d, 16 * i + 8);
                k1 *= c1; k1 = Rotl(k1, 31); k1 *= c2; h1 ^= k1;
                h1 = Rotl(h1, 27); h1 += h2; h1 = h1 * 5 + 0x52dce729;
                k2 *= c2; k2 = Rotl(k2, 33); k2 *= c1; h2 ^= k2;
                h2 = Rotl(h2, 31); h2 += h1; h2 = h2 * 5 + 0x38495ab5;
            }
            int t = 16 * nb, tl = n - t;
            if (tl > 8)
            {
                ulong k2 = 0;
                for (int i = tl - 1; i >= 8; i--) k2 ^= (ulong)d[t + i] << (8 * (i - 8));
                k2 *= c2; k2 = Rotl(k2, 33); k2 *= c1; h2 ^= k2;
            }
            if (tl > 0)
            {
                ulong k1 = 0;
                for (int i = Math.Min(tl, 8) - 1; i >= 0; i--) k1 ^= (ulong)d[t + i] << (8 * i);
                k1 *= c1; k1 = Rotl(k1, 31); k1 *= c2; h1 ^= k1;
            }
            h1 ^= (ulong)(n & 0xff); h2 ^= (ulong)(n & 0xff);
            h1 += h2; h2 += h1;
            h1 = Fmix(h1); h2 = Fmix(h2);
            h1 += h2;
            return h1;
        }

        static ulong Rotl(ulong x, int r) => (x << r) | (x >> (64 - r));
        static ulong Fmix(ulong k)
        {
            k ^= k >> 33; k *= 0xff51afd7ed558ccd;
            k ^= k >> 33; k *= 0xc4ceb9fe1a85ec53;
            return k ^ (k >> 33);
        }
    }
}
