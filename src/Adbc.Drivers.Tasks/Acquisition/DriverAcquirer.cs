using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Adbc.Drivers.Build.Archives;
using Adbc.Drivers.Build.Caching;
using Adbc.Drivers.Build.Locking;
using Adbc.Drivers.Build.Registry;
using Adbc.Drivers.Build.Security;
using Adbc.Drivers.Build.Util;

namespace Adbc.Drivers.Build.Acquisition
{
    internal sealed class AcquisitionException : Exception
    {
        public AcquisitionException(string message)
            : base(message)
        {
        }
    }

    internal sealed class DeployedFile
    {
        public DeployedFile(string sourcePath, string relativePath, string driverId, string rid, bool copyToBuildOutput, bool copyToPublishDirectory)
        {
            SourcePath = sourcePath;
            RelativePath = relativePath;
            DriverId = driverId;
            Rid = rid;
            CopyToBuildOutput = copyToBuildOutput;
            CopyToPublishDirectory = copyToPublishDirectory;
        }

        /// <summary>Absolute path in the project's intermediate directory.</summary>
        public string SourcePath { get; }

        /// <summary>Forward-slash path relative to the deployed <c>adbc</c> directory.</summary>
        public string RelativePath { get; }

        public string DriverId { get; }

        public string Rid { get; }

        public bool CopyToBuildOutput { get; }

        public bool CopyToPublishDirectory { get; }
    }

    internal sealed class AcquisitionResult
    {
        public AcquisitionResult(IReadOnlyList<DeployedFile> files, DeploymentPlan plan)
        {
            Files = files;
            Plan = plan;
        }

        public IReadOnlyList<DeployedFile> Files { get; }

        public DeploymentPlan Plan { get; }
    }

    internal sealed class AcquisitionOptions
    {
        public NetworkMode Mode { get; set; } = NetworkMode.CacheOnly;

        public ExtractionLimits Limits { get; set; } = ExtractionLimits.Default;

        public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Project-local directory the verified files are materialized into, normally
        /// <c>$(IntermediateOutputPath)adbc</c>.
        /// </summary>
        public string DestinationRoot { get; set; } = string.Empty;

        /// <summary>
        /// Re-hash every file on disk on every build instead of trusting the immutable
        /// cache's receipt. Correct but slow: driver libraries are tens of megabytes.
        /// </summary>
        public bool VerifyFileHashes { get; set; }
    }

    /// <summary>
    /// Materializes the drivers named in the lock file into a project-local directory.
    /// </summary>
    /// <remarks>
    /// This is the component an ordinary <c>Build</c> or <c>Publish</c> runs. It reads
    /// exact versions, URLs, and hashes from the lock and never consults a registry, so
    /// it cannot select a different version than the one that was reviewed and committed.
    /// </remarks>
    internal sealed class DriverAcquirer
    {
        private readonly ContentAddressedCache _cache;
        private readonly IRegistryTransport _transport;
        private readonly ISignatureVerifier _signatureVerifier;
        private readonly Action<string> _log;

        public DriverAcquirer(
            ContentAddressedCache cache,
            IRegistryTransport transport,
            ISignatureVerifier signatureVerifier,
            Action<string>? log = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
            _log = log ?? (_ => { });
        }

