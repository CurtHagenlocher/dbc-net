using System;
using System.IO;

namespace Adbc.Drivers.Build.Tests.TestSupport
{
    /// <summary>
    /// A scratch directory removed when the test finishes.
    /// </summary>
    /// <remarks>
    /// Deliberately self-contained rather than reusing the production path helpers, so
    /// that this file can also be linked into the integration test project, which must
    /// reach the product only through the packed NuGet package.
    /// </remarks>
    internal sealed class TempDirectory : IDisposable
    {
        public TempDirectory(string? prefix = null)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "adbc-tests",
                (prefix ?? "t") + "-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Combine(params string[] parts)
        {
            string result = Path;
            foreach (string part in parts)
            {
                result = System.IO.Path.Combine(result, part);
            }

            return result;
        }

        public string CreateSubdirectory(string name)
        {
            string path = Combine(name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                DeleteRecursive(Path);
            }
            catch (IOException)
            {
                // A file left open by a failing test must not mask the real failure.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Cached driver payloads are deliberately marked read-only, so the attribute has
        /// to be cleared before the tree can be removed.
        /// </summary>
        internal static void DeleteRecursive(string path)
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
    }
}
