using System;
using System.Collections.Generic;
using System.Text;
using Adbc.Drivers.Build.Caching;
using Adbc.Drivers.Build.Locking;
using Adbc.Drivers.Build.Model;
using Adbc.Drivers.Build.Registry;
using Adbc.Drivers.Build.Security;
using Adbc.Drivers.Build.Archives;

namespace Adbc.Drivers.Build.Acquisition
{
    internal sealed class ResolutionException : Exception
    {
        public ResolutionException(string message)
            : base(message)
        {
        }
    }

    internal sealed class ResolutionOptions
    {
        public IReadOnlyList<Uri> Registries { get; set; } = Array.Empty<Uri>();

        public bool AllowPrerelease { get; set; }

        public ExtractionLimits Limits { get; set; } = ExtractionLimits.Default;

        public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Maximum size accepted for a registry index document.</summary>
        public long MaxIndexBytes { get; set; } = 16L * 1024 * 1024;

        public static Uri DefaultPublicRegistry { get; } = new Uri("https://dbc-cdn.columnar.tech/");
    }

    /// <summary>
    /// Turns project intent into a lock file: reads registry indexes, applies version
    /// constraints, then downloads and inspects the selected archives to learn their
    /// hashes and metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only component that reads a registry index or interprets a version
    /// range, and it runs only from the explicit resolve target. Keeping it out of the
    /// ordinary build path is what makes builds reproducible: a mutable upstream index
    /// cannot change the bytes a build consumes without a lock-file diff.
    /// </para>
    /// <para>
    /// A hash learned here provides reproducibility, not first-use authenticity — the
    /// hash came from the same download it describes. That is why the resolve step is
    /// expected to be run deliberately and its output reviewed before being committed.
    /// See <see cref="ISignatureVerifier"/>.
    /// </para>
    /// </remarks>
    internal sealed class DriverResolver
    {
        private readonly IRegistryTransport _transport;
        private readonly ContentAddressedCache _cache;
        private readonly ISignatureVerifier _signatureVerifier;
        private readonly Action<string> _log;
        private readonly Action<string> _warn;

        public DriverResolver(
            IRegistryTransport transport,
            ContentAddressedCache cache,
            ISignatureVerifier signatureVerifier,
            Action<string>? log = null,
            Action<string>? warn = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
            _log = log ?? (_ => { });
            _warn = warn ?? (_ => { });
        }

        public DriverLock Resolve(IReadOnlyList<DriverRequest> requests, ResolutionOptions options)
        {
            if (requests is null) throw new ArgumentNullException(nameof(requests));
            if (options is null) throw new ArgumentNullException(nameof(options));

            IReadOnlyList<Uri> registries = options.Registries.Count > 0
                ? options.Registries
                : new[] { ResolutionOptions.DefaultPublicRegistry };

            RegistryCatalog catalog = LoadCatalog(registries, options);
            foreach (string shadowed in catalog.ShadowedDrivers)
            {
                _warn($"Driver '{shadowed}' appears in more than one registry; the first registry wins.");
            }

            List<LockedDriver> drivers = new List<LockedDriver>();
            List<string> registryStrings = new List<string>();
            foreach (Uri registry in registries)
            {
                registryStrings.Add(registry.AbsoluteUri);
            }

            foreach (DriverRequest request in requests)
            {
                drivers.Add(ResolveOne(request, catalog, options));
            }

            return new DriverLock(DriverLock.CurrentSchemaVersion, registryStrings, drivers);
        }

        private RegistryCatalog LoadCatalog(IReadOnlyList<Uri> registries, ResolutionOptions options)
        {
            List<RegistryIndex> indexes = new List<RegistryIndex>();
            foreach (Uri registry in registries)
            {
                Uri baseUri = DriverEntry.EnsureTrailingSlash(registry);
                Uri indexUri = new Uri(baseUri, "index.yaml");

                _log($"Reading registry index {DefaultRegistryTransport.Redact(indexUri)}");
                string yaml = _transport.ReadAllText(indexUri, options.MaxIndexBytes);
                indexes.Add(RegistryIndex.Parse(yaml, baseUri, DefaultRegistryTransport.Redact(indexUri)));
            }

            return new RegistryCatalog(indexes);
        }

