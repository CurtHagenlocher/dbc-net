using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using Adbc.Drivers.Build.Util;

namespace Adbc.Drivers.Build.Archives
{
    /// <summary>
    /// Raised when an archive is rejected for safety reasons. Distinct from
    /// <see cref="InvalidDataException"/> so callers can report a hostile archive
    /// differently from a corrupt download.
    /// </summary>
    internal sealed class UnsafeArchiveException : Exception
    {
        public UnsafeArchiveException(string message)
            : base(message)
        {
        }
    }

    internal sealed class ExtractionLimits
    {
        /// <summary>
        /// Generous enough for real drivers (the Snowflake driver alone unpacks to
        /// roughly 60 MB) while still bounding a decompression bomb.
        /// </summary>
        public long MaxTotalBytes { get; set; } = 1024L * 1024 * 1024;

        public long MaxEntryBytes { get; set; } = 768L * 1024 * 1024;

        public int MaxEntryCount { get; set; } = 1024;

        public int MaxPathLength { get; set; } = 200;

        public static ExtractionLimits Default => new ExtractionLimits();
    }

    internal sealed class ExtractedFile
    {
        public ExtractedFile(string relativePath, long length, string sha256)
        {
            RelativePath = relativePath;
            Length = length;
            Sha256 = sha256;
        }

        /// <summary>Forward-slash relative path within the extraction directory.</summary>
        public string RelativePath { get; }

        public long Length { get; }

        public string Sha256 { get; }
    }

