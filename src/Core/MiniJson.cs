using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace ManagerAgent;

/// <summary>
/// Small reflection-based JSON reader/writer for the agent's data files, so the plugin has
/// no dependency beyond netstandard2.1 (works with any BepInEx build).
/// Writes camelCase, indented; reads leniently (comments, trailing commas, any key case).
/// Supports classes with settable properties, List&lt;T&gt;, Dictionary&lt;string,T&gt;,
/// string, bool, numbers, enums (as names) and DateTime (ISO 8601).
/// </summary>
internal static class MiniJson
{
    // ================================================================ write

    public static string Serialize(object value)
    {
        var sb = new StringBuilder();
        Write(sb, value, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object v, int indent)
    {
        switch (v)
        {
            case null: sb.Append("null"); return;
            case string s: WriteString(sb, s); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case DateTime dt: WriteString(sb, dt.ToString("o", CultureInfo.InvariantCulture)); return;
            case Enum e: WriteString(sb, e.ToString()); return;
            case int or long or short or byte or uint or ulong:
                sb.Append(System.Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return;
            case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
            case IDictionary dict:
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int i = 0;
                foreach (DictionaryEntry kv in dict)
                {
                    Pad(sb, indent + 1);
                    WriteString(sb, System.Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(": ");
                    Write(sb, kv.Value, indent + 1);
                    if (++i < dict.Count) sb.Append(',');
                    sb.Append('\n');
                }
                Pad(sb, indent);
                sb.Append('}');
                return;
            }
            case IEnumerable list:
            {
                var items = list.Cast<object>().ToList();
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < items.Count; i++)
                {
                    Pad(sb, indent + 1);
                    Write(sb, items[i], indent + 1);
                    if (i < items.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                Pad(sb, indent);
                sb.Append(']');
                return;
            }
        }

        var props = Props(v.GetType());
        sb.Append("{\n");
        for (int i = 0; i < props.Length; i++)
        {
            Pad(sb, indent + 1);
            WriteString(sb, Camel(props[i].Name));
            sb.Append(": ");
            Write(sb, props[i].GetValue(v), indent + 1);
            if (i < props.Length - 1) sb.Append(',');
            sb.Append('\n');
        }
        Pad(sb, indent);
        sb.Append('}');
    }

    private static void Pad(StringBuilder sb, int indent) => sb.Append(' ', indent * 2);

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
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

    private static string Camel(string n) => string.IsNullOrEmpty(n) ? n : char.ToLowerInvariant(n[0]) + n.Substring(1);

    private static readonly Dictionary<Type, PropertyInfo[]> PropCache = new();

    private static PropertyInfo[] Props(Type t)
    {
        if (!PropCache.TryGetValue(t, out var ps))
        {
            ps = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                .ToArray();
            PropCache[t] = ps;
        }
        return ps;
    }

    // ================================================================ read

    public static T Deserialize<T>(string json) where T : class, new()
    {
        var p = new Parser(json);
        var node = p.ParseValue();
        p.SkipWs();
        if (!p.End) throw p.Error("unexpected text after the end");
        return FromNode(node, typeof(T)) as T ?? new T();
    }

    private static object FromNode(object node, Type t)
    {
        if (node == null) return t.IsValueType ? Activator.CreateInstance(t) : null;
        var under = Nullable.GetUnderlyingType(t);
        if (under != null) t = under;

        if (t == typeof(object)) return node;
        if (t == typeof(string)) return node is string s ? s : System.Convert.ToString(node, CultureInfo.InvariantCulture);
        if (t == typeof(bool)) return node is bool b ? b : bool.Parse(node.ToString());
        if (t.IsEnum) return node is string es ? Enum.Parse(t, es, true) : Enum.ToObject(t, System.Convert.ToInt64(node, CultureInfo.InvariantCulture));
        if (t == typeof(DateTime))
            return node is string ds && DateTime.TryParse(ds, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : default;
        if (t.IsPrimitive || t == typeof(decimal))
            return System.Convert.ChangeType(node is string ns ? double.Parse(ns, CultureInfo.InvariantCulture) : node, t, CultureInfo.InvariantCulture);

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var dict = (IDictionary)Activator.CreateInstance(t);
            var vt = t.GetGenericArguments()[1];
            if (node is Dictionary<string, object> obj)
                foreach (var kv in obj) dict[kv.Key] = FromNode(kv.Value, vt);
            return dict;
        }
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(t);
            var et = t.GetGenericArguments()[0];
            if (node is List<object> arr)
                foreach (var item in arr) list.Add(FromNode(item, et));
            return list;
        }

        if (node is not Dictionary<string, object> fields) return Activator.CreateInstance(t);
        var result = Activator.CreateInstance(t);
        foreach (var p in Props(t))
        {
            var match = fields.Keys.FirstOrDefault(k => string.Equals(k, p.Name, StringComparison.OrdinalIgnoreCase));
            if (match == null) continue;
            try { p.SetValue(result, FromNode(fields[match], p.PropertyType)); }
            catch (Exception ex) { throw new FormatException($"field '{match}': {ex.Message}"); }
        }
        return result;
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) { _s = s ?? ""; }
        public bool End => _i >= _s.Length;

        public FormatException Error(string what)
        {
            int line = 1;
            for (int k = 0; k < _i && k < _s.Length; k++) if (_s[k] == '\n') line++;
            return new FormatException($"JSON error on line {line}: {what}");
        }

        public void SkipWs()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (char.IsWhiteSpace(c) || c == '﻿') { _i++; continue; }
                if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                {
                    while (_i < _s.Length && _s[_i] != '\n') _i++;
                    continue;
                }
                if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
                {
                    int end = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    _i = end < 0 ? _s.Length : end + 2;
                    continue;
                }
                break;
            }
        }

