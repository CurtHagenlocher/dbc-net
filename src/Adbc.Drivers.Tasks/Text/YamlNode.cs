using System;
using System.Collections.Generic;
using System.Linq;

namespace Adbc.Drivers.Build.Text
{
    internal enum YamlKind
    {
        Null,
        Scalar,
        Sequence,
        Mapping,
    }

    /// <summary>
    /// A node in a parsed YAML document. Only the block-style subset produced by
    /// Columnar-compatible driver registries is representable; see
    /// <see cref="YamlParser"/>.
    /// </summary>
    internal sealed class YamlNode
    {
        private static readonly YamlNode NullNode = new YamlNode(YamlKind.Null, null, null, null, 0);

        private readonly IReadOnlyList<YamlNode>? _items;
        private readonly IReadOnlyList<KeyValuePair<string, YamlNode>>? _entries;

        private YamlNode(
            YamlKind kind,
            string? scalar,
            IReadOnlyList<YamlNode>? items,
            IReadOnlyList<KeyValuePair<string, YamlNode>>? entries,
            int line)
        {
            Kind = kind;
            Scalar = scalar;
            _items = items;
            _entries = entries;
            Line = line;
        }

        public YamlKind Kind { get; }

        public string? Scalar { get; }

        /// <summary>1-based line number the node started on, for diagnostics.</summary>
        public int Line { get; }

        public bool IsNull => Kind == YamlKind.Null;

        public static YamlNode Null => NullNode;

        public static YamlNode ForScalar(string? value, int line) =>
            new YamlNode(YamlKind.Scalar, value, null, null, line);

        public static YamlNode ForSequence(IReadOnlyList<YamlNode> items, int line) =>
            new YamlNode(YamlKind.Sequence, null, items, null, line);

        public static YamlNode ForMapping(IReadOnlyList<KeyValuePair<string, YamlNode>> entries, int line) =>
            new YamlNode(YamlKind.Mapping, null, null, entries, line);

        public IReadOnlyList<YamlNode> Items =>
            _items ?? (IReadOnlyList<YamlNode>)Array.Empty<YamlNode>();

        public IReadOnlyList<KeyValuePair<string, YamlNode>> Entries =>
            _entries ?? (IReadOnlyList<KeyValuePair<string, YamlNode>>)Array.Empty<KeyValuePair<string, YamlNode>>();

        /// <summary>Child of a mapping, or <see cref="Null"/> when absent.</summary>
        public YamlNode this[string key]
        {
            get
            {
                if (_entries is null)
                {
                    return NullNode;
                }

                foreach (KeyValuePair<string, YamlNode> entry in _entries)
                {
                    if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                    {
                        return entry.Value;
                    }
                }

                return NullNode;
            }
        }

        public bool ContainsKey(string key) =>
            _entries is not null && _entries.Any(e => string.Equals(e.Key, key, StringComparison.Ordinal));

        /// <summary>
        /// Scalar text, or <see langword="null"/> for absent/null nodes. Throws when
        /// the node is a collection, so a registry shape change surfaces as an error
        /// rather than a silently empty value.
        /// </summary>
        public string? AsString()
        {
            switch (Kind)
            {
                case YamlKind.Null:
                    return null;
                case YamlKind.Scalar:
                    return Scalar;
                default:
                    throw new YamlParseException(Line, $"Expected a scalar but found a {Kind.ToString().ToLowerInvariant()}.");
            }
        }

        /// <summary>
        /// Sequence items. A missing node yields an empty list; a scalar is an error.
        /// </summary>
        public IReadOnlyList<YamlNode> AsSequence()
        {
            switch (Kind)
            {
                case YamlKind.Null:
                    return Array.Empty<YamlNode>();
                case YamlKind.Sequence:
                    return Items;
                default:
                    throw new YamlParseException(Line, $"Expected a sequence but found a {Kind.ToString().ToLowerInvariant()}.");
            }
        }

        public override string ToString() => Kind switch
        {
            YamlKind.Null => "(null)",
            YamlKind.Scalar => Scalar ?? "(empty)",
            YamlKind.Sequence => $"[{Items.Count} items]",
            _ => $"{{{Entries.Count} keys}}",
        };
    }

    internal sealed class YamlParseException : Exception
    {
        public YamlParseException(int line, string message)
            : base(FormatMessage(line, message, null))
        {
            Line = line;
        }

        public YamlParseException(int line, string message, string? sourceName)
            : base(FormatMessage(line, message, sourceName))
        {
            Line = line;
            SourceName = sourceName;
        }

        public int Line { get; }

        public string? SourceName { get; }

        private static string FormatMessage(int line, string message, string? source)
        {
            string location = source is null ? $"line {line}" : $"{source}({line})";
            return $"{location}: {message}";
        }
    }
}