        public AcquisitionResult Acquire(
            DriverLock driverLock,
            IReadOnlyList<DriverRequest> requests,
            AcquisitionOptions options)
        {
            if (driverLock is null) throw new ArgumentNullException(nameof(driverLock));
            if (requests is null) throw new ArgumentNullException(nameof(requests));
            if (options is null) throw new ArgumentNullException(nameof(options));

            if (options.Mode == NetworkMode.RefreshLock)
            {
                throw new AcquisitionException(
                    "AdbcDriverNetworkMode 'RefreshLock' re-resolves versions and rewrites the lock file, which an ordinary build must never do. "
                    + "Run the 'ResolveAdbcDriverLock' target instead, review the resulting lock file, and commit it.");
            }

            if (string.IsNullOrWhiteSpace(options.DestinationRoot))
            {
                throw new ArgumentException("A destination root is required.", nameof(options));
            }

            string destinationRoot = Path.GetFullPath(options.DestinationRoot);
            Directory.CreateDirectory(destinationRoot);

            List<DeployedFile> files = new List<DeployedFile>();
            List<DeployedDriver> drivers = new List<DeployedDriver>();

            foreach (DriverRequest request in requests)
            {
                LockedDriver? locked = driverLock.FindDriver(request.Id);
                if (locked is null)
                {
                    throw new AcquisitionException(
                        $"Driver '{request.Id}' is referenced by the project but is not in the driver lock file. "
                        + "Run the 'ResolveAdbcDriverLock' target to add it, then commit the updated lock file.");
                }

                WarnIfVersionSpecExcludesLock(request, locked);

                List<DeployedArtifact> artifacts = new List<DeployedArtifact>();
                foreach (string rid in request.RuntimeIdentifiers)
                {
                    LockedArtifact? artifact = locked.FindArtifact(rid);
                    if (artifact is null)
                    {
                        throw new AcquisitionException(
                            $"The driver lock file has no '{rid}' artifact for driver '{request.Id}' {locked.Version}. "
                            + $"Locked runtime identifiers are: {string.Join(", ", RidsOf(locked))}. "
                            + "Add the runtime identifier to the AdbcDriver item's Rids metadata and re-run 'ResolveAdbcDriverLock'.");
                    }

                    CacheEntry entry = Obtain(request, artifact, options);

                    // The receipt's hash was computed while extracting bytes whose archive
                    // hash had already been checked, so comparing against it detects a lock
                    // that disagrees with the cached content without re-reading the file.
                    if (!Hex.DigestEquals(entry.Receipt.DriverSha256, artifact.DriverSha256))
                    {
                        throw new IntegrityException(
                            $"The cached driver for '{request.Id}' ({rid}) has SHA-256 {entry.Receipt.DriverSha256}, but the lock file requires {artifact.DriverSha256}.");
                    }

                    string relativeDirectory = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}/{1}/{2}",
                        locked.Id,
                        locked.Version,
                        rid);

                    string targetDirectory = Path.Combine(
                        destinationRoot,
                        relativeDirectory.Replace('/', Path.DirectorySeparatorChar));

                    CopyFromCache(entry, targetDirectory, options.VerifyFileHashes, request.Id, rid);

                    foreach (ExtractedFile file in entry.Receipt.Files)
                    {
                        files.Add(new DeployedFile(
                            Path.Combine(targetDirectory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
                            relativeDirectory + "/" + file.RelativePath,
                            request.Id,
                            rid,
                            request.CopyToBuildOutput,
                            request.CopyToPublishDirectory));
                    }

                    artifacts.Add(new DeployedArtifact(
                        rid,
                        artifact.AdbcPlatform,
                        relativeDirectory,
                        entry.Receipt.DriverFile));
                }

                drivers.Add(new DeployedDriver(
                    locked.Id,
                    locked.Version,
                    request.EffectiveManifestName,
                    locked.Name,
                    locked.Publisher,
                    locked.License,
                    request.AdbcVersion ?? locked.AdbcVersion,
                    request.Entrypoint ?? locked.Entrypoint,
                    artifacts));
            }

            return new AcquisitionResult(files, new DeploymentPlan(drivers));
        }

