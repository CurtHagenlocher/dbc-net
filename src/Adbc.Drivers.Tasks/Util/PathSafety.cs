using System;
using System.IO;

namespace Adbc.Drivers.Build.Util
{
    internal static class PathSafety
    {
        /// <summary>
        /// Windows paths are compared case-insensitively; other platforms are
        /// case-sensitive. Used for containment checks during extraction.
        /// </summary>
        public static StringComparison PathComparison { get; } =
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static StringComparer PathComparer { get; } =
            Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        public static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            char last = path[path.Length - 1];
            return last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// Reserved DOS device names are rejected on every platform so that an archive
        /// extracted on Linux and one extracted on Windows contain the same files.
        /// </summary>
        public static bool IsReservedFileName(string segment)
        {
            int dot = segment.IndexOf('.');
            string stem = dot < 0 ? segment : segment.Substring(0, dot);
            foreach (string reserved in ReservedNames)
            {
                if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Deletes a directory tree, clearing read-only attributes first. Extracted
        /// driver payloads are marked read-only, so a plain Delete would fail.
        /// </summary>
        public static void DeleteDirectoryRecursive(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileAttributes attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            Directory.Delete(path, recursive: true);
        }

        /// <summary>
        /// <c>Path.GetRelativePath</c> does not exist on the frameworks this assembly
        /// targets.
        /// </summary>
        public static string GetRelativePath(string baseDirectory, string fullPath)
        {
            string root = EnsureTrailingSeparator(Path.GetFullPath(baseDirectory));
            string target = Path.GetFullPath(fullPath);
            if (!target.StartsWith(root, PathComparison))
            {
                return target;
            }

            return target.Substring(root.Length);
        }
    }
}
