using System;
using System.IO;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI hash names: strings_gta6.txt next to CodeWalker.Core.dll (the GTA VI counterpart of strings.txt), loaded
    /// into <see cref="JenkIndex"/>. Each name goes in under joaat(as written) and joaat(lowercase): schema names are
    /// hashed as written, data values lowercased.
    /// </summary>
    public static class Rpf9Strings
    {
        public const string FileName = "strings_gta6.txt";
        static bool loaded;
        static readonly object sync = new object();
        public static int Count { get; private set; }

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
                            Count++;
                        }
                    }
                }
                catch (Exception) { }
                loaded = true;
            }
        }

        /// <summary>name for a hash from the hash list (GTA VI names, plus anything else in JenkIndex), or null</summary>
        public static string Get(uint hash)
        {
            EnsureLoaded();
            var s = JenkIndex.TryGetString(hash);
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
