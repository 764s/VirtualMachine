using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FFVM.Debug
{
    // ============================================================
    // Minimal JSON reader/writer — zero external dependencies.
    // Only supports the subset needed by DAP protocol messages.
    // ============================================================

    /// <summary>
    /// Lightweight JSON object representation (string-keyed dictionary).
    /// Supports nested objects, arrays, strings, numbers, booleans, null.
    /// </summary>
    public class JsonObject
    {
        private readonly Dictionary<string, object> _map = new Dictionary<string, object>();

        public JsonObject() { }

        public void Set(string key, object value) => _map[key] = value;

        public object Get(string key) => _map.TryGetValue(key, out var v) ? v : null;

        public string GetString(string key) => Get(key) as string;

        public int GetInt(string key, int defaultValue = 0)
        {
            var v = Get(key);
            if (v is double d) return (int)d;
            if (v is int i) return i;
            if (v is long l) return (int)l;
            return defaultValue;
        }

        public bool GetBool(string key, bool defaultValue = false)
        {
            var v = Get(key);
            if (v is bool b) return b;
            return defaultValue;
        }

        public JsonObject GetObject(string key) => Get(key) as JsonObject;

        public List<object> GetArray(string key) => Get(key) as List<object>;

        public bool ContainsKey(string key) => _map.ContainsKey(key);

        public IEnumerable<string> Keys => _map.Keys;

        /// <summary>Serialize this object to a JSON string.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            WriteValue(sb, this);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("null");
            }
            else if (value is JsonObject obj)
            {
                sb.Append('{');
                bool first = true;
                foreach (var key in obj._map.Keys)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, key);
                    sb.Append(':');
                    WriteValue(sb, obj._map[key]);
                }
                sb.Append('}');
            }
            else if (value is List<object> arr)
            {
                sb.Append('[');
                for (int i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteValue(sb, arr[i]);
                }
                sb.Append(']');
            }
            else if (value is string s)
            {
                WriteString(sb, s);
            }
            else if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
            }
            else if (value is int intVal)
            {
                sb.Append(intVal.ToString(CultureInfo.InvariantCulture));
            }
            else if (value is long longVal)
            {
                sb.Append(longVal.ToString(CultureInfo.InvariantCulture));
            }
            else if (value is double dblVal)
            {
                sb.Append(dblVal.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                // Fallback: treat as string
                WriteString(sb, value.ToString());
            }
        }

        private static void WriteString(StringBuilder sb, string s)
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
                        if (c < 0x20)
                            sb.Append($"\\u{(int)c:X4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ============================================================
        // Minimal JSON parser
        // ============================================================

        /// <summary>Parse a JSON string into a JsonObject (top-level must be object).</summary>
        public static JsonObject Parse(string json)
        {
            int pos = 0;
            var result = ParseValue(json, ref pos);
            return result as JsonObject;
        }

        private static object ParseValue(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length) return null;

            char c = json[pos];
            if (c == '{') return ParseObject(json, ref pos);
            if (c == '[') return ParseArray(json, ref pos);
            if (c == '"') return ParseString(json, ref pos);
            if (c == 't') { Expect(json, ref pos, "true"); return true; }
            if (c == 'f') { Expect(json, ref pos, "false"); return false; }
            if (c == 'n') { Expect(json, ref pos, "null"); return null; }
            if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(json, ref pos);

            throw new FormatException($"Unexpected character '{c}' at position {pos}");
        }

        private static JsonObject ParseObject(string json, ref int pos)
        {
            pos++; // skip '{'
            var obj = new JsonObject();
            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == '}') { pos++; return obj; }

            while (pos < json.Length)
            {
                SkipWhitespace(json, ref pos);
                string key = ParseString(json, ref pos);
                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ':') pos++;
                object value = ParseValue(json, ref pos);
                obj.Set(key, value);
                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
                if (pos < json.Length && json[pos] == '}') { pos++; break; }
                break;
            }
            return obj;
        }

        private static List<object> ParseArray(string json, ref int pos)
        {
            pos++; // skip '['
            var arr = new List<object>();
            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == ']') { pos++; return arr; }

            while (pos < json.Length)
            {
                arr.Add(ParseValue(json, ref pos));
                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
                if (pos < json.Length && json[pos] == ']') { pos++; break; }
                break;
            }
            return arr;
        }

        private static string ParseString(string json, ref int pos)
        {
            if (json[pos] != '"')
                throw new FormatException($"Expected '\"' at position {pos}");
            pos++; // skip opening quote
            var sb = new StringBuilder();
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c == '"') { pos++; return sb.ToString(); }
                if (c == '\\')
                {
                    pos++;
                    if (pos >= json.Length) break;
                    char esc = json[pos];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 4 < json.Length)
                            {
                                string hex = json.Substring(pos + 1, 4);
                                sb.Append((char)int.Parse(hex, NumberStyles.HexNumber));
                                pos += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
                pos++;
            }
            return sb.ToString();
        }

        private static double ParseNumber(string json, ref int pos)
        {
            int start = pos;
            if (pos < json.Length && json[pos] == '-') pos++;
            while (pos < json.Length && json[pos] >= '0' && json[pos] <= '9') pos++;
            if (pos < json.Length && json[pos] == '.')
            {
                pos++;
                while (pos < json.Length && json[pos] >= '0' && json[pos] <= '9') pos++;
            }
            if (pos < json.Length && (json[pos] == 'e' || json[pos] == 'E'))
            {
                pos++;
                if (pos < json.Length && (json[pos] == '+' || json[pos] == '-')) pos++;
                while (pos < json.Length && json[pos] >= '0' && json[pos] <= '9') pos++;
            }
            return double.Parse(json.Substring(start, pos - start), CultureInfo.InvariantCulture);
        }

        private static void SkipWhitespace(string json, ref int pos)
        {
            while (pos < json.Length && (json[pos] == ' ' || json[pos] == '\t' || json[pos] == '\n' || json[pos] == '\r'))
                pos++;
        }

        private static void Expect(string json, ref int pos, string expected)
        {
            for (int i = 0; i < expected.Length; i++)
            {
                if (pos >= json.Length || json[pos] != expected[i])
                    throw new FormatException($"Expected '{expected}' at position {pos}");
                pos++;
            }
        }
    }
}
