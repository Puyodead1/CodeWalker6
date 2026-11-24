using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI character kit (.zkit, a PRD0 resource in character_kits_streamed.rpf), read through its XML form
    /// (<see cref="Rpf9Psin.ToXml"/>). Two lists of groups:
    ///   root/hash_05269A03/Item/Kits/Item: parts. name (hash_65121749), Category, States/Item: StateName,
    ///     Asset: Drawable / Albedo / Normal / Material = 64-bit keys (Drawable = RPF9 name hash of the .zdd).
    ///   root/hash_9BCF5F90/Item/Kits/Item: outfits. name, tags (hash_12F85293: key "fragment" = the rig .zft),
    ///     parts (hash_FB5BB20D/Item: name, StateName) naming parts, or other outfits (empty StateName).
    /// Element names show as hash_XXXXXXXX when the name list isn't loaded; names and values are matched either way.
    /// </summary>
    public class Rpf9Kit
    {
        public class Part
        {
            public uint Name;          // joaat(lowercase name)
            public string NameText;    // as the XML has it (a name or hash_XXXXXXXX)
            public string Category;
            public uint Template;      // joaat(TemplateName): the part this one is a variant of, 0 if none
            public ulong Drawable, Albedo, Normal, Material;
        }

        public class Outfit
        {
            public uint Name;
            public string NameText;
            public uint Fragment;      // joaat(rig name), 0 if none
            public string FragmentText;
            public List<uint> Refs = new List<uint>();
        }

        public Dictionary<uint, Part> Parts = new Dictionary<uint, Part>();
        public List<Part> PartList = new List<Part>();
        public List<Outfit> Outfits = new List<Outfit>();

        static bool Is(XmlNode n, string name, uint hash) =>
            n != null && (n.Name == name || n.Name.Equals("hash_" + hash.ToString("X8"), StringComparison.OrdinalIgnoreCase));

        static XmlNode Child(XmlNode n, string name, uint hash)
        {
            if (n == null) return null;
            foreach (XmlNode c in n.ChildNodes) if (Is(c, name, hash)) return c;
            return null;
        }

        static IEnumerable<XmlNode> Items(XmlNode n)
        {
            if (n == null) yield break;
            foreach (XmlNode c in n.ChildNodes) if (c.NodeType == XmlNodeType.Element && c.Name == "Item") yield return c;
        }

        /// <summary>joaat of a value: the name lowercased, or the hash a hash_XXXXXXXX stands for</summary>
        public static uint ValueHash(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return 0;
            if (s.Length == 13 && s.StartsWith("hash_", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(s.Substring(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return h;
            return Rpf9Psin.Joaat(s);
        }

        static ulong Key(XmlNode n)
        {
            var s = n?.InnerText?.Trim() ?? "";
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        // schema names and their hashes (joaat as written), for XML without the name list
        const uint H_PARTS = 0x05269A03, H_OUTFITS = 0x9BCF5F90, H_KITS = 0x593BC9C3, H_NAME = 0x65121749;
        const uint H_STATES = 0xEBACF18C, H_STATENAME = 0x3EADDCD7, H_ASSET = 0xE7CB9E1B;
        const uint H_DRAWABLE = 0x41ACF8EB, H_ALBEDO = 0xEAD733FC, H_NORMAL = 0xD689B17F, H_MATERIAL = 0x9D1000D8;
        const uint H_TAGS = 0x12F85293, H_REFS = 0xFB5BB20D, H_TEMPLATE = 0x080EEC39;

        public static Rpf9Kit Read(byte[] data, Rpf9FileEntry e)
        {
            var xml = Rpf9Psin.ToXml(data, e);
            if (string.IsNullOrEmpty(xml)) return null;
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var root = doc.DocumentElement;
            var kit = new Rpf9Kit();

            foreach (var grp in Items(Child(root, "hash_05269A03", H_PARTS)))
                foreach (var it in Items(Child(grp, "Kits", H_KITS)))
                {
                    var nm = Child(it, "hash_65121749", H_NAME)?.InnerText ?? "";
                    var p = new Part { Name = ValueHash(nm), NameText = nm };
                    p.Category = CategoryText(it);
                    XmlNode asset = null;
                    foreach (var st in Items(Child(it, "States", H_STATES)))
                    {
                        var a = Child(st, "Asset", H_ASSET);
                        if (a == null) continue;
                        if (asset == null) asset = a;
                        var sn = Child(st, "StateName", H_STATENAME)?.InnerText ?? "";
                        if (ValueHash(sn) == Rpf9Psin.Joaat("base")) { asset = a; break; }
                    }
                    if (asset != null)
                    {
                        p.Drawable = Key(Child(asset, "Drawable", H_DRAWABLE));
                        p.Albedo = Key(Child(asset, "Albedo", H_ALBEDO));
                        p.Normal = Key(Child(asset, "Normal", H_NORMAL));
                        p.Material = Key(Child(asset, "Material", H_MATERIAL));
                    }
                    p.Template = ValueHash(Child(it, "TemplateName", H_TEMPLATE)?.InnerText);
                    kit.PartList.Add(p);
                    if (p.Name != 0 && !kit.Parts.ContainsKey(p.Name)) kit.Parts[p.Name] = p;
                }
            // a part with a template (TemplateName = another part) takes the template's assets where it has none
            // (human parts: the template has the drawable, the part a different albedo)
            for (int pass = 0; pass < 4; pass++)
                foreach (var p in kit.PartList)
                {
                    if (p.Template == 0 || p.Template == p.Name || !kit.Parts.TryGetValue(p.Template, out var t)) continue;
                    if (p.Drawable == 0) p.Drawable = t.Drawable;
                    if (p.Albedo == 0) p.Albedo = t.Albedo;
                    if (p.Normal == 0) p.Normal = t.Normal;
                    if (p.Material == 0) p.Material = t.Material;
                    if (string.IsNullOrEmpty(p.Category)) p.Category = t.Category;
                }

            foreach (var grp in Items(Child(root, "hash_9BCF5F90", H_OUTFITS)))
                foreach (var it in Items(Child(grp, "Kits", H_KITS)))
                {
                    var nm = Child(it, "hash_65121749", H_NAME)?.InnerText ?? "";
                    var o = new Outfit { Name = ValueHash(nm), NameText = nm };
                    foreach (var tag in Items(Child(it, "hash_12F85293", H_TAGS)))
                    {
                        var key = tag.Attributes?["key"]?.Value ?? "";
                        if (ValueHash(key) != Rpf9Psin.Joaat("fragment")) continue;
                        var v = Items(tag).FirstOrDefault()?.InnerText ?? "";
                        o.Fragment = ValueHash(v);
                        o.FragmentText = v;
                    }
                    foreach (var r in Items(Child(it, "hash_FB5BB20D", H_REFS)))
                    {
                        var h = ValueHash(Child(r, "hash_65121749", H_NAME)?.InnerText);
                        if (h != 0) o.Refs.Add(h);
                    }
                    kit.Outfits.Add(o);
                }
            return kit;
        }

        static string CategoryText(XmlNode it)
        {
            foreach (XmlNode c in it.ChildNodes)
                if (c.Name == "Category" || c.Name.Equals("hash_" + Rpf9Psin.Joaat("Category", false).ToString("X8"), StringComparison.OrdinalIgnoreCase))
                    return c.InnerText;
            return "";
        }

        /// <summary>
        /// Texture keys to replace in a part's drawable: parts that share a drawable differ by their Albedo / Normal /
        /// Material keys, and the drawable's own texture references hold one of them (the default part's). Maps every
        /// other sharing part's key to this part's.
        /// </summary>
        public Dictionary<ulong, ulong> TextureSwaps(Part p)
        {
            var res = new Dictionary<ulong, ulong>();
            if (p == null || p.Drawable == 0) return res;
            void Add(ulong from, ulong to) { if (from != 0 && to != 0 && from != to && !res.ContainsKey(from)) res[from] = to; }
            foreach (var s in PartList)
            {
                if (s == p || s.Drawable != p.Drawable) continue;
                Add(s.Albedo, p.Albedo);
                Add(s.Normal, p.Normal);
                Add(s.Material, p.Material);
            }
            return res;
        }

        /// <summary>The parts an outfit puts on, following references to other outfits; the fragment (rig) of the first
        /// outfit on the way that names one.</summary>
        public List<Part> Resolve(Outfit o, out uint fragment)
        {
            var res = new List<Part>();
            fragment = 0;
            var seen = new HashSet<uint>();
            var byName = new Dictionary<uint, Outfit>();
            foreach (var x in Outfits) if (x.Name != 0 && !byName.ContainsKey(x.Name)) byName[x.Name] = x;
            uint frag = 0;
            void Walk(Outfit w, int depth)
            {
                if (w == null || depth > 8) return;
                if (frag == 0) frag = w.Fragment;
                foreach (var h in w.Refs)
                {
                    if (Parts.TryGetValue(h, out var p)) { if (seen.Add(h)) res.Add(p); }
                    else if (byName.TryGetValue(h, out var sub) && sub != w) Walk(sub, depth + 1);
                }
            }
            Walk(o, 0);
            fragment = frag;
            return res;
        }
    }
}