        private CacheEntry Obtain(DriverRequest request, LockedArtifact artifact, AcquisitionOptions options)
        {
            if (!Uri.TryCreate(artifact.Url, UriKind.Absolute, out Uri? url))
            {
                throw new AcquisitionException(
                    $"The driver lock file records an unusable URL for '{request.Id}' ({artifact.Rid}): '{artifact.Url}'.");
            }

            CacheEntry? hit = _cache.TryOpen(artifact.ArchiveSha256);
            if (hit is not null)
            {
                return hit;
            }

            switch (options.Mode)
            {
                case NetworkMode.CacheOnly:
                    throw new CacheMissException(
                        $"Driver '{request.Id}' ({artifact.Rid}) is not in the driver cache at '{_cache.Root}'.\n"
                        + $"  expected SHA-256: {artifact.ArchiveSha256}\n"
                        + $"  source: {DefaultRegistryTransport.Redact(url!)}\n"
                        + $"  cache path: {_cache.EntryDirectory(artifact.ArchiveSha256)}\n"
                        + "Set AdbcDriverNetworkMode to 'Online' to allow this build to download it, or restore the cache directory.");

                case NetworkMode.Online:
                    return _cache.Install(
                        url!,
                        artifact.ArchiveSha256,
                        _transport,
                        _signatureVerifier,
                        options.Limits,
                        options.LockTimeout);

                case NetworkMode.ReadOnly:
                    _log($"Driver '{request.Id}' ({artifact.Rid}) is not cached; downloading without writing to the cache.");
                    return _cache.MaterializeUncached(
                        url!,
                        artifact.ArchiveSha256,
                        _transport,
                        _signatureVerifier,
                        options.Limits,
                        Path.Combine(Path.GetFullPath(options.DestinationRoot), ".uncached", artifact.ArchiveSha256));

                default:
                    throw new AcquisitionException($"Network mode '{options.Mode}' cannot be used to acquire drivers.");
            }
        }

        /// <summary>
        /// Copies a cache entry's files into the project-local directory. The cache is
        /// immutable, so a destination file of the right length is already correct;
        /// length is compared rather than timestamps, which say nothing about content.
        /// </summary>
        private static void CopyFromCache(CacheEntry entry, string targetDirectory, bool verifyFileHashes, string driverId, string rid)
        {
            Directory.CreateDirectory(targetDirectory);

            foreach (ExtractedFile file in entry.Receipt.Files)
            {
                string relative = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                string source = Path.Combine(entry.ExtractDirectory, relative);
                string target = Path.Combine(targetDirectory, relative);

                if (!File.Exists(source))
                {
                    throw new IntegrityException(
                        $"The cache entry for driver '{driverId}' ({rid}) is missing '{file.RelativePath}'. Delete '{entry.ExtractDirectory}' and build again.");
                }

                if (verifyFileHashes)
                {
                    string actual = Hashing.Sha256File(source);
                    if (!Hex.DigestEquals(actual, file.Sha256))
                    {
                        throw new IntegrityException(
                            $"The cached file '{source}' has SHA-256 {actual} but its receipt records {file.Sha256}. The cache entry has been tampered with or corrupted.");
                    }
                }

                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent!);
                }

                if (File.Exists(target) && new FileInfo(target).Length == file.Length)
                {
                    continue;
                }

                File.Copy(source, target, overwrite: true);

                // Cached files are read-only; the working copy must not be, or the next
                // build's copy will fail.
                FileAttributes attributes = File.GetAttributes(target);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }

        /// <summary>
        /// The lock is authoritative, but a project whose Version metadata no longer
        /// admits the locked version is almost certainly waiting on a forgotten resolve.
        /// </summary>
        private void WarnIfVersionSpecExcludesLock(DriverRequest request, LockedDriver locked)
        {
            if (!Model.VersionRange.TryParse(request.VersionSpec, out Model.VersionRange? range, out _))
            {
                return;
            }

            if (!Model.SemanticVersion.TryParse(locked.Version, out Model.SemanticVersion? version))
            {
                return;
            }

            if (!range!.Satisfies(version!))
            {
                _log(
                    $"Driver '{request.Id}' requests '{request.VersionSpec}' but the lock file pins {locked.Version}. "
                    + "The lock file wins; run 'ResolveAdbcDriverLock' to update it.");
            }
        }

        private static IEnumerable<string> RidsOf(LockedDriver locked)
        {
            foreach (LockedArtifact artifact in locked.Artifacts)
            {
                yield return artifact.Rid;
            }
        }
    }
}
