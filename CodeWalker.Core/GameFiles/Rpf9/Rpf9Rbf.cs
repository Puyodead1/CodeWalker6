using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodeWalker.GameFiles
{
    /// <summary>
    /// GTA VI RBF0 (binary XML, .zmt) to XML text. Port of tools\rbf0.py. Not GTA V's RBF: a token stream, little endian:
    /// <code>
    /// 'RBF0' [u16 0xFFFB, u16 version]          no header = version 0
    /// u16 token:
    ///   0xFFFF                                   close element
    ///   0xFFFD u32 len, bytes (NUL terminated)   text content
    ///   (flags &lt;&lt; 12) | name index           name index == names so far: new name follows (u16 len, ascii)
    ///     flags 0: element: u32 (0), u16 attribute count, attributes (name token + value as below)
    ///     flags 1: int value element (s32 in v0, s64 in v1)     2: true     3: false
    ///     flags 4: float value element (f32 in v0, f64 in v1)   6: string (u16 len, chars)
    /// </code>
    /// Value elements carry no children and no close token.
    /// </summary>
    public class Rpf9Rbf
    {
        readonly byte[] d;
        int p, version;
        readonly List<string> names = new List<string>();

        Rpf9Rbf(byte[] data) { d = data; }

        public static bool IsRbf0(byte[] d) => d != null && d.Length >= 6 && d[0] == 'R' && d[1] == 'B' && d[2] == 'F' && d[3] == '0';

        public static string ToXml(byte[] data)
        {
            if (!IsRbf0(data)) return null;
            return new Rpf9Rbf(data).Parse();
        }

        ushort U16() { var v = BitConverter.ToUInt16(d, p); p += 2; return v; }
        uint U32() { var v = BitConverter.ToUInt32(d, p); p += 4; return v; }

        string Parse()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            var stack = new List<string>();
            bool open = false;          // last element opened has had no children yet (text goes inline)
            p = 4;
            while (p < d.Length)
            {
                int at = p;
                if (p + 2 > d.Length) throw new InvalidDataException($"RBF0: truncated at 0x{at:X}");
                var tok = U16();
                if (tok == 0xFFFF)
                {
                    if (stack.Count == 0) continue;
                    var nm = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    if (!open) Indent(sb, stack.Count);
                    sb.Append("</").Append(nm).Append(">\n");
                    open = false;
                    continue;
                }
                if (tok == 0xFFFB) { version = U16(); continue; }
                if (tok == 0xFFFD)
                {
                    var n = (int)U32();
                    sb.Append(Esc(Str(n).TrimEnd('\0')));
                    continue;
                }
                if (tok >= 0xFF00) throw new InvalidDataException($"RBF0: unknown token 0x{tok:X4} at 0x{at:X}");
                int flags = tok >> 12;
                string name = Name(tok & 0xFFF, at);
                if (open) { sb.Append('\n'); open = false; }
                Indent(sb, stack.Count);
                if (flags == 0)
                {
                    var unk = U32();
                    int nattr = U16();
                    sb.Append('<').Append(name);
                    for (int i = 0; i < nattr; i++)
                    {
                        int aat = p;
                        var atok = U16();
                        var an = Name(atok & 0xFFF, aat);
                        sb.Append(' ').Append(an).Append("=\"").Append(Esc(Value(atok >> 12, aat))).Append('"');
                    }
                    if (unk != 0) sb.Append(" rbf_unk=\"").Append(unk.ToString("X")).Append('"');
                    sb.Append('>');
                    stack.Add(name);
                    open = true;
                }
                else
                {
                    sb.Append('<').Append(name).Append(" value=\"").Append(Esc(Value(flags, at))).Append("\" />\n");
                }
            }
            while (stack.Count > 0)     // unclosed at end of file
            {
                var nm = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                if (!open) Indent(sb, stack.Count);
                sb.Append("</").Append(nm).Append(">\n");
                open = false;
            }
            return sb.ToString();
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
                    if (version != 0) { var v = BitConverter.ToInt64(d, p); p += 8; return v.ToString(c); }
                    else { var v = BitConverter.ToInt32(d, p); p += 4; return v.ToString(c); }
                case 2: return "true";
                case 3: return "false";
                case 4:
                    if (version != 0) { var v = BitConverter.ToDouble(d, p); p += 8; return v.ToString("R", c); }
                    else { var v = BitConverter.ToSingle(d, p); p += 4; return FloatUtil.ToString(v); }
                case 6: return Str(U16());
            }
            throw new InvalidDataException($"RBF0: unknown value type {flags} at 0x{at:X}");
        }

        static void Indent(StringBuilder sb, int n) => sb.Append(' ', n * 2);
        static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