        private LockedDriver ResolveOne(DriverRequest request, RegistryCatalog catalog, ResolutionOptions options)
        {
            DriverEntry? entry = catalog.Find(request.Id);
            if (entry is null)
            {
                throw new ResolutionException(
                    $"No driver named '{request.Id}' is published by the configured registries. Available drivers: {string.Join(", ", Sorted(catalog.Slugs))}.");
            }

            if (!VersionRange.TryParse(request.VersionSpec, out VersionRange? range, out string? rangeError))
            {
                throw new ResolutionException(
                    $"The Version metadata '{request.VersionSpec}' on driver '{request.Id}' is not usable: {rangeError}");
            }

            List<SemanticVersion> candidates = new List<SemanticVersion>();
            foreach (DriverRelease release in entry.Releases)
            {
                candidates.Add(release.Version);
            }

            // Per-item metadata wins over the project-wide default, so that one driver can
            // track prereleases without opting every other driver in.
            bool allowPrerelease = request.AllowPrerelease ?? options.AllowPrerelease;

            SemanticVersion? selected = range!.SelectBest(candidates, allowPrerelease);
            if (selected is null)
            {
                string prereleaseHint = !allowPrerelease && HasOnlyPrereleaseMatches(range, candidates)
                    ? " Only prerelease versions match; set Prerelease=\"allow\" on the item to accept one."
                    : string.Empty;

                throw new ResolutionException(
                    $"No version of driver '{request.Id}' satisfies '{range.Original}'. Published versions: {string.Join(", ", Sorted(Describe(candidates)))}.{prereleaseHint}");
            }

            DriverRelease chosen = entry.FindRelease(selected)
                ?? throw new ResolutionException($"Internal error: release {selected} vanished from driver '{request.Id}'.");

            _log($"Resolved {request.Id} {range.Original} to {selected.ToNormalizedString()}");

            List<LockedArtifact> artifacts = new List<LockedArtifact>();
            string? publisher = null;
            string? adbcVersion = request.AdbcVersion;
            string? entrypoint = request.Entrypoint;
            string? license = entry.License;
            string? name = entry.Name;

            foreach (string rid in request.RuntimeIdentifiers)
            {
                string platform = request.GetAdbcPlatform(rid);
                DriverPackage? package = chosen.FindPackage(platform);
                if (package is null)
                {
                    throw new ResolutionException(
                        $"Driver '{request.Id}' {selected.ToNormalizedString()} has no package for '{platform}' (requested as RID '{rid}'). "
                        + $"Available platforms: {string.Join(", ", Sorted(Platforms(chosen)))}.");
                }

                Uri url = entry.ResolvePackageUrl(chosen, package, out bool derived);
                if (derived)
                {
                    _warn(
                        $"The registry does not record a URL for driver '{request.Id}' {chosen.RawVersion} on '{platform}'; "
                        + $"using the conventional location {DefaultRegistryTransport.Redact(url)}.");
                }

                // Downloaded during resolve precisely so the hashes and package metadata
                // recorded in the lock describe bytes that were actually inspected.
                CacheEntry cached = _cache.Install(
                    url,
                    expectedArchiveSha256: null,
                    _transport,
                    _signatureVerifier,
                    options.Limits,
                    options.LockTimeout);

                CacheReceipt receipt = cached.Receipt;
                publisher ??= receipt.Publisher;
                license ??= receipt.License;
                name ??= receipt.ManifestName;
                adbcVersion ??= receipt.AdbcVersion;
                entrypoint ??= receipt.Entrypoint;

                WarnOnVersionMismatch(request.Id, rid, selected, receipt.ManifestVersion);

                artifacts.Add(new LockedArtifact(
                    rid,
                    platform,
                    url.AbsoluteUri,
                    receipt.ArchiveSha256,
                    receipt.ArchiveLength,
                    receipt.DriverFile,
                    receipt.DriverSha256,
                    receipt.SignatureFile,
                    receipt.SignatureSha256,
                    signatureKeyFingerprint: null));
            }

            if (artifacts.Count == 0)
            {
                throw new ResolutionException(
                    $"No runtime identifiers were requested for driver '{request.Id}'. Set Rids metadata or a project RuntimeIdentifier.");
            }

            return new LockedDriver(
                request.Id,
                selected.ToNormalizedString(),
                name,
                publisher,
                license,
                adbcVersion,
                entrypoint,
                artifacts);
        }

        /// <summary>
        /// The archive's own MANIFEST is the authority on what it contains; a disagreement
        /// with the index is worth surfacing but is not fatal, because the index version
        /// is only a selector.
        /// </summary>
        private void WarnOnVersionMismatch(string id, string rid, SemanticVersion selected, string? manifestVersion)
        {
            if (string.IsNullOrWhiteSpace(manifestVersion))
            {
                return;
            }

            if (SemanticVersion.TryParse(manifestVersion, out SemanticVersion? parsed) && parsed!.Equals(selected))
            {
                return;
            }

            _warn(
                $"The registry lists driver '{id}' ({rid}) as version {selected.ToNormalizedString()}, "
                + $"but the archive's MANIFEST declares '{manifestVersion}'.");
        }

        private static IEnumerable<string> Describe(IEnumerable<SemanticVersion> versions)
        {
            foreach (SemanticVersion version in versions)
            {
                yield return version.ToNormalizedString();
            }
        }

        private static IEnumerable<string> Platforms(DriverRelease release)
        {
            foreach (DriverPackage package in release.Packages)
            {
                yield return package.Platform;
            }
        }

        /// <summary>
        /// True when the constraint would have been satisfiable had prereleases been
        /// permitted, so the error can say so instead of just listing versions.
        /// </summary>
        private static bool HasOnlyPrereleaseMatches(VersionRange range, IEnumerable<SemanticVersion> candidates)
        {
            foreach (SemanticVersion candidate in candidates)
            {
                if (candidate.IsPrerelease && range.Satisfies(candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> Sorted(IEnumerable<string> values)
        {
            List<string> list = new List<string>(values);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }
}