        public object ParseValue()
        {
            SkipWs();
            if (End) throw Error("unexpected end of file");
            char c = _s[_i];
            if (c == '{') return ParseObject();
            if (c == '[') return ParseArray();
            if (c == '"') return ParseString();
            if (c == '-' || char.IsDigit(c)) return ParseNumber();
            if (Match("true")) return true;
            if (Match("false")) return false;
            if (Match("null")) return null;
            throw Error($"unexpected '{c}'");
        }

        private bool Match(string word)
        {
            if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0) return false;
            _i += word.Length;
            return true;
        }

        private Dictionary<string, object> ParseObject()
        {
            var obj = new Dictionary<string, object>(StringComparer.Ordinal);
            _i++; // {
            while (true)
            {
                SkipWs();
                if (End) throw Error("unclosed {");
                if (_s[_i] == '}') { _i++; return obj; }
                if (_s[_i] != '"') throw Error("expected a \"name\"");
                var key = ParseString();
                SkipWs();
                if (End || _s[_i] != ':') throw Error($"expected ':' after \"{key}\"");
                _i++;
                obj[key] = ParseValue();
                SkipWs();
                if (End) throw Error("unclosed {");
                if (_s[_i] == ',') { _i++; continue; }
                if (_s[_i] == '}') { _i++; return obj; }
                throw Error("expected ',' or '}' (missing comma?)");
            }
        }

        private List<object> ParseArray()
        {
            var arr = new List<object>();
            _i++; // [
            while (true)
            {
                SkipWs();
                if (End) throw Error("unclosed [");
                if (_s[_i] == ']') { _i++; return arr; }
                arr.Add(ParseValue());
                SkipWs();
                if (End) throw Error("unclosed [");
                if (_s[_i] == ',') { _i++; continue; }
                if (_s[_i] == ']') { _i++; return arr; }
                throw Error("expected ',' or ']' (missing comma?)");
            }
        }

        private string ParseString()
        {
            var sb = new StringBuilder();
            _i++; // "
            while (true)
            {
                if (End) throw Error("unclosed string");
                char c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (End) throw Error("unclosed string");
                char e = _s[_i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_i + 4 > _s.Length) throw Error("bad \\u escape");
                        sb.Append((char)int.Parse(_s.Substring(_i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _i += 4;
                        break;
                    default: throw Error($"bad escape \\{e}");
                }
            }
        }

        private object ParseNumber()
        {
            int start = _i;
            if (_s[_i] == '-') _i++;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E' || _s[_i] == '+' || _s[_i] == '-')) _i++;
            var text = _s.Substring(start, _i - start);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            throw Error($"bad number '{text}'");
        }
    }
}
