using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Adbc.Drivers.Build.Archives;
using Adbc.Drivers.Build.Packaging;
using Adbc.Drivers.Build.Registry;
using Adbc.Drivers.Build.Security;
using Adbc.Drivers.Build.Util;

namespace Adbc.Drivers.Build.Caching
{
    internal sealed class CacheMissException : Exception
    {
        public CacheMissException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Raised when downloaded bytes do not match what the lock file requires, or a
    /// signature check fails. Always fatal: never downgraded to a warning.
    /// </summary>
    internal sealed class IntegrityException : Exception
    {
        public IntegrityException(string message)
            : base(message)
        {
        }
    }

    internal sealed class CacheEntry
    {
        public CacheEntry(string archiveSha256, string extractDirectory, string? archivePath, CacheReceipt receipt, bool cached)
        {
            ArchiveSha256 = archiveSha256;
            ExtractDirectory = extractDirectory;
            ArchivePath = archivePath;
            Receipt = receipt;
            Cached = cached;
        }

        public string ArchiveSha256 { get; }

        public string ExtractDirectory { get; }

        /// <summary>Null when the archive was verified but deliberately not stored.</summary>
        public string? ArchivePath { get; }

        public CacheReceipt Receipt { get; }

        /// <summary>False when the entry lives outside the cache (read-only mode).</summary>
        public bool Cached { get; }

        public string DriverPath =>
            Path.Combine(ExtractDirectory, Receipt.DriverFile.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// An immutable, content-addressed store of verified driver archives.
    /// </summary>
    /// <remarks>
    /// <para>Layout, keyed by the SHA-256 of the archive:</para>
    /// <code>
    /// &lt;root&gt;/sha256/&lt;hash&gt;/archive.tar.gz
    /// &lt;root&gt;/sha256/&lt;hash&gt;/extract/...
    /// &lt;root&gt;/sha256/&lt;hash&gt;/receipt.json
    /// </code>
    /// <para>
    /// Work is staged under <c>&lt;root&gt;/tmp</c> and promoted with a directory move,
    /// and <c>receipt.json</c> is written last, so an entry is only ever observed
    /// complete. Entries are never mutated in place: different content means a different
    /// hash, which means a different entry.
    /// </para>
    /// </remarks>
    internal sealed class ContentAddressedCache
    {
        private static readonly string TaskVersion = ResolveTaskVersion();

        private readonly string _root;
        private readonly Action<string> _log;

        public ContentAddressedCache(string root, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A cache root is required.", nameof(root));

            _root = Path.GetFullPath(root);
            _log = log ?? (_ => { });
        }

        public string Root => _root;

        /// <summary>Default cache location, overridable by <c>ADBC_DRIVER_CACHE</c>.</summary>
        public static string DefaultRoot
        {
            get
            {
                string? configured = Environment.GetEnvironmentVariable("ADBC_DRIVER_CACHE");
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    return Path.GetFullPath(configured!.Trim());
                }

                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(profile))
                {
                    profile = Path.GetTempPath();
                }

                return Path.Combine(profile, ".adbc", "driver-cache");
            }
        }

        public string EntryDirectory(string archiveSha256) =>
            Path.Combine(_root, "sha256", NormalizeHash(archiveSha256));

