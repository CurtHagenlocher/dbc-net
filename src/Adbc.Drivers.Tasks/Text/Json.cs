using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Adbc.Drivers.Build.Text
{
    internal sealed class JsonParseException : Exception
    {
        public JsonParseException(int position, string message)
            : base($"offset {position}: {message}")
        {
            Position = position;
        }

        public int Position { get; }
    }

    internal enum JsonKind
    {
        Null,
        Boolean,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>
    /// A JSON value. Used for the driver lock file and cache receipts.
    /// </summary>
    /// <remarks>
    /// System.Text.Json is deliberately avoided: shipping it inside an MSBuild task
    /// package is a well-known source of assembly load conflicts with the host and
    /// with other tasks. The lock file schema is small enough not to need it.
    /// </remarks>
    internal sealed class JsonValue
    {
        private static readonly JsonValue NullValue = new JsonValue(JsonKind.Null, null, null, null, null);

        private readonly string? _text;
        private readonly bool? _boolean;
        private readonly List<JsonValue>? _array;
        private readonly List<KeyValuePair<string, JsonValue>>? _members;

        private JsonValue(
            JsonKind kind,
            string? text,
            bool? boolean,
            List<JsonValue>? array,
            List<KeyValuePair<string, JsonValue>>? members)
        {
            Kind = kind;
            _text = text;
            _boolean = boolean;
            _array = array;
            _members = members;
        }

        public JsonKind Kind { get; }

        public static JsonValue Null => NullValue;

        public static JsonValue ForString(string value) =>
            new JsonValue(JsonKind.String, value ?? throw new ArgumentNullException(nameof(value)), null, null, null);

        public static JsonValue ForNumber(string literal) => new JsonValue(JsonKind.Number, literal, null, null, null);

        public static JsonValue ForBoolean(bool value) => new JsonValue(JsonKind.Boolean, null, value, null, null);

        public static JsonValue ForArray(List<JsonValue> items) => new JsonValue(JsonKind.Array, null, null, items, null);

        public static JsonValue ForObject(List<KeyValuePair<string, JsonValue>> members) =>
            new JsonValue(JsonKind.Object, null, null, null, members);

        public bool IsNull => Kind == JsonKind.Null;

        public IReadOnlyList<JsonValue> Items =>
            _array ?? (IReadOnlyList<JsonValue>)Array.Empty<JsonValue>();

        public IReadOnlyList<KeyValuePair<string, JsonValue>> Members =>
            _members ?? (IReadOnlyList<KeyValuePair<string, JsonValue>>)Array.Empty<KeyValuePair<string, JsonValue>>();

        public JsonValue this[string name]
        {
            get
            {
                if (_members is not null)
                {
                    foreach (KeyValuePair<string, JsonValue> member in _members)
                    {
                        if (string.Equals(member.Key, name, StringComparison.Ordinal))
                        {
                            return member.Value;
                        }
                    }
                }

                return NullValue;
            }
        }

        public string? AsString() => Kind switch
        {
            JsonKind.Null => null,
            JsonKind.String => _text,
            JsonKind.Number => _text,
            JsonKind.Boolean => _boolean!.Value ? "true" : "false",
            _ => throw new InvalidOperationException($"Expected a JSON scalar but found {Kind}."),
        };

        public bool? AsBoolean() => Kind switch
        {
            JsonKind.Null => null,
            JsonKind.Boolean => _boolean,
            _ => throw new InvalidOperationException($"Expected a JSON boolean but found {Kind}."),
        };

        public int? AsInt32()
        {
            if (Kind == JsonKind.Null)
            {
                return null;
            }

            if (Kind != JsonKind.Number)
            {
                throw new InvalidOperationException($"Expected a JSON number but found {Kind}.");
            }

            return int.TryParse(_text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : (int?)null;
        }

        public long? AsInt64()
        {
            if (Kind == JsonKind.Null)
            {
                return null;
            }

            if (Kind != JsonKind.Number)
            {
                throw new InvalidOperationException($"Expected a JSON number but found {Kind}.");
            }

            return long.TryParse(_text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                ? value
                : (long?)null;
        }

        public IReadOnlyList<JsonValue> AsArray() => Kind switch
        {
            JsonKind.Null => Array.Empty<JsonValue>(),
            JsonKind.Array => Items,
            _ => throw new InvalidOperationException($"Expected a JSON array but found {Kind}."),
        };
    }

    internal static class JsonParser
    {
        public static JsonValue Parse(string text)
        {
            if (text is null) throw new ArgumentNullException(nameof(text));

            int position = 0;
            SkipWhitespace(text, ref position);
            JsonValue value = ParseValue(text, ref position);
            SkipWhitespace(text, ref position);
            if (position != text.Length)
            {
                throw new JsonParseException(position, "Unexpected trailing content.");
            }

            return value;
        }

        private static JsonValue ParseValue(string text, ref int position)
        {
            if (position >= text.Length)
            {
                throw new JsonParseException(position, "Unexpected end of input.");
            }

            switch (text[position])
            {
                case '{': return ParseObject(text, ref position);
                case '[': return ParseArray(text, ref position);
                case '"': return JsonValue.ForString(ParseString(text, ref position));
                case 't': Expect(text, ref position, "true"); return JsonValue.ForBoolean(true);
                case 'f': Expect(text, ref position, "false"); return JsonValue.ForBoolean(false);
                case 'n': Expect(text, ref position, "null"); return JsonValue.Null;
                default: return JsonValue.ForNumber(ParseNumber(text, ref position));
            }
        }

        private static JsonValue ParseObject(string text, ref int position)
        {
            position++; // '{'
            List<KeyValuePair<string, JsonValue>> members = new List<KeyValuePair<string, JsonValue>>();
            SkipWhitespace(text, ref position);
            if (Peek(text, position) == '}')
            {
                position++;
                return JsonValue.ForObject(members);
            }

            while (true)
            {
                SkipWhitespace(text, ref position);
                if (Peek(text, position) != '"')
                {
                    throw new JsonParseException(position, "Expected a property name.");
                }

                string name = ParseString(text, ref position);
                SkipWhitespace(text, ref position);
                if (Peek(text, position) != ':')
                {
                    throw new JsonParseException(position, "Expected ':' after a property name.");
                }

                position++;
                SkipWhitespace(text, ref position);
                members.Add(new KeyValuePair<string, JsonValue>(name, ParseValue(text, ref position)));
                SkipWhitespace(text, ref position);

                char next = Peek(text, position);
                if (next == ',')
                {
                    position++;
                    continue;
                }

                if (next == '}')
                {
                    position++;
                    return JsonValue.ForObject(members);
                }

                throw new JsonParseException(position, "Expected ',' or '}'.");
            }
        }

        private static JsonValue ParseArray(string text, ref int position)
        {
            position++; // '['
            List<JsonValue> items = new List<JsonValue>();
            SkipWhitespace(text, ref position);
            if (Peek(text, position) == ']')
            {
                position++;
                return JsonValue.ForArray(items);
            }

            while (true)
            {
                SkipWhitespace(text, ref position);
                items.Add(ParseValue(text, ref position));
                SkipWhitespace(text, ref position);

                char next = Peek(text, position);
                if (next == ',')
                {
                    position++;
                    continue;
                }

                if (next == ']')
                {
                    position++;
                    return JsonValue.ForArray(items);
                }

                throw new JsonParseException(position, "Expected ',' or ']'.");
            }
        }

        private static string ParseString(string text, ref int position)
        {
            position++; // opening quote
            StringBuilder builder = new StringBuilder();
            while (true)
            {
                if (position >= text.Length)
                {
                    throw new JsonParseException(position, "Unterminated string.");
                }

                char c = text[position++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    if (c < 0x20)
                    {
                        throw new JsonParseException(position, "Unescaped control character in a string.");
                    }

                    builder.Append(c);
                    continue;
                }

                if (position >= text.Length)
                {
                    throw new JsonParseException(position, "Unterminated escape sequence.");
                }

                char escape = text[position++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (position + 4 > text.Length
                            || !int.TryParse(
                                text.Substring(position, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out int code))
                        {
                            throw new JsonParseException(position, "Malformed \\u escape.");
                        }

                        builder.Append((char)code);
                        position += 4;
                        break;
                    default:
                        throw new JsonParseException(position, $"Unsupported escape '\\{escape}'.");
                }
            }
        }

        private static string ParseNumber(string text, ref int position)
        {
            int start = position;
            if (Peek(text, position) == '-')
            {
                position++;
            }

            while (position < text.Length && IsNumberChar(text[position]))
            {
                position++;
            }

            if (position == start)
            {
                throw new JsonParseException(position, $"Unexpected character '{Peek(text, position)}'.");
            }

            string literal = text.Substring(start, position - start);
            if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                throw new JsonParseException(start, $"Malformed number '{literal}'.");
            }

            return literal;
        }

        private static bool IsNumberChar(char c) =>
            (c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-';

        private static void Expect(string text, ref int position, string literal)
        {
            if (position + literal.Length > text.Length
                || string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0)
            {
                throw new JsonParseException(position, $"Expected '{literal}'.");
            }

            position += literal.Length;
        }

        private static char Peek(string text, int position) => position < text.Length ? text[position] : '\0';

        private static void SkipWhitespace(string text, ref int position)
        {
            while (position < text.Length)
            {
                char c = text[position];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    position++;
                }
                else
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Minimal indented JSON writer. Output is deterministic in the order members are
    /// written, so a lock file only changes when its content changes.
    /// </summary>
    internal sealed class JsonTextWriter
    {
        private readonly StringBuilder _builder = new StringBuilder();
        private readonly List<bool> _hasMembers = new List<bool>();

        public override string ToString() => _builder.ToString();

        public JsonTextWriter StartObject()
        {
            WriteSeparator();
            _builder.Append('{');
            _hasMembers.Add(false);
            return this;
        }

        public JsonTextWriter EndObject()
        {
            bool any = Pop();
            if (any)
            {
                _builder.Append('\n').Append(Indent());
            }

            _builder.Append('}');
            return this;
        }

        public JsonTextWriter StartArray()
        {
            WriteSeparator();
            _builder.Append('[');
            _hasMembers.Add(false);
            return this;
        }

        public JsonTextWriter EndArray()
        {
            bool any = Pop();
            if (any)
            {
                _builder.Append('\n').Append(Indent());
            }

            _builder.Append(']');
            return this;
        }

        public JsonTextWriter Name(string name)
        {
            WriteSeparator();
            _builder.Append(Escape(name)).Append(": ");
            _pendingValue = true;
            return this;
        }

        public JsonTextWriter String(string? value)
        {
            WriteSeparator();
            _builder.Append(value is null ? "null" : Escape(value));
            return this;
        }

        public JsonTextWriter Number(long value)
        {
            WriteSeparator();
            _builder.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonTextWriter Boolean(bool value)
        {
            WriteSeparator();
            _builder.Append(value ? "true" : "false");
            return this;
        }

        public JsonTextWriter Property(string name, string? value) => Name(name).String(value);

        public JsonTextWriter Property(string name, long value) => Name(name).Number(value);

        public JsonTextWriter Property(string name, bool value) => Name(name).Boolean(value);

        public static string Escape(string value)
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
                        if (c < 0x20)
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

        private bool _pendingValue;

        private void WriteSeparator()
        {
            if (_pendingValue)
            {
                _pendingValue = false;
                return;
            }

            if (_hasMembers.Count == 0)
            {
                return;
            }

            int last = _hasMembers.Count - 1;
            if (_hasMembers[last])
            {
                _builder.Append(',');
            }

            _hasMembers[last] = true;
            _builder.Append('\n').Append(Indent());
        }

        private bool Pop()
        {
            int last = _hasMembers.Count - 1;
            bool any = _hasMembers[last];
            _hasMembers.RemoveAt(last);
            return any;
        }

        private string Indent() => new string(' ', _hasMembers.Count * 2);
    }
}
