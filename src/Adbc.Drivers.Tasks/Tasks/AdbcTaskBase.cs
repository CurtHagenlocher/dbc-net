using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Adbc.Drivers.Build.Acquisition;
using Adbc.Drivers.Build.Archives;
using Adbc.Drivers.Build.Caching;
using Adbc.Drivers.Build.Locking;
using Adbc.Drivers.Build.Model;
using Adbc.Drivers.Build.Packaging;
using Adbc.Drivers.Build.Registry;
using Adbc.Drivers.Build.Security;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Adbc.Drivers.Build.Tasks
{
    /// <summary>
    /// Shared configuration and error handling for the ADBC driver tasks.
    /// </summary>
    public abstract class AdbcTaskBase : Microsoft.Build.Utilities.Task
    {
        private static readonly char[] ListSeparators = { ';', ',' };

        /// <summary>
        /// Root of the content-addressed driver cache. Defaults to
        /// <c>$(UserProfile)/.adbc/driver-cache</c>, or <c>ADBC_DRIVER_CACHE</c>.
        /// </summary>
        public string? CacheRoot { get; set; }

        /// <summary>Maximum total expanded size accepted from one driver archive.</summary>
        public string? MaxExpandedBytes { get; set; }

        /// <summary>Maximum number of entries accepted in one driver archive.</summary>
        public string? MaxArchiveEntries { get; set; }

        /// <summary>Seconds to wait for another build to finish with the same cache entry.</summary>
        public string? CacheLockTimeoutSeconds { get; set; }

        /// <summary>Seconds to wait for a single network request.</summary>
        public string? NetworkTimeoutSeconds { get; set; }

        /// <summary>
        /// Permit plaintext HTTP to non-loopback hosts. For test fixtures only; never
        /// set this for a real registry.
        /// </summary>
        public bool AllowInsecureHttp { get; set; }

        public sealed override bool Execute()
        {
            try
            {
                Run();
                return !Log.HasLoggedErrors;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // These carry actionable, already-formatted messages; a stack trace would
                // only bury the useful part.
                Log.LogError(ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }

        protected abstract void Run();

        /// <summary>
        /// Failures that represent a bad configuration, a bad artifact, or an
        /// unreachable registry, rather than a defect in this task.
        /// </summary>
        private static bool IsExpected(Exception ex) =>
            ex is DriverLockException
            or DriverRequestException
            or ResolutionException
            or AcquisitionException
            or CacheMissException
            or IntegrityException
            or UnsafeArchiveException
            or PackageManifestException
            or RegistryTransportException
            or FileLeaseTimeoutException
            or Text.YamlParseException
            or Text.TomlParseException
            or Text.JsonParseException
            or InvalidDataException;

        private protected ContentAddressedCache CreateCache()
        {
            string root = string.IsNullOrWhiteSpace(CacheRoot)
                ? ContentAddressedCache.DefaultRoot
                : Path.GetFullPath(CacheRoot!.Trim());

            // Downloads are slow and otherwise invisible, so they are reported at high
            // importance while everything else stays at normal.
            return new ContentAddressedCache(root, message => Log.LogMessage(MessageImportance.High, message));
        }

        private protected ExtractionLimits CreateLimits()
        {
            ExtractionLimits limits = ExtractionLimits.Default;

            long expanded = ParseInt64(MaxExpandedBytes, nameof(MaxExpandedBytes), limits.MaxTotalBytes);
            limits.MaxTotalBytes = expanded;
            limits.MaxEntryBytes = Math.Min(limits.MaxEntryBytes, expanded);
            limits.MaxEntryCount = (int)ParseInt64(MaxArchiveEntries, nameof(MaxArchiveEntries), limits.MaxEntryCount);
            return limits;
        }

        private protected TimeSpan CreateLockTimeout() =>
            TimeSpan.FromSeconds(ParseInt64(CacheLockTimeoutSeconds, nameof(CacheLockTimeoutSeconds), 600));

        private protected DefaultRegistryTransport CreateTransport() =>
            new DefaultRegistryTransport(
                TimeSpan.FromSeconds(ParseInt64(NetworkTimeoutSeconds, nameof(NetworkTimeoutSeconds), 300)),
                AllowInsecureHttp);

        /// <summary>
        /// No signature verification is performed yet, and the receipt records that
        /// honestly rather than implying a check that did not happen.
        /// </summary>
        private protected static ISignatureVerifier CreateSignatureVerifier() => NullSignatureVerifier.Instance;

        /// <summary>
        /// Converts <c>AdbcDriver</c> items into requests, applying defaults and
        /// validating everything a later stage would otherwise fail on obscurely.
        /// </summary>
        private protected static IReadOnlyList<DriverRequest> ParseRequests(ITaskItem[]? drivers, string? defaultRuntimeIdentifiers)
        {
            List<string> defaults = SplitList(defaultRuntimeIdentifiers);
            List<DriverRequest> requests = new List<DriverRequest>();
            HashSet<string> seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenManifestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ITaskItem item in drivers ?? Array.Empty<ITaskItem>())
            {
                string id = (item.ItemSpec ?? string.Empty).Trim();
                if (id.Length == 0)
                {
                    throw new DriverRequestException("An AdbcDriver item has an empty identity. Set Include to a driver name such as 'snowflake'.");
                }

                if (!seenIds.Add(id))
                {
                    throw new DriverRequestException(
                        $"Driver '{id}' is listed more than once in AdbcDriver items. Combine the runtime identifiers into a single item's Rids metadata.");
                }

                List<string> rids = SplitList(item.GetMetadata("Rids"));
                if (rids.Count == 0)
                {
                    rids = defaults;
                }

                if (rids.Count == 0)
                {
                    throw new DriverRequestException(
                        $"No runtime identifiers were determined for driver '{id}'. Set Rids metadata on the item, set RuntimeIdentifier on the project, or set AdbcDriverRuntimeIdentifiers.");
                }

                List<string> normalized = new List<string>();
                HashSet<string> seenRids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string rid in rids)
                {
                    string portable = RuntimeIdentifierMap.Normalize(rid);
                    if (seenRids.Add(portable))
                    {
                        normalized.Add(portable);
                    }
                }

                string manifestName = item.GetMetadata("ManifestName");
                if (string.IsNullOrWhiteSpace(manifestName))
                {
                    manifestName = id;
                }

                if (!seenManifestNames.Add(manifestName.Trim()))
                {
                    throw new DriverRequestException(
                        $"More than one AdbcDriver item would produce the manifest '{manifestName.Trim()}.toml'. Set distinct ManifestName metadata.");
                }

                ValidateManifestName(id, manifestName.Trim());

                DriverRequest request = new DriverRequest(
                    id,
                    item.GetMetadata("Version"),
                    normalized,
                    manifestName.Trim(),
                    NullIfEmpty(item.GetMetadata("Entrypoint")),
                    NullIfEmpty(item.GetMetadata("AdbcVersion")),
                    ParsePlatformOverrides(id, item.GetMetadata("PlatformOverride"), normalized),
                    ParseBoolean(item.GetMetadata("CopyToBuildOutput"), true),
                    ParseBoolean(item.GetMetadata("CopyToPublishDirectory"), true));

                // Surfaces an unmappable RID here rather than midway through a download.
                foreach (string rid in normalized)
                {
                    request.GetAdbcPlatform(rid);
                }

                requests.Add(request);
            }

            return requests;
        }

        /// <summary>
        /// Accepts <c>rid=platform</c> pairs, or a bare platform tuple when the item names
        /// exactly one runtime identifier.
        /// </summary>
        private static Dictionary<string, string> ParsePlatformOverrides(
            string id,
            string? metadata,
            List<string> rids)
        {
            Dictionary<string, string> overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(metadata))
            {
                return overrides;
            }

            foreach (string piece in SplitList(metadata))
            {
                int equals = piece.IndexOf('=');
                if (equals < 0)
                {
                    if (rids.Count != 1)
                    {
                        throw new DriverRequestException(
                            $"PlatformOverride '{piece}' on driver '{id}' must be written as 'rid=platform' because the item names {rids.Count} runtime identifiers.");
                    }

                    overrides[rids[0]] = piece;
                    continue;
                }

                string rid = piece.Substring(0, equals).Trim();
                string platform = piece.Substring(equals + 1).Trim();
                if (rid.Length == 0 || platform.Length == 0)
                {
                    throw new DriverRequestException(
                        $"PlatformOverride '{piece}' on driver '{id}' is not of the form 'rid=platform'.");
                }

                overrides[RuntimeIdentifierMap.Normalize(rid)] = platform;
            }

            return overrides;
        }

        /// <summary>
        /// The manifest name becomes a file name in the output directory, so it must not
        /// be able to escape it.
        /// </summary>
        private static void ValidateManifestName(string id, string manifestName)
        {
            if (manifestName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || manifestName.IndexOf('/') >= 0
                || manifestName.IndexOf('\\') >= 0
                || string.Equals(manifestName, ".", StringComparison.Ordinal)
                || string.Equals(manifestName, "..", StringComparison.Ordinal))
            {
                throw new DriverRequestException(
                    $"ManifestName '{manifestName}' on driver '{id}' is not a valid file name.");
            }
        }

        protected static List<string> SplitList(string? value)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return result;
            }

            foreach (string piece in value!.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = piece.Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }

            return result;
        }

        protected static string? NullIfEmpty(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        protected static bool ParseBoolean(string? value, bool fallback) =>
            string.IsNullOrWhiteSpace(value) || !bool.TryParse(value!.Trim(), out bool parsed)
                ? fallback
                : parsed;

        private static long ParseInt64(string? value, string name, long fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            if (!long.TryParse(value!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) || parsed <= 0)
            {
                throw new DriverRequestException($"{name} must be a positive integer, but was '{value}'.");
            }

            return parsed;
        }
    }
}