        /// <summary>
        /// Returns a completed entry, or null when the archive is not cached. A partial
        /// or unreadable entry reads as a miss.
        /// </summary>
        public CacheEntry? TryOpen(string archiveSha256)
        {
            string hash = NormalizeHash(archiveSha256);
            string entryDirectory = EntryDirectory(hash);
            string receiptPath = Path.Combine(entryDirectory, "receipt.json");
            string extractDirectory = Path.Combine(entryDirectory, "extract");
            string archivePath = Path.Combine(entryDirectory, "archive.tar.gz");

            if (!File.Exists(receiptPath) || !Directory.Exists(extractDirectory))
            {
                return null;
            }

            CacheReceipt receipt;
            try
            {
                receipt = CacheReceipt.Load(receiptPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or Text.JsonParseException)
            {
                _log($"Ignoring the unreadable cache receipt '{receiptPath}': {ex.Message}");
                return null;
            }

            if (!Hex.DigestEquals(receipt.ArchiveSha256, hash))
            {
                _log($"Ignoring the cache entry at '{entryDirectory}' because its receipt records a different archive hash.");
                return null;
            }

            return new CacheEntry(hash, extractDirectory, archivePath, receipt, cached: true);
        }

        /// <summary>
        /// Ensures the archive is present in the cache and verified, then returns its entry.
        /// </summary>
        /// <param name="expectedArchiveSha256">
        /// The hash the lock file requires. Null only from the resolve step, which is
        /// learning the hash for the first time.
        /// </param>
        public CacheEntry Install(
            Uri url,
            string? expectedArchiveSha256,
            IRegistryTransport transport,
            ISignatureVerifier signatureVerifier,
            ExtractionLimits limits,
            TimeSpan lockTimeout)
        {
            if (url is null) throw new ArgumentNullException(nameof(url));
            if (transport is null) throw new ArgumentNullException(nameof(transport));

            if (expectedArchiveSha256 is not null)
            {
                CacheEntry? hit = TryOpen(expectedArchiveSha256);
                if (hit is not null)
                {
                    return hit;
                }
            }

            // With no known hash there is nothing to lock on yet, so serialize on the URL;
            // the per-hash lock is taken during promotion once the hash is known.
            string lockKey = expectedArchiveSha256 is not null
                ? NormalizeHash(expectedArchiveSha256)
                : "url-" + Hashing.Sha256Bytes(Encoding.UTF8.GetBytes(url.AbsoluteUri));

            Directory.CreateDirectory(Path.Combine(_root, "locks"));
            using (FileLease.Acquire(Path.Combine(_root, "locks", lockKey + ".lock"), lockTimeout))
            {
                // Another process may have completed the entry while we waited.
                if (expectedArchiveSha256 is not null)
                {
                    CacheEntry? hit = TryOpen(expectedArchiveSha256);
                    if (hit is not null)
                    {
                        return hit;
                    }
                }

                string stagingRoot = Path.Combine(_root, "tmp", Guid.NewGuid().ToString("N"));
                try
                {
                    StagedArchive staged = Stage(stagingRoot, url, expectedArchiveSha256, transport, signatureVerifier, limits);
                    Promote(stagingRoot, staged, lockTimeout, needsHashLock: expectedArchiveSha256 is null);

                    CacheEntry? entry = TryOpen(staged.Sha256);
                    if (entry is null)
                    {
                        throw new IOException(
                            $"The cache entry at '{EntryDirectory(staged.Sha256)}' was not readable immediately after being written.");
                    }

                    _log($"Cached {DefaultRegistryTransport.Redact(url)} as sha256:{staged.Sha256}");
                    return entry;
                }
                finally
                {
                    TryDelete(stagingRoot);
                }
            }
        }

        /// <summary>
        /// Downloads, verifies, and extracts into <paramref name="destinationDirectory"/>
        /// without touching the cache. Used by read-only mode, where the cache may be a
        /// shared or immutable directory that this build must not write to.
        /// </summary>
        public CacheEntry MaterializeUncached(
            Uri url,
            string? expectedArchiveSha256,
            IRegistryTransport transport,
            ISignatureVerifier signatureVerifier,
            ExtractionLimits limits,
            string destinationDirectory)
        {
            if (url is null) throw new ArgumentNullException(nameof(url));
            if (destinationDirectory is null) throw new ArgumentNullException(nameof(destinationDirectory));

            string staging = Path.GetFullPath(destinationDirectory);
            if (Directory.Exists(staging))
            {
                PathSafety.DeleteDirectoryRecursive(staging);
            }

            StagedArchive staged = Stage(staging, url, expectedArchiveSha256, transport, signatureVerifier, limits);
            TryDeleteFile(staged.ArchivePath);
            return new CacheEntry(staged.Sha256, staged.ExtractDirectory, null, staged.Receipt, cached: false);
        }

        /// <summary>
        /// Downloads, hash-verifies, extracts, and validates one archive into a private
        /// staging directory. Nothing outside that directory is touched, so a failure at
        /// any step leaves no partial state anywhere observable.
        /// </summary>
        private StagedArchive Stage(
            string stagingRoot,
            Uri url,
            string? expectedArchiveSha256,
            IRegistryTransport transport,
            ISignatureVerifier signatureVerifier,
            ExtractionLimits limits)
        {
            Directory.CreateDirectory(stagingRoot);

            string stagedArchive = Path.Combine(stagingRoot, "archive.tar.gz");

            _log($"Downloading {DefaultRegistryTransport.Redact(url)}");
            string actualHash;
            using (Stream source = transport.OpenRead(url))
            using (FileStream target = new FileStream(stagedArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024))
            {
                actualHash = Hashing.CopyAndHash(source, target, limits.MaxEntryBytes);
            }

            long archiveLength = new FileInfo(stagedArchive).Length;

            // Checked before extraction, so an archive whose hash does not match is never
            // unpacked at all.
            if (expectedArchiveSha256 is not null && !Hex.DigestEquals(actualHash, expectedArchiveSha256))
            {
                throw new IntegrityException(
                    $"The driver archive downloaded from {DefaultRegistryTransport.Redact(url)} has SHA-256 {actualHash}, but the lock file requires {NormalizeHash(expectedArchiveSha256)}. Refusing to use it.");
            }

            string stagedExtract = Path.Combine(stagingRoot, "extract");
            IReadOnlyList<ExtractedFile> files = ArchiveExtractor.ExtractTarGz(stagedArchive, stagedExtract, limits);

            string manifestPath = Path.Combine(stagedExtract, "MANIFEST");
            if (!File.Exists(manifestPath))
            {
                throw new PackageManifestException(
                    $"The driver archive from {DefaultRegistryTransport.Redact(url)} does not contain a MANIFEST file.");
            }

            PackageManifest manifest = PackageManifest.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));

