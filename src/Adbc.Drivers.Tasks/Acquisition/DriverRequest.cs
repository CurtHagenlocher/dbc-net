using System;
using System.Collections.Generic;
using Adbc.Drivers.Build.Model;

namespace Adbc.Drivers.Build.Acquisition
{
    /// <summary>
    /// What a project asked for, as opposed to what was resolved. Comes from
    /// <c>AdbcDriver</c> items.
    /// </summary>
    internal sealed class DriverRequest
    {
        public DriverRequest(
            string id,
            string versionSpec,
            IReadOnlyList<string> runtimeIdentifiers,
            string? manifestName,
            string? entrypoint,
            string? adbcVersion,
            IReadOnlyDictionary<string, string> platformOverrides,
            bool copyToBuildOutput,
            bool copyToPublishDirectory)
        {
            Id = id;
            VersionSpec = versionSpec;
            RuntimeIdentifiers = runtimeIdentifiers;
            ManifestName = manifestName;
            Entrypoint = entrypoint;
            AdbcVersion = adbcVersion;
            PlatformOverrides = platformOverrides;
            CopyToBuildOutput = copyToBuildOutput;
            CopyToPublishDirectory = copyToPublishDirectory;
        }

        /// <summary>Registry driver slug, for example <c>snowflake</c>.</summary>
        public string Id { get; }

        /// <summary>
        /// Version or constraint as written in the project. Only the resolve step
        /// interprets it; builds use the exact version from the lock.
        /// </summary>
        public string VersionSpec { get; }

        /// <summary>Normalized portable RIDs to acquire.</summary>
        public IReadOnlyList<string> RuntimeIdentifiers { get; }

        /// <summary>Base name of the generated runtime manifest. Defaults to <see cref="Id"/>.</summary>
        public string? ManifestName { get; }

        public string? Entrypoint { get; }

        public string? AdbcVersion { get; }

        /// <summary>
        /// RID to ADBC platform tuple overrides, for the rare case where a registry
        /// publishes a tuple the built-in table does not map. Intentionally narrow: a
        /// project cannot supply an arbitrary download URL here.
        /// </summary>
        public IReadOnlyDictionary<string, string> PlatformOverrides { get; }

        public bool CopyToBuildOutput { get; }

        public bool CopyToPublishDirectory { get; }

        public string EffectiveManifestName =>
            string.IsNullOrWhiteSpace(ManifestName) ? Id : ManifestName!.Trim();

        /// <summary>Resolves the ADBC platform tuple to request for a RID.</summary>
        public string GetAdbcPlatform(string runtimeIdentifier)
        {
            if (PlatformOverrides.TryGetValue(runtimeIdentifier, out string? overridden))
            {
                return overridden;
            }

            if (RuntimeIdentifierMap.TryGetAdbcPlatform(runtimeIdentifier, out string? platform))
            {
                return platform!;
            }

            throw new DriverRequestException(
                $"The runtime identifier '{runtimeIdentifier}' requested for driver '{Id}' does not map to an ADBC platform. "
                + $"Known identifiers are {string.Join(", ", RuntimeIdentifierMap.KnownRuntimeIdentifiers)}. "
                + "Set PlatformOverride metadata to map it explicitly.");
        }
    }

    internal sealed class DriverRequestException : Exception
    {
        public DriverRequestException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// How much a build is allowed to do over the network.
    /// </summary>
    internal enum NetworkMode
    {
        /// <summary>
        /// Never reaches the network. A locked artifact missing from the cache is an
        /// error. The right default for CI and release builds.
        /// </summary>
        CacheOnly,

        /// <summary>
        /// Uses the cache, and otherwise downloads exactly the URL in the lock and
        /// verifies it against the locked hash. Never selects a different version.
        /// </summary>
        Online,

        /// <summary>
        /// May download and verify, but never writes to the cache. For builds whose
        /// cache directory is shared or immutable.
        /// </summary>
        ReadOnly,

        /// <summary>
        /// Re-resolves against the registries and rewrites the lock. Valid only for the
        /// explicit resolve target, never for <c>Build</c> or <c>Publish</c>.
        /// </summary>
        RefreshLock,
    }

    internal static class NetworkModeParser
    {
        public static bool TryParse(string? text, out NetworkMode mode)
        {
            mode = NetworkMode.CacheOnly;
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            switch (text!.Trim().ToLowerInvariant())
            {
                case "cacheonly":
                    mode = NetworkMode.CacheOnly;
                    return true;
                case "online":
                    mode = NetworkMode.Online;
                    return true;
                case "readonly":
                    mode = NetworkMode.ReadOnly;
                    return true;
                case "refreshlock":
                    mode = NetworkMode.RefreshLock;
                    return true;
                default:
                    return false;
            }
        }

        public static string Describe() => "CacheOnly, Online, ReadOnly, or RefreshLock";
    }
}
