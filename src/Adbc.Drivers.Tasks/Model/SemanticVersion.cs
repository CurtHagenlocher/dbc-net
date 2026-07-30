using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Adbc.Drivers.Build.Model
{
    /// <summary>
    /// A semantic version, tolerant of the inconsistencies present in real driver
    /// registries.
    /// </summary>
    /// <remarks>
    /// The public Columnar index mixes bare and <c>v</c>-prefixed versions for the same
    /// driver (for example <c>1.9.0</c> and <c>v1.10.0</c> both appear under
    /// <c>snowflake</c>), so the prefix is accepted and stripped. The original spelling
    /// is preserved in <see cref="Original"/> because it appears in package URLs.
    /// </remarks>
    internal sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
    {
        private SemanticVersion(int major, int minor, int patch, string[] prerelease, string? build, string original)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            PrereleaseIdentifiers = prerelease;
            Build = build;
            Original = original;
        }

        public int Major { get; }

        public int Minor { get; }

        public int Patch { get; }

        public IReadOnlyList<string> PrereleaseIdentifiers { get; }

        public string? Build { get; }

        /// <summary>The version exactly as it appeared in its source.</summary>
        public string Original { get; }

        public bool IsPrerelease => PrereleaseIdentifiers.Count > 0;

        public static bool TryParse(string? text, out SemanticVersion? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string original = text!.Trim();
            string remaining = original;
            if (remaining.Length > 1 && (remaining[0] == 'v' || remaining[0] == 'V'))
            {
                remaining = remaining.Substring(1);
            }

            string? build = null;
            int plus = remaining.IndexOf('+');
            if (plus >= 0)
            {
                build = remaining.Substring(plus + 1);
                remaining = remaining.Substring(0, plus);
                if (build.Length == 0)
                {
                    return false;
                }
            }

            string[] prerelease = Array.Empty<string>();
            int dash = remaining.IndexOf('-');
            if (dash >= 0)
            {
                string prereleaseText = remaining.Substring(dash + 1);
                remaining = remaining.Substring(0, dash);
                if (prereleaseText.Length == 0)
                {
                    return false;
                }

                prerelease = prereleaseText.Split('.');
                foreach (string identifier in prerelease)
                {
                    if (identifier.Length == 0)
                    {
                        return false;
                    }
                }
            }

            string[] parts = remaining.Split('.');
            if (parts.Length == 0 || parts.Length > 3)
            {
                return false;
            }

            int[] numbers = new int[3];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!TryParseComponent(parts[i], out numbers[i]))
                {
                    return false;
                }
            }

            version = new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease, build, original);
            return true;
        }

        public static SemanticVersion Parse(string text)
        {
            if (!TryParse(text, out SemanticVersion? version))
            {
                throw new FormatException($"'{text}' is not a recognized version.");
            }

            return version!;
        }

        public int CompareTo(SemanticVersion? other)
        {
            if (other is null)
            {
                return 1;
            }

            int result = Major.CompareTo(other.Major);
            if (result != 0) return result;

            result = Minor.CompareTo(other.Minor);
            if (result != 0) return result;

            result = Patch.CompareTo(other.Patch);
            if (result != 0) return result;

            // A release outranks any prerelease of the same core version.
            if (PrereleaseIdentifiers.Count == 0 && other.PrereleaseIdentifiers.Count == 0) return 0;
            if (PrereleaseIdentifiers.Count == 0) return 1;
            if (other.PrereleaseIdentifiers.Count == 0) return -1;

            int shared = Math.Min(PrereleaseIdentifiers.Count, other.PrereleaseIdentifiers.Count);
            for (int i = 0; i < shared; i++)
            {
                result = ComparePrereleaseIdentifier(PrereleaseIdentifiers[i], other.PrereleaseIdentifiers[i]);
                if (result != 0) return result;
            }

            return PrereleaseIdentifiers.Count.CompareTo(other.PrereleaseIdentifiers.Count);
        }

        public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;

        public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

        public override int GetHashCode()
        {
            int hash = Major;
            hash = (hash * 397) ^ Minor;
            hash = (hash * 397) ^ Patch;
            foreach (string identifier in PrereleaseIdentifiers)
            {
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(identifier);
            }

            return hash;
        }

        /// <summary>Canonical form: no <c>v</c> prefix, no build metadata.</summary>
        public string ToNormalizedString()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(Major.ToString(CultureInfo.InvariantCulture)).Append('.');
            builder.Append(Minor.ToString(CultureInfo.InvariantCulture)).Append('.');
            builder.Append(Patch.ToString(CultureInfo.InvariantCulture));
            if (PrereleaseIdentifiers.Count > 0)
            {
                builder.Append('-').Append(string.Join(".", PrereleaseIdentifiers));
            }

            return builder.ToString();
        }

        public override string ToString() => ToNormalizedString();

        private static bool TryParseComponent(string text, out int value)
        {
            value = 0;
            if (text.Length == 0 || (text.Length > 1 && text[0] == '0'))
            {
                // Leading zeros are not valid semver and usually indicate a typo.
                return false;
            }

            foreach (char c in text)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static int ComparePrereleaseIdentifier(string left, string right)
        {
            bool leftNumeric = IsNumeric(left);
            bool rightNumeric = IsNumeric(right);

            if (leftNumeric && rightNumeric)
            {
                // Numeric identifiers can exceed int range in principle; compare by
                // length first to avoid overflow.
                string l = left.TrimStart('0');
                string r = right.TrimStart('0');
                if (l.Length != r.Length)
                {
                    return l.Length.CompareTo(r.Length);
                }

                return string.CompareOrdinal(l, r);
            }

            if (leftNumeric) return -1;
            if (rightNumeric) return 1;

            return string.CompareOrdinal(left, right);
        }

        private static bool IsNumeric(string text)
        {
            foreach (char c in text)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return text.Length > 0;
        }
    }
}