            string driverPath = Path.Combine(stagedExtract, manifest.DriverFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(driverPath))
            {
                throw new PackageManifestException(
                    $"The package MANIFEST names '{manifest.DriverFile}' as the driver, but the archive does not contain it.");
            }

            string? signaturePath = manifest.SignatureFile is null
                ? null
                : Path.Combine(stagedExtract, manifest.SignatureFile.Replace('/', Path.DirectorySeparatorChar));

            SignatureVerificationResult verification = signatureVerifier.Verify(driverPath, signaturePath);
            if (verification.Status == SignatureVerificationStatus.Failed)
            {
                throw new IntegrityException(
                    $"The detached signature for '{manifest.DriverFile}' from {DefaultRegistryTransport.Redact(url)} did not verify: {verification.Detail}");
            }

            CacheReceipt receipt = CacheReceipt.Create(
                TaskVersion,
                actualHash,
                archiveLength,
                url,
                manifest,
                verification.Status.ToString(),
                files);

            return new StagedArchive(actualHash, stagedArchive, stagedExtract, receipt);
        }

        /// <summary>
        /// Moves a validated staging directory into its final location. The receipt is
        /// written last, so a crash mid-promotion leaves an entry that
        /// <see cref="TryOpen"/> treats as absent rather than as valid.
        /// </summary>
        private void Promote(string stagingRoot, StagedArchive staged, TimeSpan lockTimeout, bool needsHashLock)
        {
            // When the hash was not known up front the URL lock was taken instead, so take
            // the real per-hash lock now to serialize against a build that already knew it.
            FileLease? hashLease = needsHashLock
                ? FileLease.Acquire(Path.Combine(_root, "locks", staged.Sha256 + ".lock"), lockTimeout)
                : null;

            try
            {
                string entryDirectory = EntryDirectory(staged.Sha256);
                if (File.Exists(Path.Combine(entryDirectory, "receipt.json")))
                {
                    return;
                }

                MarkReadOnly(staged.ExtractDirectory);

                Directory.CreateDirectory(entryDirectory);

                string finalExtract = Path.Combine(entryDirectory, "extract");
                if (Directory.Exists(finalExtract))
                {
                    // Left by an interrupted promotion. The receipt's absence above proves
                    // it was never usable.
                    PathSafety.DeleteDirectoryRecursive(finalExtract);
                }

                Directory.Move(staged.ExtractDirectory, finalExtract);

                string finalArchive = Path.Combine(entryDirectory, "archive.tar.gz");
                TryDeleteFile(finalArchive);
                File.Move(staged.ArchivePath, finalArchive);
                MakeReadOnly(finalArchive);

                string stagedReceipt = Path.Combine(stagingRoot, "receipt.json");
                File.WriteAllText(stagedReceipt, staged.Receipt.ToJson(), new UTF8Encoding(false));
                File.Move(stagedReceipt, Path.Combine(entryDirectory, "receipt.json"));
            }
            finally
            {
                hashLease?.Dispose();
            }
        }

        private static void MarkReadOnly(string directory)
        {
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                MakeReadOnly(file);
            }
        }

        private static void MakeReadOnly(string file)
        {
            try
            {
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDelete(string directory)
        {
            try
            {
                PathSafety.DeleteDirectoryRecursive(directory);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                }

                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        internal static string NormalizeHash(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                throw new ArgumentException("A SHA-256 hash is required.", nameof(hash));
            }

            string trimmed = hash.Trim();
            if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring("sha256:".Length);
            }

            if (trimmed.Length != 64 || !Hex.TryParse(trimmed, out _))
            {
                throw new ArgumentException($"'{hash}' is not a SHA-256 hash in hexadecimal.", nameof(hash));
            }

            return trimmed.ToLowerInvariant();
        }

        private static string ResolveTaskVersion()
        {
            string version = typeof(ContentAddressedCache).GetTypeInfo().Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.0.0";
            int plus = version.IndexOf('+');
            return plus > 0 ? version.Substring(0, plus) : version;
        }

        private sealed class StagedArchive
        {
            public StagedArchive(string sha256, string archivePath, string extractDirectory, CacheReceipt receipt)
            {
                Sha256 = sha256;
                ArchivePath = archivePath;
                ExtractDirectory = extractDirectory;
                Receipt = receipt;
            }

            public string Sha256 { get; }

            public string ArchivePath { get; }

            public string ExtractDirectory { get; }

            public CacheReceipt Receipt { get; }
        }
    }
}
