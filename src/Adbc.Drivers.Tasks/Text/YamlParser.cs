using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Adbc.Drivers.Build.Text
{
    /// <summary>
    /// A deliberately small block-style YAML reader, sufficient for Columnar-compatible
    /// driver registry indexes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists instead of a YAML library because the assembly is loaded into a
    /// shared, long-lived MSBuild process where any third-party dependency risks an
    /// assembly version conflict with another task.
    /// </para>
    /// <para>
    /// Supported: block mappings, block sequences (including a sequence at the same
    /// indentation as its key), plain and quoted scalars, plain multi-line folded
    /// scalars, literal (<c>|</c>) and folded (<c>&gt;</c>) block scalars with
    /// chomping indicators, and comments.
    /// </para>
    /// <para>
    /// Deliberately unsupported, and reported as an error rather than guessed at:
    /// flow collections (<c>{}</c>/<c>[]</c>), anchors, aliases, tags, directives,
    /// complex keys, and multiple documents. Mis-parsing a registry index would
    /// select the wrong bytes to download, so ambiguity must fail the build.
    /// </para>
    /// </remarks>
    internal static class YamlParser
    {
        public static YamlNode Parse(string text) => Parse(text, null);

        public static YamlNode Parse(string text, string? sourceName)
        {
            if (text is null) throw new ArgumentNullException(nameof(text));

            Reader reader = new Reader(SplitLines(text), sourceName);
            if (!reader.TryPeek(out Line first))
            {
                return YamlNode.Null;
            }

            if (first.Indent != 0)
            {
                throw reader.Error(first.Number, "The first line of the document must not be indented.");
            }

            YamlNode result = ParseNode(reader, 0);

            if (reader.TryPeek(out Line trailing))
            {
                throw reader.Error(trailing.Number, "Unexpected content after the end of the document.");
            }

            return result;
        }

        internal static string[] SplitLines(string text) =>
            text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private static YamlNode ParseNode(Reader reader, int indent)
        {
            if (!reader.TryPeek(out Line line) || line.Indent < indent)
            {
                return YamlNode.Null;
            }

            return IsSequenceEntry(line.Text)
                ? ParseSequence(reader, indent)
                : ParseMapping(reader, indent);
        }

        private static YamlNode ParseSequence(Reader reader, int indent)
        {
            List<YamlNode> items = new List<YamlNode>();
            int startLine = 0;

            while (reader.TryPeek(out Line line) && line.Indent == indent && IsSequenceEntry(line.Text))
            {
                if (startLine == 0)
                {
                    startLine = line.Number;
                }

                string afterDash = line.Text.Substring(1);
                if (afterDash.Trim().Length == 0)
                {
                    // "-" alone: the entry body is on the following, deeper lines.
                    reader.Consume();
                    if (reader.TryPeek(out Line nested) && nested.Indent > indent)
                    {
                        items.Add(ParseNode(reader, nested.Indent));
                    }
                    else
                    {
                        items.Add(YamlNode.Null);
                    }

                    continue;
                }

                int extraSpaces = CountLeadingSpaces(afterDash);
                int bodyIndent = indent + 1 + extraSpaces;
                string body = afterDash.Substring(extraSpaces);

                if (LooksLikeMappingKey(body))
                {
                    // "- key: value": rewrite the dash as whitespace so the body can be
                    // re-parsed as an ordinary node whose indentation is the column the
                    // body actually starts at. This makes nested sequences of mappings
                    // fall out of the same code path as top-level mappings.
                    reader.RewritePeeked(new string(' ', bodyIndent) + body);
                    items.Add(ParseNode(reader, bodyIndent));
                }
                else
                {
                    // "- scalar", as used by the registry index's "urls" lists. Note that
                    // a URL contains a colon but is not a key, because the colon is not
                    // followed by whitespace.
                    int number = line.Number;
                    reader.Consume();
                    items.Add(YamlNode.ForScalar(ReadInlineScalar(reader, indent, number, body), number));
                }
            }

            if (reader.TryPeek(out Line stray) && stray.Indent > indent)
            {
                throw reader.Error(stray.Number, $"Unexpected indentation; expected a sequence entry at column {indent + 1}.");
            }

            return YamlNode.ForSequence(items, startLine);
        }

        private static YamlNode ParseMapping(Reader reader, int indent)
        {
            List<KeyValuePair<string, YamlNode>> entries = new List<KeyValuePair<string, YamlNode>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            int startLine = 0;

            while (reader.TryPeek(out Line line) && line.Indent == indent && !IsSequenceEntry(line.Text))
            {
                if (startLine == 0)
                {
                    startLine = line.Number;
                }

                SplitKey(reader, line, out string key, out string rest);
                if (!seen.Add(key))
                {
                    throw reader.Error(line.Number, $"Duplicate key '{key}'.");
                }

                reader.Consume();
                entries.Add(new KeyValuePair<string, YamlNode>(key, ParseValue(reader, indent, line.Number, rest)));
            }

            if (reader.TryPeek(out Line stray) && stray.Indent > indent)
            {
                throw reader.Error(stray.Number, $"Unexpected indentation; expected a key at column {indent + 1}.");
            }

            return YamlNode.ForMapping(entries, startLine);
        }

        private static YamlNode ParseValue(Reader reader, int keyIndent, int keyLine, string rest)
        {
            if (rest.Length > 0 && (rest[0] == '|' || rest[0] == '>'))
            {
                return YamlNode.ForScalar(ReadBlockScalar(reader, keyIndent, keyLine, rest), keyLine);
            }

            if (rest.Length > 0)
            {
                return YamlNode.ForScalar(ReadInlineScalar(reader, keyIndent, keyLine, rest), keyLine);
            }

            if (reader.TryPeek(out Line next))
            {
                // A block sequence may sit at the same indentation as its key.
                if (next.Indent == keyIndent && IsSequenceEntry(next.Text))
                {
                    return ParseSequence(reader, keyIndent);
                }

                if (next.Indent > keyIndent)
                {
                    if (IsSequenceEntry(next.Text) || LooksLikeMappingKey(next.Text))
                    {
                        return ParseNode(reader, next.Indent);
                    }

                    // "key:" with nothing after the colon, and the value on the following
                    // more-indented lines: a plain multi-line scalar whose first line is
                    // empty. The public registry index wraps long package URLs this way.
                    int number = next.Number;
                    reader.Consume();
                    return YamlNode.ForScalar(ReadInlineScalar(reader, keyIndent, number, next.Text), number);
                }
            }

            return YamlNode.Null;
        }

        private static string? ReadInlineScalar(Reader reader, int keyIndent, int keyLine, string rest)
        {
            string value;
            if (rest[0] == '"' || rest[0] == '\'')
            {
                value = UnquoteScalar(reader, keyLine, rest);
            }
            else
            {
                RejectUnsupportedScalar(reader, keyLine, rest);
                StringBuilder folded = new StringBuilder(StripComment(rest).TrimEnd());

                // Plain multi-line scalar: any deeper-indented following line continues
                // the value and folds into a single space.
                while (reader.TryPeek(out Line cont) && cont.Indent > keyIndent)
                {
                    if (IsSequenceEntry(cont.Text))
                    {
                        throw reader.Error(cont.Number, "A sequence entry cannot continue a scalar value.");
                    }

                    reader.Consume();
                    string piece = StripComment(cont.Text).Trim();
                    if (piece.Length == 0)
                    {
                        continue;
                    }

                    if (folded.Length > 0)
                    {
                        folded.Append(' ');
                    }

                    folded.Append(piece);
                }

                value = folded.ToString();
            }

            return IsNullLiteral(value) ? null : value;
        }

        private static string ReadBlockScalar(Reader reader, int keyIndent, int keyLine, string rest)
        {
            char style = rest[0];
            string indicators = StripComment(rest.Substring(1)).Trim();
            char chomp = ' ';
            foreach (char c in indicators)
            {
                if (c == '-' || c == '+')
                {
                    chomp = c;
                }
                else if (c >= '1' && c <= '9')
                {
                    throw reader.Error(keyLine, "Explicit block scalar indentation indicators are not supported.");
                }
                else
                {
                    throw reader.Error(keyLine, $"Unrecognized block scalar indicator '{c}'.");
                }
            }

            List<string> lines = new List<string>();
            int blockIndent = -1;

            while (reader.TryPeekRaw(out string raw, out int number))
            {
                if (raw.Trim().Length == 0)
                {
                    lines.Add(string.Empty);
                    reader.ConsumeRaw();
                    continue;
                }

                RejectTabIndentation(reader, raw, number);
                int indent = CountLeadingSpaces(raw);
                if (indent <= keyIndent)
                {
                    break;
                }

                if (blockIndent < 0)
                {
                    blockIndent = indent;
                }
                else if (indent < blockIndent)
                {
                    break;
                }

                lines.Add(raw.Substring(blockIndent));
                reader.ConsumeRaw();
            }

            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            StringBuilder text = new StringBuilder();
            if (style == '|')
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    if (i > 0) text.Append('\n');
                    text.Append(lines[i]);
                }
            }
            else
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Length == 0)
                    {
                        text.Append('\n');
                    }
                    else
                    {
                        if (i > 0 && lines[i - 1].Length != 0)
                        {
                            text.Append(' ');
                        }

                        text.Append(lines[i]);
                    }
                }
            }

            if (chomp != '-' && lines.Count > 0)
            {
                text.Append('\n');
            }

            return text.ToString();
        }

        private static void SplitKey(Reader reader, Line line, out string key, out string rest)
        {
            string text = line.Text;
            if (text.Length > 0 && (text[0] == '?' || text[0] == '['))
            {
                throw reader.Error(line.Number, "Complex and flow-style keys are not supported.");
            }

            int i = 0;
            if (text[0] == '"' || text[0] == '\'')
            {
                int close = FindClosingQuote(reader, line.Number, text, 0);
                key = UnquoteScalar(reader, line.Number, text.Substring(0, close + 1));
                i = close + 1;
                while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
                {
                    i++;
                }
            }
            else
            {
                int colon = -1;
                for (int j = 0; j < text.Length; j++)
                {
                    if (text[j] == ':' && (j + 1 == text.Length || text[j + 1] == ' ' || text[j + 1] == '\t'))
                    {
                        colon = j;
                        break;
                    }
                }

                if (colon < 0)
                {
                    throw reader.Error(
                        line.Number,
                        "Expected 'key: value'. Flow-style collections and bare scalars are not supported at this position.");
                }

                key = text.Substring(0, colon).TrimEnd();
                i = colon;
            }

            if (i >= text.Length || text[i] != ':')
            {
                throw reader.Error(line.Number, "Expected ':' after the key.");
            }

            rest = text.Substring(i + 1).TrimStart(' ', '\t');
            if (rest.StartsWith("#", StringComparison.Ordinal))
            {
                rest = string.Empty;
            }

            if (key.Length == 0)
            {
                throw reader.Error(line.Number, "Empty keys are not supported.");
            }
        }

        private static void RejectUnsupportedScalar(Reader reader, int line, string value)
        {
            switch (value[0])
            {
                case '{':
                case '[':
                    throw reader.Error(line, "Flow-style collections are not supported.");
                case '&':
                    throw reader.Error(line, "Anchors are not supported.");
                case '*':
                    throw reader.Error(line, "Aliases are not supported.");
                case '!':
                    throw reader.Error(line, "Tags are not supported.");
                case '`':
                case '@':
                    throw reader.Error(line, $"'{value[0]}' is reserved and cannot start a plain scalar.");
                default:
                    break;
            }
        }

        private static bool IsNullLiteral(string value) =>
            value.Length == 0
            || string.Equals(value, "~", StringComparison.Ordinal)
            || string.Equals(value, "null", StringComparison.Ordinal)
            || string.Equals(value, "Null", StringComparison.Ordinal)
            || string.Equals(value, "NULL", StringComparison.Ordinal);

        private static string StripComment(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '#' && (i == 0 || text[i - 1] == ' ' || text[i - 1] == '\t'))
                {
                    return text.Substring(0, i);
                }
            }

            return text;
        }

        private static string UnquoteScalar(Reader reader, int line, string text)
        {
            char quote = text[0];
            int close = FindClosingQuote(reader, line, text, 0);
            string trailing = StripComment(text.Substring(close + 1)).Trim();
            if (trailing.Length != 0)
            {
                throw reader.Error(line, "Unexpected content after a quoted scalar.");
            }

            string inner = text.Substring(1, close - 1);
            if (quote == '\'')
            {
                return inner.Replace("''", "'");
            }

            StringBuilder builder = new StringBuilder(inner.Length);
            for (int i = 0; i < inner.Length; i++)
            {
                if (inner[i] != '\\')
                {
                    builder.Append(inner[i]);
                    continue;
                }

                i++;
                if (i >= inner.Length)
                {
                    throw reader.Error(line, "Trailing escape character in a quoted scalar.");
                }

                switch (inner[i])
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case '0': builder.Append('\0'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case ' ': builder.Append(' '); break;
                    case 'u':
                        if (i + 4 >= inner.Length
                            || !int.TryParse(
                                inner.Substring(i + 1, 4),
                                NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture,
                                out int code))
                        {
                            throw reader.Error(line, "Malformed \\u escape in a quoted scalar.");
                        }

                        builder.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw reader.Error(line, $"Unsupported escape '\\{inner[i]}' in a quoted scalar.");
                }
            }

            return builder.ToString();
        }

        private static int FindClosingQuote(Reader reader, int line, string text, int start)
        {
            char quote = text[start];
            for (int i = start + 1; i < text.Length; i++)
            {
                if (text[i] == '\\' && quote == '"')
                {
                    i++;
                    continue;
                }

                if (text[i] != quote)
                {
                    continue;
                }

                if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                return i;
            }

            throw reader.Error(line, "Unterminated quoted scalar. Multi-line quoted scalars are not supported.");
        }

        /// <summary>
        /// Distinguishes a sequence entry that starts a mapping ("- name: x") from one
        /// that is a plain scalar ("- https://example.com/"). A colon only introduces a
        /// key when it is followed by whitespace or ends the line, which is what keeps
        /// URLs from being misread as keys.
        /// </summary>
        private static bool LooksLikeMappingKey(string text)
        {
            if (text.Length == 0)
            {
                return false;
            }

            char first = text[0];
            if (first == '"' || first == '\'')
            {
                int i = 1;
                while (i < text.Length)
                {
                    if (first == '"' && text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == first)
                    {
                        if (first == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        break;
                    }

                    i++;
                }

                if (i >= text.Length)
                {
                    return false;
                }

                int after = i + 1;
                while (after < text.Length && (text[after] == ' ' || text[after] == '\t'))
                {
                    after++;
                }

                return after < text.Length && text[after] == ':';
            }

            bool inSingle = false;
            bool inDouble = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (inDouble)
                {
                    if (c == '\\')
                    {
                        i++;
                    }
                    else if (c == '"')
                    {
                        inDouble = false;
                    }

                    continue;
                }

                if (inSingle)
                {
                    if (c == '\'')
                    {
                        inSingle = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inDouble = true;
                }
                else if (c == '\'')
                {
                    inSingle = true;
                }
                else if (c == '#' && (i == 0 || text[i - 1] == ' ' || text[i - 1] == '\t'))
                {
                    return false;
                }
                else if (c == ':' && (i + 1 == text.Length || text[i + 1] == ' ' || text[i + 1] == '\t'))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSequenceEntry(string text) =>
            text.Length > 0
            && text[0] == '-'
            && (text.Length == 1 || text[1] == ' ' || text[1] == '\t');

        private static int CountLeadingSpaces(string text)
        {
            int i = 0;
            while (i < text.Length && text[i] == ' ')
            {
                i++;
            }

            return i;
        }

        private static void RejectTabIndentation(Reader reader, string raw, int number)
        {
            int i = 0;
            while (i < raw.Length && raw[i] == ' ')
            {
                i++;
            }

            if (i < raw.Length && raw[i] == '\t')
            {
                throw reader.Error(number, "Tabs cannot be used for indentation.");
            }
        }

        private readonly struct Line
        {
            public Line(int indent, string text, int number)
            {
                Indent = indent;
                Text = text;
                Number = number;
            }

            public int Indent { get; }

            /// <summary>Line content with indentation removed.</summary>
            public string Text { get; }

            /// <summary>1-based line number.</summary>
            public int Number { get; }
        }

        /// <summary>
        /// Cursor over the document's lines. Sequence entries are rewritten in place
        /// (see <see cref="RewritePeeked"/>), so the backing array is mutable.
        /// </summary>
        private sealed class Reader
        {
            private readonly string[] _lines;
            private readonly string? _sourceName;
            private int _position;
            private int _peeked = -1;

            public Reader(string[] lines, string? sourceName)
            {
                _lines = lines;
                _sourceName = sourceName;
            }

            public bool TryPeek(out Line line)
            {
                for (int i = _position; i < _lines.Length; i++)
                {
                    string raw = _lines[i];
                    if (IsInsignificant(raw))
                    {
                        continue;
                    }

                    RejectTabIndentation(this, raw, i + 1);
                    int indent = CountLeadingSpaces(raw);
                    _peeked = i;
                    line = new Line(indent, raw.Substring(indent).TrimEnd(), i + 1);
                    return true;
                }

                _peeked = -1;
                line = default;
                return false;
            }

            public void Consume()
            {
                if (_peeked < 0)
                {
                    throw new InvalidOperationException("Consume() requires a preceding successful TryPeek().");
                }

                _position = _peeked + 1;
                _peeked = -1;
            }

            public void RewritePeeked(string text)
            {
                if (_peeked < 0)
                {
                    throw new InvalidOperationException("RewritePeeked() requires a preceding successful TryPeek().");
                }

                _lines[_peeked] = text;
            }

            public bool TryPeekRaw(out string raw, out int number)
            {
                if (_position < _lines.Length)
                {
                    raw = _lines[_position];
                    number = _position + 1;
                    return true;
                }

                raw = string.Empty;
                number = _position + 1;
                return false;
            }

            public void ConsumeRaw()
            {
                _position++;
                _peeked = -1;
            }

            public YamlParseException Error(int line, string message) =>
                new YamlParseException(line, message, _sourceName);

            private static bool IsInsignificant(string raw)
            {
                string trimmed = raw.Trim();
                return trimmed.Length == 0
                    || trimmed[0] == '#'
                    || string.Equals(trimmed, "---", StringComparison.Ordinal)
                    || string.Equals(trimmed, "...", StringComparison.Ordinal);
            }
        }
    }
}
