using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Buzzcord
{
    // Small JSON reader/writer. Objects become JObj (a dictionary with typed getters),
    // arrays become List<object>, numbers become long or double.
    public sealed class JObj : Dictionary<string, object>
    {
        public JObj() : base(StringComparer.Ordinal) { }

        public string Str(string key)
        {
            object v;
            if (!TryGetValue(key, out v) || v == null) return null;
            return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public long Long(string key, long fallback = 0)
        {
            object v;
            if (!TryGetValue(key, out v) || v == null) return fallback;
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            long l;
            return long.TryParse(v as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out l) ? l : fallback;
        }

        public bool Bool(string key)
        {
            object v;
            return TryGetValue(key, out v) && v is bool && (bool)v;
        }

        public JObj Obj(string key)
        {
            object v;
            return TryGetValue(key, out v) ? v as JObj : null;
        }

        public List<object> Arr(string key)
        {
            object v;
            return TryGetValue(key, out v) ? v as List<object> : null;
        }

        public IEnumerable<JObj> Objs(string key)
        {
            var a = Arr(key);
            if (a == null) yield break;
            foreach (var o in a)
            {
                var j = o as JObj;
                if (j != null) yield return j;
            }
        }

        public bool Has(string key) { return ContainsKey(key); }
    }

    public static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            var v = Read(s, ref i);
            return v;
        }

        public static JObj ParseObj(string s) { return Parse(s) as JObj; }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object Read(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                var o = new JObj();
                i++;
                Ws(s, ref i);
                if (s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = ReadString(s, ref i);
                    Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("Expected : at " + i);
                    i++;
                    o[k] = Read(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return o; }
                    throw new FormatException("Expected , or } at " + i);
                }
            }
            if (c == '[')
            {
                var a = new List<object>();
                i++;
                Ws(s, ref i);
                if (s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Read(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return a; }
                    throw new FormatException("Expected , or ] at " + i);
                }
            }
            if (c == '"') return ReadString(s, ref i);
            if (c == 't' && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (c == 'f' && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (c == 'n' && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            bool real = false;
            while (i < s.Length)
            {
                char d = s[i];
                if (d >= '0' && d <= '9' || d == '-' || d == '+') { i++; continue; }
                if (d == '.' || d == 'e' || d == 'E') { real = true; i++; continue; }
                break;
            }
            if (start == i) throw new FormatException("Unexpected character '" + c + "' at " + i);
            string num = s.Substring(start, i - start);
            long l;
            if (!real && long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string ReadString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected string at " + i);
            i++;
            int start = i;
            // Fast path: no escapes.
            while (i < s.Length && s[i] != '"' && s[i] != '\\') i++;
            if (i < s.Length && s[i] == '"') { i++; return s.Substring(start, i - 1 - start); }
            var sb = new StringBuilder(s, start, i - start, i - start + 16);
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("Unterminated string");
        }

        public static string Write(object v)
        {
            var sb = new StringBuilder();
            Write(sb, v);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            var str = v as string;
            if (str != null) { WriteString(sb, str); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is double || v is float || v is decimal)
            {
                sb.Append(Convert.ToDouble(v).ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, (string)e.Key);
                    sb.Append(':');
                    Write(sb, e.Value);
                }
                sb.Append('}');
                return;
            }
            var list = v as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, v.ToString());
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // Builds a JObj from alternating key/value pairs: Json.O("op", 1, "d", null)
        public static JObj O(params object[] kv)
        {
            var o = new JObj();
            for (int i = 0; i + 1 < kv.Length; i += 2) o[(string)kv[i]] = kv[i + 1];
            return o;
        }
    }
}
