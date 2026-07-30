using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Adbc.Drivers.Build.Model
{
    /// <summary>
    /// Maps between NuGet portable RIDs, used at the project boundary, and the ADBC
    /// platform tuples used by driver registries.
    /// </summary>
    /// <remarks>
    /// The mapping is explicit and closed on purpose. A custom <c>runtime.json</c> RID
    /// graph would be fragile on .NET 8 and later, which deliberately use a smaller
    /// portable graph, and would still not describe ADBC's separate tuple scheme.
    /// </remarks>
    internal static class RuntimeIdentifierMap
    {
        private static readonly KeyValuePair<string, string>[] Entries =
        {
            new KeyValuePair<string, string>("win-x64", "windows_amd64"),
            new KeyValuePair<string, string>("win-arm64", "windows_arm64"),
            new KeyValuePair<string, string>("linux-x64", "linux_amd64"),
            new KeyValuePair<string, string>("linux-arm64", "linux_arm64"),
            new KeyValuePair<string, string>("linux-musl-x64", "linux_amd64_musl"),
            new KeyValuePair<string, string>("linux-musl-arm64", "linux_arm64_musl"),
            new KeyValuePair<string, string>("osx-x64", "macos_amd64"),
            new KeyValuePair<string, string>("osx-arm64", "macos_arm64"),
        };

        private static readonly Dictionary<string, string> RidToPlatform = BuildRidToPlatform();

        private static readonly Dictionary<string, string> PlatformToRid = BuildPlatformToRid();

        public static IEnumerable<string> KnownRuntimeIdentifiers
        {
            get
            {
                foreach (KeyValuePair<string, string> entry in Entries)
                {
                    yield return entry.Key;
                }
            }
        }

        public static bool TryGetAdbcPlatform(string runtimeIdentifier, out string? platform)
        {
            platform = null;
            if (string.IsNullOrWhiteSpace(runtimeIdentifier))
            {
                return false;
            }

            return RidToPlatform.TryGetValue(runtimeIdentifier.Trim(), out platform);
        }

        public static bool TryGetRuntimeIdentifier(string adbcPlatform, out string? runtimeIdentifier)
        {
            runtimeIdentifier = null;
            if (string.IsNullOrWhiteSpace(adbcPlatform))
            {
                return false;
            }

            return PlatformToRid.TryGetValue(adbcPlatform.Trim(), out runtimeIdentifier);
        }

        /// <summary>
        /// The RID of the machine running the build, used when a project does not set
        /// <c>RuntimeIdentifier</c>.
        /// </summary>
        /// <remarks>
        /// musl is never inferred: distinguishing glibc from musl reliably needs more
        /// than this API surface offers, and guessing wrong produces a driver that
        /// fails to load at runtime. Alpine builds must set the RID explicitly.
        /// </remarks>
        public static string? TryGetHostRuntimeIdentifier()
        {
            string? os = null;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                os = "win";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                os = "linux";
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                os = "osx";
            }

            if (os is null)
            {
                return null;
            }

            string? architecture;
            switch (RuntimeInformation.OSArchitecture)
            {
                case Architecture.X64:
                    architecture = "x64";
                    break;
                case Architecture.Arm64:
                    architecture = "arm64";
                    break;
                default:
                    return null;
            }

            string candidate = os + "-" + architecture;
            return RidToPlatform.ContainsKey(candidate) ? candidate : null;
        }

        /// <summary>
        /// Normalizes a RID for lookup, collapsing version- and distro-qualified RIDs
        /// (<c>win10-x64</c>, <c>ubuntu.22.04-x64</c>) onto their portable equivalent.
        /// </summary>
        public static string Normalize(string runtimeIdentifier)
        {
            if (string.IsNullOrWhiteSpace(runtimeIdentifier))
            {
                return string.Empty;
            }

            string rid = runtimeIdentifier.Trim();
            if (RidToPlatform.ContainsKey(rid))
            {
                return rid;
            }

            int dash = rid.LastIndexOf('-');
            if (dash <= 0)
            {
                return rid;
            }

            string platform = rid.Substring(0, dash);
            string architecture = rid.Substring(dash + 1);

            bool musl = platform.IndexOf("musl", StringComparison.OrdinalIgnoreCase) >= 0
                || platform.StartsWith("alpine", StringComparison.OrdinalIgnoreCase);

            string? os = null;
            if (platform.StartsWith("win", StringComparison.OrdinalIgnoreCase))
            {
                os = "win";
            }
            else if (platform.StartsWith("osx", StringComparison.OrdinalIgnoreCase)
                || platform.StartsWith("macos", StringComparison.OrdinalIgnoreCase)
                || platform.StartsWith("ios", StringComparison.OrdinalIgnoreCase))
            {
                os = "osx";
            }
            else if (musl)
            {
                os = "linux-musl";
            }
            else
            {
                // Every remaining known RID base (linux, ubuntu, debian, rhel, fedora,
                // centos, sles, opensuse, arch, ...) is a glibc Linux.
                os = "linux";
            }

            string candidate = os + "-" + architecture;
            return RidToPlatform.ContainsKey(candidate) ? candidate : rid;
        }

        private static Dictionary<string, string> BuildRidToPlatform()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> entry in Entries)
            {
                map[entry.Key] = entry.Value;
            }

            return map;
        }

        private static Dictionary<string, string> BuildPlatformToRid()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> entry in Entries)
            {
                map[entry.Value] = entry.Key;
            }

            return map;
        }
    }
}
