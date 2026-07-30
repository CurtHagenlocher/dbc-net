using System;
using System.Globalization;
using System.Text;

namespace Adbc.Drivers.Build.Util
{
    /// <summary>
    /// Lowercase hex conversion and constant-shape comparison for content hashes.
    /// </summary>
    internal static class Hex
    {
        public static string ToLowerHex(byte[] bytes)
        {
            if (bytes is null) throw new ArgumentNullException(nameof(bytes));

            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        public static bool TryParse(string? text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (string.IsNullOrEmpty(text) || text!.Length % 2 != 0)
            {
                return false;
            }

            byte[] result = new byte[text.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int hi = FromHexDigit(text[i * 2]);
                int lo = FromHexDigit(text[(i * 2) + 1]);
                if (hi < 0 || lo < 0)
                {
                    return false;
                }

                result[i] = (byte)((hi << 4) | lo);
            }

            bytes = result;
            return true;
        }

        /// <summary>
        /// Compares two hex digests. Hashes are public values, so this only needs
        /// to be correct, not timing-safe; it is case- and whitespace-insensitive
        /// because lock files are hand-edited.
        /// </summary>
        public static bool DigestEquals(string? left, string? right)
        {
            if (left is null || right is null)
            {
                return false;
            }

            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static int FromHexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return (c - 'a') + 10;
            if (c >= 'A' && c <= 'F') return (c - 'A') + 10;
            return -1;
        }
    }
}
