using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Adbc.Drivers.Build.Tests.TestSupport
{
    /// <summary>
    /// Builds ustar archives, including deliberately malformed ones.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than shelling out to <c>tar</c> so the hostile-archive
    /// tests can produce entries no real archiver would emit: absolute paths, <c>..</c>
    /// components, symlinks, duplicates, and lying size headers.
    /// </remarks>
    internal sealed class TarGzBuilder
    {
        private const int BlockSize = 512;

        private readonly MemoryStream _tar = new MemoryStream();

        public TarGzBuilder AddFile(string name, byte[] content, int mode = 0b110_100_100)
        {
            WriteHeader(name, content.Length, '0', null, mode);
            _tar.Write(content, 0, content.Length);
            WritePadding(content.Length);
            return this;
        }

        public TarGzBuilder AddFile(string name, string content) =>
            AddFile(name, Encoding.UTF8.GetBytes(content));

        public TarGzBuilder AddDirectory(string name)
        {
            WriteHeader(name.EndsWith("/", StringComparison.Ordinal) ? name : name + "/", 0, '5', null, 0b111_101_101);
            return this;
        }

        public TarGzBuilder AddSymbolicLink(string name, string target)
        {
            WriteHeader(name, 0, '2', target, 0b111_111_111);
            return this;
        }

        public TarGzBuilder AddHardLink(string name, string target)
        {
            WriteHeader(name, 0, '1', target, 0b110_100_100);
            return this;
        }

        public TarGzBuilder AddEntryWithTypeFlag(string name, char typeFlag)
        {
            WriteHeader(name, 0, typeFlag, null, 0b110_100_100);
            return this;
        }

        /// <summary>Declares one size in the header but writes different content.</summary>
        public TarGzBuilder AddFileWithLyingSize(string name, byte[] content, long declaredSize)
        {
            WriteHeader(name, declaredSize, '0', null, 0b110_100_100);
            _tar.Write(content, 0, content.Length);
            WritePadding(content.Length);
            return this;
        }

        public byte[] ToTar()
        {
            byte[] body = _tar.ToArray();
            using (MemoryStream output = new MemoryStream())
            {
                output.Write(body, 0, body.Length);

                // Two zero blocks terminate the archive.
                byte[] trailer = new byte[BlockSize * 2];
                output.Write(trailer, 0, trailer.Length);
                return output.ToArray();
            }
        }

        public byte[] ToTarGz()
        {
            byte[] tar = ToTar();
            using (MemoryStream output = new MemoryStream())
            {
                using (GZipStream gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                {
                    gzip.Write(tar, 0, tar.Length);
                }

                return output.ToArray();
            }
        }

        public string WriteTarGz(string path)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            File.WriteAllBytes(path, ToTarGz());
            return path;
        }

        /// <summary>A well-formed driver package: MANIFEST, driver, signature, LICENSE, NOTICE.</summary>
        public static TarGzBuilder CreateDriverPackage(
            string driverFileName,
            string driverContent,
            string manifestName = "Fixture ADBC Driver",
            string version = "v1.11.0",
            string? adbcVersion = "v1.1.0",
            string? entrypoint = null,
            bool includeSignature = true,
            string? manifestVersionLine = null)
        {
            StringBuilder manifest = new StringBuilder();
            manifest.Append("# Fixture package manifest\n");
            if (manifestVersionLine is not null)
            {
                manifest.Append(manifestVersionLine).Append('\n');
            }

            manifest.Append("name = ").Append(Quote(manifestName)).Append('\n');
            manifest.Append("publisher = \"Fixture Publisher\"\n");
            manifest.Append("license = \"Apache-2.0\"\n");
            manifest.Append("version = ").Append(Quote(version)).Append('\n');

            if (adbcVersion is not null)
            {
                manifest.Append("\n[ADBC]\nversion = ").Append(Quote(adbcVersion)).Append('\n');
            }

            if (entrypoint is not null)
            {
                manifest.Append("\n[Driver]\nentrypoint = ").Append(Quote(entrypoint)).Append('\n');
            }

            manifest.Append("\n[Files]\n");
            manifest.Append("driver = ").Append(Quote(driverFileName)).Append('\n');
            if (includeSignature)
            {
                manifest.Append("signature = ").Append(Quote(driverFileName + ".sig")).Append('\n');
            }

            TarGzBuilder builder = new TarGzBuilder()
                .AddFile("MANIFEST", manifest.ToString())
                .AddFile(driverFileName, driverContent)
                .AddFile("LICENSE", "Apache License 2.0 (fixture)")
                .AddFile("NOTICE", "Fixture notice");

            if (includeSignature)
            {
                builder.AddFile(driverFileName + ".sig", "not-a-real-signature");
            }

            return builder;
        }

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private void WritePadding(long written)
        {
            int padding = (int)(((written + BlockSize - 1) / BlockSize * BlockSize) - written);
            if (padding > 0)
            {
                _tar.Write(new byte[padding], 0, padding);
            }
        }

        private void WriteHeader(string name, long size, char typeFlag, string? linkName, int mode)
        {
            byte[] header = new byte[BlockSize];

            WriteString(header, 0, 100, name);
            WriteOctal(header, 100, 8, mode);
            WriteOctal(header, 108, 8, 0);
            WriteOctal(header, 116, 8, 0);
            WriteOctal(header, 124, 12, size);
            WriteOctal(header, 136, 12, 0);

            // Checksum field is spaces while the checksum itself is computed.
            for (int i = 148; i < 156; i++)
            {
                header[i] = (byte)' ';
            }

            header[156] = (byte)typeFlag;
            if (linkName is not null)
            {
                WriteString(header, 157, 100, linkName);
            }

            WriteString(header, 257, 6, "ustar");
            header[263] = (byte)'0';
            header[264] = (byte)'0';
            WriteString(header, 265, 32, "root");
            WriteString(header, 297, 32, "root");

            int checksum = 0;
            foreach (byte b in header)
            {
                checksum += b;
            }

            // Six octal digits, NUL, then a space.
            string text = Convert.ToString(checksum, 8).PadLeft(6, '0');
            for (int i = 0; i < 6; i++)
            {
                header[148 + i] = (byte)text[i];
            }

            header[154] = 0;
            header[155] = (byte)' ';

            _tar.Write(header, 0, header.Length);
        }

        private static void WriteString(byte[] buffer, int offset, int length, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > length)
            {
                throw new ArgumentException($"'{value}' does not fit in {length} bytes.", nameof(value));
            }

            Array.Copy(bytes, 0, buffer, offset, bytes.Length);
        }

        private static void WriteOctal(byte[] buffer, int offset, int length, long value)
        {
            string text = Convert.ToString(value, 8).PadLeft(length - 1, '0');
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            if (bytes.Length > length - 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The value does not fit in the tar field.");
            }

            Array.Copy(bytes, 0, buffer, offset, bytes.Length);
            buffer[offset + length - 1] = 0;
        }
    }
}