    internal static class ArchiveExtractor
    {
        /// <summary>
        /// Extracts a gzip-compressed tar archive into <paramref name="destinationDirectory"/>,
        /// hashing every file as it is written.
        /// </summary>
        /// <remarks>
        /// The destination is expected to be a fresh, private directory that the caller
        /// promotes atomically afterwards; nothing here is safe to point at a shared
        /// output folder.
        /// </remarks>
        public static IReadOnlyList<ExtractedFile> ExtractTarGz(
            string archivePath,
            string destinationDirectory,
            ExtractionLimits? limits = null)
        {
            if (archivePath is null) throw new ArgumentNullException(nameof(archivePath));

            using (FileStream file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024))
            using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                return ExtractTar(gzip, destinationDirectory, limits);
            }
        }

        public static IReadOnlyList<ExtractedFile> ExtractTar(
            Stream tarStream,
            string destinationDirectory,
            ExtractionLimits? limits = null)
        {
            if (tarStream is null) throw new ArgumentNullException(nameof(tarStream));
            if (destinationDirectory is null) throw new ArgumentNullException(nameof(destinationDirectory));

            ExtractionLimits effective = limits ?? ExtractionLimits.Default;
            string root = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(root);

            List<ExtractedFile> files = new List<ExtractedFile>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            int entryCount = 0;

            using (TarReader reader = new TarReader(tarStream))
            {
                TarEntry? entry;
                while ((entry = reader.MoveNext()) is not null)
                {
                    entryCount++;
                    if (entryCount > effective.MaxEntryCount)
                    {
                        throw new UnsafeArchiveException(
                            $"The archive contains more than {effective.MaxEntryCount} entries.");
                    }

                    switch (entry.Type)
                    {
                        case TarEntryType.File:
                        case TarEntryType.Contiguous:
                            break;

                        case TarEntryType.Directory:
                            Directory.CreateDirectory(ResolveEntryPath(root, entry.Name, effective, directory: true));
                            continue;

                        case TarEntryType.SymbolicLink:
                        case TarEntryType.HardLink:
                            // A link can redirect a later write outside the extraction
                            // root, or point the loaded driver at an arbitrary library.
                            throw new UnsafeArchiveException(
                                $"The archive entry '{entry.Name}' is a link, which is not allowed in a driver package.");

                        default:
                            throw new UnsafeArchiveException(
                                $"The archive entry '{entry.Name}' has unsupported type '{DescribeTypeFlag(entry.TypeFlag)}'.");
                    }

                    if (entry.Length > effective.MaxEntryBytes)
                    {
                        throw new UnsafeArchiveException(
                            $"The archive entry '{entry.Name}' declares {entry.Length} bytes, above the {effective.MaxEntryBytes} byte limit.");
                    }

                    string fullPath = ResolveEntryPath(root, entry.Name, effective, directory: false);
                    string relativePath = NormalizeRelative(root, fullPath);

                    if (!seen.Add(relativePath))
                    {
                        // Duplicates let a later entry silently replace a verified one.
                        throw new UnsafeArchiveException(
                            $"The archive contains more than one entry for '{relativePath}'.");
                    }

                    string? parent = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent!);
                    }

                    // Checked against the declared length before writing anything, then
                    // confirmed against the bytes actually produced below, so a lying
                    // header cannot overshoot the budget by more than one entry.
                    if (totalBytes + entry.Length > effective.MaxTotalBytes)
                    {
                        throw new UnsafeArchiveException(
                            $"The archive expands to more than the {effective.MaxTotalBytes} byte limit (reached at '{entry.Name}').");
                    }

                    string hash;
                    using (Stream source = reader.OpenEntry())
                    using (FileStream target = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024))
                    {
                        hash = Hashing.CopyAndHash(source, target, effective.MaxEntryBytes);
                    }

                    long written = new FileInfo(fullPath).Length;
                    if (written != entry.Length)
                    {
                        throw new InvalidDataException(
                            $"The archive entry '{entry.Name}' declared {entry.Length} bytes but produced {written}.");
                    }

                    totalBytes += written;
                    files.Add(new ExtractedFile(relativePath, written, hash));
                }
            }

            if (files.Count == 0)
            {
                throw new InvalidDataException("The archive contains no files.");
            }

            return files;
        }

        private static string DescribeTypeFlag(char typeFlag) =>
            typeFlag == '\0'
                ? "\\0"
                : char.IsControl(typeFlag)
                    ? "0x" + ((int)typeFlag).ToString("x2", CultureInfo.InvariantCulture)
                    : typeFlag.ToString();

        /// <summary>
        /// Maps an archive entry name to an absolute path guaranteed to sit under
        /// <paramref name="root"/>, rejecting anything that tries to escape.
        /// </summary>
        internal static string ResolveEntryPath(string root, string name, ExtractionLimits limits, bool directory)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new UnsafeArchiveException("The archive contains an entry with an empty name.");
            }

            if (name.Length > limits.MaxPathLength)
            {
                throw new UnsafeArchiveException(
                    $"The archive entry name '{Truncate(name)}' is longer than {limits.MaxPathLength} characters.");
            }

            if (name.IndexOf('\0') >= 0)
            {
                throw new UnsafeArchiveException("The archive contains an entry name with an embedded NUL.");
            }

            // Tar uses '/'. A '\' is a legal filename character on Linux, so an entry
            // containing one is either hostile or would silently become a directory
            // separator on Windows.
            if (name.IndexOf('\\') >= 0)
            {
                throw new UnsafeArchiveException(
                    $"The archive entry name '{Truncate(name)}' contains a backslash.");
            }

            string trimmed = directory ? name.TrimEnd('/') : name;
            if (trimmed.Length == 0)
            {
                throw new UnsafeArchiveException("The archive contains an entry naming the root directory.");
            }

            if (trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                throw new UnsafeArchiveException(
                    $"The archive entry name '{Truncate(name)}' is an absolute path.");
            }

            if (trimmed.Length >= 2 && trimmed[1] == ':')
            {
                throw new UnsafeArchiveException(
                    $"The archive entry name '{Truncate(name)}' is a drive-qualified path.");
            }

            string[] segments = trimmed.Split('/');
            foreach (string segment in segments)
            {
                if (segment.Length == 0)
                {
                    throw new UnsafeArchiveException(
                        $"The archive entry name '{Truncate(name)}' contains an empty path segment.");
                }

                if (string.Equals(segment, ".", StringComparison.Ordinal))
                {
                    throw new UnsafeArchiveException(
                        $"The archive entry name '{Truncate(name)}' contains a '.' segment.");
                }

                if (string.Equals(segment, "..", StringComparison.Ordinal))
                {
                    throw new UnsafeArchiveException(
                        $"The archive entry name '{Truncate(name)}' escapes the extraction directory.");
                }

                if (PathSafety.IsReservedFileName(segment))
                {
                    throw new UnsafeArchiveException(
                        $"The archive entry name '{Truncate(name)}' uses the reserved name '{segment}'.");
                }

                foreach (char c in segment)
                {
                    if (c < 0x20 || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
                    {
                        throw new UnsafeArchiveException(
                            $"The archive entry name '{Truncate(name)}' contains the unsupported character '{DescribeTypeFlag(c)}'.");
                    }
                }
            }

            string combined = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar.ToString(), segments)));

            // Belt-and-braces: even with the segment checks above, confirm containment
            // against the canonical path in case of platform-specific normalization.
            string rootWithSeparator = PathSafety.EnsureTrailingSeparator(root);
            if (!combined.StartsWith(rootWithSeparator, PathSafety.PathComparison))
            {
                throw new UnsafeArchiveException(
                    $"The archive entry name '{Truncate(name)}' resolves outside the extraction directory.");
            }

            return combined;
        }

        private static string NormalizeRelative(string root, string fullPath) =>
            fullPath.Substring(PathSafety.EnsureTrailingSeparator(root).Length)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');

        private static string Truncate(string value) =>
            value.Length <= 120 ? value : value.Substring(0, 117) + "...";
    }
}
