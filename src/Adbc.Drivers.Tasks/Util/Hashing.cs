using System;
using System.IO;
using System.Security.Cryptography;

namespace Adbc.Drivers.Build.Util
{
    internal static class Hashing
    {
        private const int BufferSize = 128 * 1024;

        public static string Sha256File(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
            {
                return Sha256Stream(stream);
            }
        }

        public static string Sha256Stream(Stream stream)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Hex.ToLowerHex(sha.ComputeHash(stream));
            }
        }

        public static string Sha256Bytes(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Hex.ToLowerHex(sha.ComputeHash(bytes));
            }
        }

        /// <summary>
        /// Copies <paramref name="source"/> to <paramref name="destination"/> while
        /// hashing the bytes that actually landed, so a download is never hashed by
        /// re-reading a file that something else could have replaced.
        /// </summary>
        public static string CopyAndHash(Stream source, Stream destination, long maxBytes)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (destination is null) throw new ArgumentNullException(nameof(destination));

            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[BufferSize];
                long total = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new InvalidDataException(
                            $"Download exceeded the {maxBytes} byte limit for a single driver archive.");
                    }

                    sha.TransformBlock(buffer, 0, read, null, 0);
                    destination.Write(buffer, 0, read);
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Hex.ToLowerHex(sha.Hash!);
            }
        }
    }
}
