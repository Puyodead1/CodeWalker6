using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI RBF0 (binary XML, .zmt) to XML text. Port of tools\rbf0.py; reader in the exe sub_1416B3200 (main loop
    /// sub_14128BE20, token read sub_1451D2070). Not GTA V's RBF: a token stream, little endian:
    /// <code>
    /// 'RBF0' [u16 0xFFFB, u16 version]          version 1: ints and floats are 64-bit, else 32-bit
    /// token: u16 (flags &lt;&lt; 12) | idx, or u16 0xFFFE + u32 (flags &lt;&lt; 28) | idx (more than 4095 names);
    ///        idx == names so far: a new name follows (u16 len, ascii)
    ///   flags 0: element (sub_14080E220): u16, u16 (ignored, 0), u16 attribute count, attributes (token + value)
    ///   flags 1 s32/s64, 2 true, 3 false, 4 f32/f64, 5 vec3 (3 x f32), 6 u16 len + UTF-8: a leaf element with one value
    /// 0xFFFD u32 len, bytes   content of the open element, read per its content= attribute (sub_1420AC8B0)
    /// 0xFFFC u32 len, bytes   skipped by the reader (sub_144066720)
    /// 0xFFFF                  close element
    /// </code>
    /// </summary>
    public class Rpf9Rbf
    {
        readonly byte[] d;
        int p, version;
        readonly List<string> names = new List<string>();

        // content= (sub_1411CC510) -> (value code, values per line); 'x' = hex bytes, 'u' = UTF-16 text
        static readonly Dictionary<string, (char Code, int Per)> CONTENT = new Dictionary<string, (char, int)>
        {
            { "int_array", ('i', 1) }, { "char_array", ('x', 0) }, { "short_array", ('h', 1) }, { "float_array", ('f', 1) },
            { "vector2_array", ('f', 2) }, { "vec2v_array", ('f', 4) }, { "vector3_array", ('f', 3) }, { "vec3v_array", ('f', 4) },
            { "vector4_array", ('f', 4) }, { "vec4v_array", ('f', 4) }, { "matrix33", ('f', 3) }, { "matrix34", ('f', 4) },
            { "matrix43", ('f', 3) }, { "matrix44", ('f', 4) }, { "int64_array", ('q', 1) }, { "double_array", ('d', 1) },
            { "pos3v_array", ('f', 4) }, { "quatv_array", ('f', 4) }, { "float16_array", ('e', 1) }, { "binary", ('x', 0) },
            { "utf16", ('u', 0) },
        };

        Rpf9Rbf(byte[] data) { d = data; }

        public static bool IsRbf0(byte[] d) => d != null && d.Length >= 6 && d[0] == 'R' && d[1] == 'B' && d[2] == 'F' && d[3] == '0';

        public static string ToXml(byte[] data)
        {
            if (!IsRbf0(data)) return null;
            return new Rpf9Rbf(data).Parse();
        }

        ushort U16() { var v = BitConverter.ToUInt16(d, p); p += 2; return v; }
        uint U32() { var v = BitConverter.ToUInt32(d, p); p += 4; return v; }
        static string F(float v) => FloatUtil.ToString(v);

        class Open { public string Name, Kind; public bool Kids; }

        string Parse()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            var stack = new List<Open>();
            p = 4;
            while (p < d.Length)
            {
                int at = p;
                if (p + 2 > d.Length) throw new InvalidDataException($"RBF0: truncated at 0x{at:X}");
                var tok = U16();
                if (tok == 0xFFFF)
                {
                    if (stack.Count == 0) continue;
                    var o = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    if (o.Kids) Indent(sb, stack.Count);
                    sb.Append("</").Append(o.Name).Append(">\n");
                    continue;
                }
                if (tok == 0xFFFB) { version = U16(); continue; }
                if (tok == 0xFFFD)
                {
                    int n = (int)U32();
                    if (n < 0 || p + n > d.Length) throw new InvalidDataException($"RBF0: content past end at 0x{at:X}");
                    sb.Append(Content(p, n, stack.Count > 0 ? stack[stack.Count - 1].Kind : null));
                    p += n;
                    continue;
                }
                if (tok == 0xFFFC)
                {
                    int n = (int)U32();
                    if (n < 0 || p + n > d.Length) throw new InvalidDataException($"RBF0: skipped block past end at 0x{at:X}");
                    sb.Append($"<!-- skipped block, {n} bytes: {Hex(p, Math.Min(n, 256))}{(n > 256 ? "..." : "")} -->");
                    p += n;
                    continue;
                }
                int flags; string name;
                if (tok == 0xFFFE) { uint v = U32(); flags = (int)(v >> 28); name = Name((int)(v & 0xFFFFFFF), at); }
                else if (tok >= 0xFF00) throw new InvalidDataException($"RBF0: unknown token 0x{tok:X4} at 0x{at:X}");
                else { flags = tok >> 12; name = Name(tok & 0xFFF, at); }
                if (stack.Count > 0 && !stack[stack.Count - 1].Kids) { stack[stack.Count - 1].Kids = true; sb.Append('\n'); }
                Indent(sb, stack.Count);
                if (flags == 0)
                {
                    int u0 = U16(), u1 = U16(), nattr = U16();
                    string kind = null;
                    sb.Append('<').Append(name);
                    for (int i = 0; i < nattr; i++)
                    {
                        int aat = p;
                        var atok = U16();
                        int af; string an;
                        if (atok == 0xFFFE) { uint v = U32(); af = (int)(v >> 28); an = Name((int)(v & 0xFFFFFFF), aat); }
                        else { af = atok >> 12; an = Name(atok & 0xFFF, aat); }
                        var val = Value(af, aat);
                        if (an == "content") kind = val;
                        sb.Append(' ').Append(an).Append("=\"").Append(Esc(val)).Append('"');
                    }
                    if (u0 != 0 || u1 != 0) sb.Append($" rbf_unk=\"{u0:x},{u1:x}\"");
                    sb.Append('>');
                    stack.Add(new Open { Name = name, Kind = kind });
                }
                else if (flags == 5)
                {
                    float x = BitConverter.ToSingle(d, p), y = BitConverter.ToSingle(d, p + 4), z = BitConverter.ToSingle(d, p + 8);
                    p += 12;
                    sb.Append('<').Append(name).Append($" x=\"{F(x)}\" y=\"{F(y)}\" z=\"{F(z)}\" />\n");
                }
                else
                {
                    sb.Append('<').Append(name).Append(" value=\"").Append(Esc(Value(flags, at))).Append("\" />\n");
                }
            }
            while (stack.Count > 0)     // unclosed at end of file
            {
                var o = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                if (o.Kids) Indent(sb, stack.Count);
                sb.Append("</").Append(o.Name).Append(">\n");
            }
            return sb.ToString();
        }

        string Hex(int at, int n)
        {
            var sb = new StringBuilder(n * 2);
            for (int i = 0; i < n; i++) sb.Append(d[at + i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>0xFFFD data, read per the open element's content= attribute; unknown kinds are text</summary>
        string Content(int at, int n, string kind)
        {
            if (kind == null || !CONTENT.TryGetValue(kind, out var c))
                return Esc(Encoding.UTF8.GetString(d, at, n).TrimEnd('\0'));
            if (c.Code == 'x') return Hex(at, n);
            if (c.Code == 'u') return Esc(Encoding.Unicode.GetString(d, at, n & ~1).TrimEnd('\0'));
            int w = c.Code == 'h' || c.Code == 'e' ? 2 : c.Code == 'q' || c.Code == 'd' ? 8 : 4;
            var ci = CultureInfo.InvariantCulture;
            var vals = new List<string>(n / w);
            for (int q = at; q + w <= at + n; q += w)
            {
                switch (c.Code)
                {
                    case 'i': vals.Add(BitConverter.ToInt32(d, q).ToString(ci)); break;
                    case 'h': vals.Add(BitConverter.ToInt16(d, q).ToString(ci)); break;
                    case 'q': vals.Add(BitConverter.ToInt64(d, q).ToString(ci)); break;
                    case 'd': vals.Add(BitConverter.ToDouble(d, q).ToString("R", ci)); break;
                    case 'e': vals.Add(F(Half(BitConverter.ToUInt16(d, q)))); break;
                    default: vals.Add(F(BitConverter.ToSingle(d, q))); break;
                }
            }
            if (vals.Count == 0) return "";
            var sb = new StringBuilder("\n");
            for (int i = 0; i < vals.Count; i += c.Per)
                sb.Append(string.Join(" ", vals.Skip(i).Take(c.Per))).Append('\n');
            return sb.ToString();
        }

        static float Half(ushort h)
        {
            int s = (h >> 15) & 1, e = (h >> 10) & 0x1f, m = h & 0x3ff;
            float v = e == 0 ? m * (1f / 16777216f) : e == 31 ? (m == 0 ? float.PositiveInfinity : float.NaN) : (1 + m / 1024f) * (float)Math.Pow(2, e - 15);
            return s != 0 ? -v : v;
        }

        string Name(int idx, int at)
        {
            if (idx == names.Count)
            {
                int n = U16();
                names.Add(Encoding.ASCII.GetString(d, p, n));
                p += n;
            }
            else if (idx > names.Count) throw new InvalidDataException($"RBF0: name index {idx} > {names.Count} at 0x{at:X}");
            return names[idx];
        }

        string Str(int n)
        {
            if (n < 0 || p + n > d.Length) throw new InvalidDataException($"RBF0: string past end at 0x{p:X}");
            var s = Encoding.UTF8.GetString(d, p, n);
            p += n;
            return s;
        }

        string Value(int flags, int at)
        {
            var c = CultureInfo.InvariantCulture;
            switch (flags)
            {
                case 1:
                    if (version == 1) { var v = BitConverter.ToInt64(d, p); p += 8; return v.ToString(c); }
                    else { var v = BitConverter.ToInt32(d, p); p += 4; return v.ToString(c); }
                case 2: return "true";
                case 3: return "false";
                case 4:
                    if (version == 1) { var v = BitConverter.ToDouble(d, p); p += 8; return v.ToString("R", c); }
                    else { var v = BitConverter.ToSingle(d, p); p += 4; return F(v); }
                case 5:
                    {
                        float x = BitConverter.ToSingle(d, p), y = BitConverter.ToSingle(d, p + 4), z = BitConverter.ToSingle(d, p + 8);
                        p += 12;
                        return F(x) + ", " + F(y) + ", " + F(z);
                    }
                case 6: return Str(U16());
            }
            throw new InvalidDataException($"RBF0: unknown value type {flags} at 0x{at:X}");
        }

        static void Indent(StringBuilder sb, int n) => sb.Append(' ', n * 2);
        static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
