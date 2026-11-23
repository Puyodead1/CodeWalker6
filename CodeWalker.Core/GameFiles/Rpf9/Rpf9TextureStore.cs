using System;
using System.Collections.Generic;
using System.IO;
using CodeWalker.Utils;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// Finds the textures of GTA VI drawables. Drawables carry no textures: each shader texture reference has a
    /// 64-bit key into the TTDB packs (*.ttpf anywhere under the game folder). The packs give no names or sampler
    /// slots, so each shader's diffuse and normal map are picked by format and colour (see <see cref="Apply"/>).
    /// </summary>
    public class Rpf9TextureStore : IDisposable
    {
        readonly List<Rpf9Ttdb> packs = new List<Rpf9Ttdb>();
        readonly Dictionary<ulong, Rpf9Ttdb> byKey = new Dictionary<ulong, Rpf9Ttdb>();
        readonly Dictionary<ulong, Texture> full = new Dictionary<ulong, Texture>();
        readonly Dictionary<ulong, int> kind = new Dictionary<ulong, int>();   // 0 other, 1 colour, 2 normal, 3 placeholder
        public readonly List<string> Errors = new List<string>();
        public int KeyCount => byKey.Count;
        public int PackCount => packs.Count;

        public Rpf9TextureStore(string gameFolder)
        {
            string[] files;
            try { files = Directory.GetFiles(gameFolder, "*.ttpf", SearchOption.AllDirectories); }
            catch (Exception ex) { Errors.Add(ex.Message); return; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                try
                {
                    var p = new Rpf9Ttdb(f);
                    packs.Add(p);
                    foreach (var k in p.Index.Keys) if (!byKey.ContainsKey(k)) byKey[k] = p;
                }
                catch (Exception ex) { Errors.Add(Path.GetFileName(f) + ": " + ex.Message); }
            }
        }

        public void Dispose() { foreach (var p in packs) p.Dispose(); packs.Clear(); byKey.Clear(); }

        public bool Has(ulong key) => key != 0 && byKey.ContainsKey(key);

        /// <summary>Full texture for a key (cached), or null if missing or undecodable.</summary>
        public Texture Get(ulong key)
        {
            lock (this)
            {
                if (full.TryGetValue(key, out var t)) return t;
                if (!byKey.TryGetValue(key, out var p)) return null;
                try { t = p.GetTexture(key); }
                catch (Exception ex) { Errors.Add(key.ToString("x16") + ": " + ex.Message); t = null; }
                full[key] = t;
                return t;
            }
        }

        /// <summary>0 other, 1 colour (sRGB), 2 normal map, 3 placeholder (tiny constant texture), -1 missing.</summary>
        public int Kind(ulong key)
        {
            lock (this)
            {
                if (kind.TryGetValue(key, out int k)) return k;
                k = -1;
                if (byKey.TryGetValue(key, out var p))
                {
                    var r = p.GetRecord(p.Index[key]);
                    if (r.W <= 16 && r.H <= 16) k = 3;
                    else if (Rpf9Texture.IsSrgb(r.Fmt)) k = 1;
                    else if (r.Fmt == 83 || r.Fmt == 84) k = 2;   // BC5: two-channel normal map
                    else k = LooksLikeNormal(p, key, r) ? 2 : 0;
                }
                kind[key] = k;
                return k;
            }
        }

        /// <summary>average of a small mip is the flat-normal blue (0.5, 0.5, 1)</summary>
        bool LooksLikeNormal(Rpf9Ttdb p, ulong key, Rpf9Ttdb.Record r)
        {
            try
            {
                var small = p.GetTextureMip(key, 32);
                if (small == null) return false;
                var px = DDSIO.GetPixels(small, 0);   // BGRA
                if (px == null || px.Length < 4) return false;
                double b = 0, g = 0, rr = 0; int n = px.Length / 4;
                for (int i = 0; i < n; i++) { b += px[4 * i]; g += px[4 * i + 1]; rr += px[4 * i + 2]; }
                b /= n; g /= n; rr /= n;
                return Math.Abs(rr - 128) < 40 && Math.Abs(g - 128) < 40 && b > 200;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Gives each shader of the drawables a DiffuseSampler (lowest-slot sRGB texture) and a BumpSampler (lowest-slot
        /// normal map) so CodeWalker's basic shader can draw them. Returns the number of shaders that got a diffuse map.
        /// </summary>
        public int Apply(List<Rpf9Drawable.Item> items)
        {
            int textured = 0;
            foreach (var it in items)
            {
                var shaders = it.Drawable?.ShaderGroup?.Shaders?.data_items;
                if (shaders == null) continue;
                var byShader = new Dictionary<int, List<Rpf9Drawable.TexRef>>();
                foreach (var tr in it.TexRefs)
                {
                    if (!byShader.TryGetValue(tr.Shader, out var l)) byShader[tr.Shader] = l = new List<Rpf9Drawable.TexRef>();
                    l.Add(tr);
                }
                var used = new List<Texture>();
                foreach (var kv in byShader)
                {
                    if (kv.Key < 0 || kv.Key >= shaders.Length) continue;
                    ulong diff = 0, bump = 0;
                    kv.Value.Sort((a, b) => a.Slot.CompareTo(b.Slot));
                    foreach (var tr in kv.Value)
                    {
                        int k = Kind(tr.Key);
                        if (k == 1 && diff == 0) diff = tr.Key;
                        else if (k == 2 && bump == 0) bump = tr.Key;
                    }
                    var pars = new List<ShaderParameter>();
                    var names = new List<MetaName>();
                    void Add(ulong key, ShaderParamNames name)
                    {
                        if (key == 0) return;
                        var t = Get(key);
                        if (t == null) return;
                        pars.Add(new ShaderParameter { DataType = 0, Data = t });
                        names.Add((MetaName)(uint)name);
                        if (!used.Contains(t)) used.Add(t);
                    }
                    Add(diff, ShaderParamNames.DiffuseSampler);
                    Add(bump, ShaderParamNames.BumpSampler);
                    if (pars.Count == 0) continue;
                    if (names.Contains((MetaName)(uint)ShaderParamNames.DiffuseSampler)) textured++;
                    var sh = shaders[kv.Key];
                    sh.ParametersList = new ShaderParametersBlock { Parameters = pars.ToArray(), Hashes = names.ToArray(), Owner = sh };
                }
                if (used.Count > 0)
                {
                    var td = new TextureDictionary();
                    td.BuildFromTextureList(used);
                    it.Drawable.ShaderGroup.TextureDictionary = td;
                }
            }
            return textured;
        }
    }
}
