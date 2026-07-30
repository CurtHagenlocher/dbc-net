using System;
using System.Collections.Generic;
using System.Linq;

namespace Adbc.Drivers.Build.Model
{
    /// <summary>
    /// A version constraint, matching the subset <c>dbc</c> accepts: an exact version,
    /// <c>*</c>, comparison operators, caret, and tilde. Multiple comparators separated
    /// by whitespace or commas are combined with AND.
    /// </summary>
    /// <remarks>
    /// Constraints are only evaluated by the explicit resolve step. An ordinary build
    /// reads exact versions out of the lock file and never interprets a range, so a
    /// mutable registry cannot change which bytes a build consumes.
    /// </remarks>
    internal sealed class VersionRange
    {
        private static readonly char[] ComparatorSeparators = { ',', ' ', '\t' };

        private readonly List<Comparator> _comparators;

        private VersionRange(List<Comparator> comparators, bool isAny, bool mentionsPrerelease, string original)
        {
            _comparators = comparators;
            IsAny = isAny;
            MentionsPrerelease = mentionsPrerelease;
            Original = original;
        }

        public bool IsAny { get; }

        /// <summary>
        /// True when the constraint itself names a prerelease, which opts that
        /// resolution into prerelease candidates without a global flag.
        /// </summary>
        public bool MentionsPrerelease { get; }

        public string Original { get; }

        public static VersionRange Any { get; } = new VersionRange(new List<Comparator>(), true, false, "*");

        public static bool TryParse(string? text, out VersionRange? range, out string? error)
        {
            range = null;
            error = null;

            string trimmed = (text ?? string.Empty).Trim();
            if (trimmed.Length == 0
                || string.Equals(trimmed, "*", StringComparison.Ordinal)
                || string.Equals(trimmed, "latest", StringComparison.OrdinalIgnoreCase))
            {
                range = Any;
                return true;
            }

            if (trimmed.IndexOf("||", StringComparison.Ordinal) >= 0)
            {
                error = "Alternative ('||') version constraints are not supported.";
                return false;
            }

            List<Comparator> comparators = new List<Comparator>();
            bool mentionsPrerelease = false;

            foreach (string piece in trimmed.Split(ComparatorSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParseComparator(piece, comparators, ref mentionsPrerelease, out error))
                {
                    return false;
                }
            }

            if (comparators.Count == 0)
            {
                error = $"'{trimmed}' is not a recognized version constraint.";
                return false;
            }

            range = new VersionRange(comparators, false, mentionsPrerelease, trimmed);
            return true;
        }

        public static VersionRange Parse(string? text)
        {
            if (!TryParse(text, out VersionRange? range, out string? error))
            {
                throw new FormatException(error);
            }

            return range!;
        }

        public bool Satisfies(SemanticVersion version)
        {
            if (version is null) throw new ArgumentNullException(nameof(version));

            if (IsAny)
            {
                return true;
            }

            return _comparators.All(c => c.Satisfies(version));
        }

        /// <summary>
        /// Highest version satisfying the constraint. Prereleases are excluded unless
        /// the constraint names one or <paramref name="allowPrerelease"/> is set, which
        /// mirrors <c>dbc</c>'s <c>--pre</c> behaviour.
        /// </summary>
        public SemanticVersion? SelectBest(IEnumerable<SemanticVersion> candidates, bool allowPrerelease)
        {
            if (candidates is null) throw new ArgumentNullException(nameof(candidates));

            bool includePrerelease = allowPrerelease || MentionsPrerelease;
            SemanticVersion? best = null;

            foreach (SemanticVersion candidate in candidates)
            {
                if (candidate.IsPrerelease && !includePrerelease)
                {
                    continue;
                }

                if (!Satisfies(candidate))
                {
                    continue;
                }

                if (best is null || candidate.CompareTo(best) > 0)
                {
                    best = candidate;
                }
            }

            return best;
        }

        public override string ToString() => Original;

        private static bool TryParseComparator(
            string piece,
            List<Comparator> comparators,
            ref bool mentionsPrerelease,
            out string? error)
        {
            error = null;

            string op = string.Empty;
            int i = 0;
            while (i < piece.Length && (piece[i] == '>' || piece[i] == '<' || piece[i] == '=' || piece[i] == '^' || piece[i] == '~'))
            {
                op += piece[i];
                i++;
            }

            string versionText = piece.Substring(i);
            if (!SemanticVersion.TryParse(versionText, out SemanticVersion? version))
            {
                error = $"'{versionText}' is not a recognized version.";
                return false;
            }

            if (version!.IsPrerelease)
            {
                mentionsPrerelease = true;
            }

            switch (op)
            {
                case "":
                case "=":
                case "==":
                    comparators.Add(new Comparator(ComparatorKind.Equal, version));
                    return true;
                case ">":
                    comparators.Add(new Comparator(ComparatorKind.GreaterThan, version));
                    return true;
                case ">=":
                    comparators.Add(new Comparator(ComparatorKind.GreaterThanOrEqual, version));
                    return true;
                case "<":
                    comparators.Add(new Comparator(ComparatorKind.LessThan, version));
                    return true;
                case "<=":
                    comparators.Add(new Comparator(ComparatorKind.LessThanOrEqual, version));
                    return true;
                case "^":
                    // Compatible-with: allow changes that do not modify the leftmost
                    // non-zero component.
                    comparators.Add(new Comparator(ComparatorKind.GreaterThanOrEqual, version));
                    comparators.Add(new Comparator(ComparatorKind.LessThan, CaretUpperBound(version)));
                    return true;
                case "~":
                    comparators.Add(new Comparator(ComparatorKind.GreaterThanOrEqual, version));
                    comparators.Add(new Comparator(ComparatorKind.LessThan, TildeUpperBound(version)));
                    return true;
                default:
                    error = $"'{op}' is not a supported version comparison operator.";
                    return false;
            }
        }

        private static SemanticVersion CaretUpperBound(SemanticVersion version)
        {
            if (version.Major != 0)
            {
                return SemanticVersion.Parse($"{version.Major + 1}.0.0-0");
            }

            if (version.Minor != 0)
            {
                return SemanticVersion.Parse($"0.{version.Minor + 1}.0-0");
            }

            return SemanticVersion.Parse($"0.0.{version.Patch + 1}-0");
        }

        private static SemanticVersion TildeUpperBound(SemanticVersion version) =>
            SemanticVersion.Parse($"{version.Major}.{version.Minor + 1}.0-0");

        private enum ComparatorKind
        {
            Equal,
            GreaterThan,
            GreaterThanOrEqual,
            LessThan,
            LessThanOrEqual,
        }

        private sealed class Comparator
        {
            private readonly ComparatorKind _kind;
            private readonly SemanticVersion _version;

            public Comparator(ComparatorKind kind, SemanticVersion version)
            {
                _kind = kind;
                _version = version;
            }

            public bool Satisfies(SemanticVersion version)
            {
                int comparison = version.CompareTo(_version);
                return _kind switch
                {
                    ComparatorKind.Equal => comparison == 0,
                    ComparatorKind.GreaterThan => comparison > 0,
                    ComparatorKind.GreaterThanOrEqual => comparison >= 0,
                    ComparatorKind.LessThan => comparison < 0,
                    ComparatorKind.LessThanOrEqual => comparison <= 0,
                    _ => false,
                };
            }
        }
    }
}
