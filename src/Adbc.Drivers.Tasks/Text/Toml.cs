using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Adbc.Drivers.Build.Text
{
    internal sealed class TomlParseException : Exception
    {
        public TomlParseException(int line, string message)
            : base($"line {line}: {message}")
        {
            Line = line;
        }

        public int Line { get; }
    }

    /// <summary>
    /// A TOML table: ordered key/value pairs plus nested sub-tables.
    /// </summary>
    internal sealed class TomlTable
    {
        private readonly List<KeyValuePair<string, string>> _values = new List<KeyValuePair<string, string>>();
        private readonly List<KeyValuePair<string, TomlTable>> _tables = new List<KeyValuePair<string, TomlTable>>();

        public IReadOnlyList<KeyValuePair<string, string>> Values => _values;

        public IReadOnlyList<KeyValuePair<string, TomlTable>> Tables => _tables;

        public string? GetString(string key)
        {
            foreach (KeyValuePair<string, string> pair in _values)
            {
                if (string.Equals(pair.Key, key, StringComparison.Ordinal))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        public int? GetInt32(string key)
        {
            string? text = GetString(key);
            if (text is null)
            {
                return null;
            }

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : (int?)null;
        }

        public TomlTable? GetTable(string key)
        {
            foreach (KeyValuePair<string, TomlTable> pair in _tables)
            {
                if (string.Equals(pair.Key, key, StringComparison.Ordinal))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        public void SetString(string key, string value)
        {
            for (int i = 0; i < _values.Count; i++)
            {
                if (string.Equals(_values[i].Key, key, StringComparison.Ordinal))
                {
                    _values[i] = new KeyValuePair<string, string>(key, value);
                    return;
                }
            }

            _values.Add(new KeyValuePair<string, string>(key, value));
        }

        public TomlTable GetOrAddTable(string key)
        {
            TomlTable? existing = GetTable(key);
            if (existing is not null)
            {
                return existing;
            }

            TomlTable added = new TomlTable();
            _tables.Add(new KeyValuePair<string, TomlTable>(key, added));
            return added;
        }

        /// <summary>Resolves a dotted path such as <c>Driver.shared</c>.</summary>
        public TomlTable? GetTablePath(params string[] path)
        {
            TomlTable? current = this;
            foreach (string segment in path)
            {
                current = current?.GetTable(segment);
                if (current is null)
                {
                    return null;
                }
            }

            return current;
        }

        public TomlTable GetOrAddTablePath(params string[] path)
        {
            TomlTable current = this;
            foreach (string segment in path)
            {
                current = current.GetOrAddTable(segment);
            }

            return current;
        }
    }

    /// <summary>
    /// Reads the subset of TOML used by ADBC package <c>MANIFEST</c> files and driver
    /// manifests: comments, tables (including dotted names), and scalar values.
    /// </summary>
    /// <remarks>
    /// All scalars are surfaced as strings; callers convert. Arrays, inline tables,
    /// arrays of tables, and multi-line strings are rejected rather than guessed at,
    /// for the same reason as in <see cref="YamlParser"/>.
    /// </remarks>
    internal static class TomlParser
    {
        public static TomlTable Parse(string text)
        {
            if (text is null) throw new ArgumentNullException(nameof(text));

            TomlTable root = new TomlTable();
            TomlTable current = root;
            string[] lines = YamlParser.SplitLines(text);

            for (int i = 0; i < lines.Length; i++)
            {
                int number = i + 1;
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (line[0] == '[')
                {
                    if (line.StartsWith("[[", StringComparison.Ordinal))
                    {
                        throw new TomlParseException(number, "Arrays of tables are not supported.");
                    }

                    int close = line.IndexOf(']');
                    if (close < 0)
                    {
                        throw new TomlParseException(number, "Unterminated table header.");
                    }

                    string trailing = StripComment(line.Substring(close + 1)).Trim();
                    if (trailing.Length != 0)
                    {
                        throw new TomlParseException(number, "Unexpected content after a table header.");
                    }

                    string name = line.Substring(1, close - 1).Trim();
                    if (name.Length == 0)
                    {
                        throw new TomlParseException(number, "Empty table header.");
                    }

                    current = root;
                    foreach (string segment in SplitDotted(name, number))
                    {
                        current = current.GetOrAddTable(segment);
                    }

                    continue;
                }

                int equals = IndexOfEqualsOutsideQuotes(line);
                if (equals < 0)
                {
                    throw new TomlParseException(number, "Expected 'key = value'.");
                }

                string key = line.Substring(0, equals).Trim();
                string rawValue = line.Substring(equals + 1).Trim();
                if (key.Length == 0)
                {
                    throw new TomlParseException(number, "Empty key.");
                }

                // SplitDotted performs the unquoting, so that a quoted key containing a
                // dot is one segment rather than two.
                string[] keyPath = SplitDotted(key, number);
                TomlTable target = current;
                for (int s = 0; s < keyPath.Length - 1; s++)
                {
                    target = target.GetOrAddTable(keyPath[s]);
                }

                target.SetString(keyPath[keyPath.Length - 1], ParseValue(rawValue, number));
            }

            return root;
        }

        private static string ParseValue(string raw, int number)
        {
            if (raw.Length == 0)
            {
                throw new TomlParseException(number, "Missing value.");
            }

            if (raw.StartsWith("\"\"\"", StringComparison.Ordinal) || raw.StartsWith("'''", StringComparison.Ordinal))
            {
                throw new TomlParseException(number, "Multi-line strings are not supported.");
            }

            if (raw[0] == '[')
            {
                throw new TomlParseException(number, "Arrays are not supported.");
            }

            if (raw[0] == '{')
            {
                throw new TomlParseException(number, "Inline tables are not supported.");
            }

            if (raw[0] == '"')
            {
                return UnquoteBasicString(raw, number);
            }

            if (raw[0] == '\'')
            {
                int close = raw.IndexOf('\'', 1);
                if (close < 0)
                {
                    throw new TomlParseException(number, "Unterminated literal string.");
                }

                if (StripComment(raw.Substring(close + 1)).Trim().Length != 0)
                {
                    throw new TomlParseException(number, "Unexpected content after a literal string.");
                }

                return raw.Substring(1, close - 1);
            }

            // Bare value: integer, float, boolean, or date. Kept verbatim; callers convert.
            string bare = StripComment(raw).Trim();
            if (bare.Length == 0)
            {
                throw new TomlParseException(number, "Missing value.");
            }

            return bare;
        }

        private static string UnquoteBasicString(string raw, int number)
        {
            StringBuilder builder = new StringBuilder();
            int i = 1;
            bool closed = false;
            while (i < raw.Length)
            {
                char c = raw[i];
                if (c == '"')
                {
                    closed = true;
                    i++;
                    break;
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    i++;
                    continue;
                }

                i++;
                if (i >= raw.Length)
                {
                    throw new TomlParseException(number, "Trailing escape character in a string.");
                }

                switch (raw[i])
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case 'u':
                    case 'U':
                        int width = raw[i] == 'u' ? 4 : 8;
                        if (i + width >= raw.Length
                            || !int.TryParse(
                                raw.Substring(i + 1, width),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out int code))
                        {
                            throw new TomlParseException(number, "Malformed unicode escape in a string.");
                        }

                        builder.Append(char.ConvertFromUtf32(code));
                        i += width;
                        break;
                    default:
                        throw new TomlParseException(number, $"Unsupported escape '\\{raw[i]}' in a string.");
                }

                i++;
            }

            if (!closed)
            {
                throw new TomlParseException(number, "Unterminated string.");
            }

            if (StripComment(raw.Substring(i)).Trim().Length != 0)
            {
                throw new TomlParseException(number, "Unexpected content after a string.");
            }

            return builder.ToString();
        }

        private static string[] SplitDotted(string name, int number)
        {
            List<string> segments = new List<string>();
            int start = 0;
            bool inQuotes = false;
            char quote = '\0';

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (inQuotes)
                {
                    if (c == quote)
                    {
                        inQuotes = false;
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inQuotes = true;
                    quote = c;
                    continue;
                }

                if (c == '.')
                {
                    segments.Add(UnquoteKey(name.Substring(start, i - start).Trim(), number));
                    start = i + 1;
                }
            }

            if (inQuotes)
            {
                throw new TomlParseException(number, "Unterminated quoted name.");
            }

            segments.Add(UnquoteKey(name.Substring(start).Trim(), number));

            foreach (string segment in segments)
            {
                if (segment.Length == 0)
                {
                    throw new TomlParseException(number, "Empty name segment.");
                }
            }

            return segments.ToArray();
        }

        private static string UnquoteKey(string key, int number)
        {
            if (key.Length >= 2 && (key[0] == '"' || key[0] == '\'') && key[key.Length - 1] == key[0])
            {
                return key.Substring(1, key.Length - 2);
            }

            if (key.IndexOf('"') >= 0 || key.IndexOf('\'') >= 0)
            {
                throw new TomlParseException(number, $"Malformed quoted key '{key}'.");
            }

            return key;
        }

        private static int IndexOfEqualsOutsideQuotes(string line)
        {
            bool inQuotes = false;
            char quote = '\0';
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == quote)
                    {
                        inQuotes = false;
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    inQuotes = true;
                    quote = c;
                }
                else if (c == '=')
                {
                    return i;
                }
            }

            return -1;
        }

        private static string StripComment(string text)
        {
            int hash = text.IndexOf('#');
            return hash < 0 ? text : text.Substring(0, hash);
        }
    }

    /// <summary>
    /// Writes ADBC driver manifests. Values are always emitted as TOML basic strings
    /// unless declared bare, so Windows paths are escaped correctly.
    /// </summary>
    internal static class TomlWriter
    {
        public static string Write(TomlTable table, string? header)
        {
            if (table is null) throw new ArgumentNullException(nameof(table));

            StringBuilder builder = new StringBuilder();
            if (!string.IsNullOrEmpty(header))
            {
                foreach (string line in YamlParser.SplitLines(header!))
                {
                    builder.Append("# ").Append(line).Append('\n');
                }

                builder.Append('\n');
            }

            WriteTable(builder, table, string.Empty);
            return builder.ToString();
        }

        public static string EscapeBasicString(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == 0x7f)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static void WriteTable(StringBuilder builder, TomlTable table, string prefix)
        {
            foreach (KeyValuePair<string, string> value in table.Values)
            {
                builder.Append(FormatKey(value.Key)).Append(" = ");
                builder.Append(IsBareValue(value.Value) ? value.Value : EscapeBasicString(value.Value));
                builder.Append('\n');
            }

            foreach (KeyValuePair<string, TomlTable> child in table.Tables)
            {
                string name = prefix.Length == 0
                    ? FormatKey(child.Key)
                    : prefix + "." + FormatKey(child.Key);
                builder.Append('\n').Append('[').Append(name).Append(']').Append('\n');
                WriteTable(builder, child.Value, name);
            }
        }

        /// <summary>
        /// Integers and booleans round-trip as bare values; everything else is quoted.
        /// Version strings such as "1.11.0" must stay quoted, so floats are excluded.
        /// </summary>
        private static bool IsBareValue(string value)
        {
            if (string.Equals(value, "true", StringComparison.Ordinal)
                || string.Equals(value, "false", StringComparison.Ordinal))
            {
                return true;
            }

            if (value.Length == 0)
            {
                return false;
            }

            foreach (char c in value)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private static string FormatKey(string key)
        {
            bool bare = key.Length > 0;
            foreach (char c in key)
            {
                bool ok = (c >= 'A' && c <= 'Z')
                    || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9')
                    || c == '_'
                    || c == '-';
                if (!ok)
                {
                    bare = false;
                    break;
                }
            }

            return bare ? key : EscapeBasicString(key);
        }
    }
}
