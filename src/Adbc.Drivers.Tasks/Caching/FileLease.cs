using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Adbc.Drivers.Build.Caching
{
    internal sealed class FileLeaseTimeoutException : Exception
    {
        public FileLeaseTimeoutException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// A cross-process advisory lock built on exclusive file creation, used to serialize
    /// work on one cache entry.
    /// </summary>
    /// <remarks>
    /// Parallel MSBuild puts several projects (and several <c>dotnet build</c> processes)
    /// on the same cache concurrently, so the mutual exclusion has to work across
    /// processes, not just threads. A named mutex would be simpler but is not portable
    /// across the platforms this task runs on.
    /// </remarks>
    internal sealed class FileLease : IDisposable
    {
        private readonly string _path;
        private FileStream? _stream;

        private FileLease(string path, FileStream stream)
        {
            _path = path;
            _stream = stream;
        }

        /// <summary>
        /// Whether the lock file is removed when the lease is released.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Windows only. There, <see cref="FileShare.None"/> means exactly one handle to
        /// the file exists at a time, so create-lock-delete is race-free.
        /// </para>
        /// <para>
        /// On Unix, .NET implements <see cref="FileShare"/> with <c>flock</c> and
        /// <see cref="FileOptions.DeleteOnClose"/> by unlinking on close. Unlinking while
        /// another process sits between <c>open()</c> and <c>flock()</c> would let that
        /// process lock the old inode while a third creates and locks a new file at the
        /// same path — two holders of one lock. The file is therefore left in place;
        /// the kernel releases <c>flock</c> when a process dies, so a crash cannot leave
        /// a permanently held lease either way.
        /// </para>
        /// </remarks>
        private static readonly FileOptions LeaseOptions =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? FileOptions.DeleteOnClose
                : FileOptions.None;

        public static FileLease Acquire(string path, TimeSpan timeout)
        {
            if (path is null) throw new ArgumentNullException(nameof(path));

            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            const int InitialDelayMilliseconds = 25;
            const int MaximumDelayMilliseconds = 500;

            int delay = InitialDelayMilliseconds;
            long deadline = Environment.TickCount + (long)timeout.TotalMilliseconds;

            while (true)
            {
                try
                {
                    FileStream stream = new FileStream(
                        path,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        LeaseOptions);
                    return new FileLease(path, stream);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                    // Seen on Windows when another process holds the handle with
                    // DeleteOnClose and the file is pending deletion.
                }

                if (Environment.TickCount >= deadline)
                {
                    throw new FileLeaseTimeoutException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Timed out after {0:0.#}s waiting for the driver cache lock '{1}'. Another build may be downloading the same driver; if no build is running, delete that file.",
                            timeout.TotalSeconds,
                            path));
                }

                Thread.Sleep(delay);
                delay = Math.Min(MaximumDelayMilliseconds, delay * 2);
            }
        }

        public void Dispose()
        {
            FileStream? stream = _stream;
            _stream = null;
            if (stream is null)
            {
                return;
            }

            try
            {
                stream.Dispose();
            }
            catch (IOException)
            {
            }
        }

        public override string ToString() => _path;
    }
}
