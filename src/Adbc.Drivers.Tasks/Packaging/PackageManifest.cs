using System;
using System.Collections.Generic;
using Adbc.Drivers.Build.Text;

namespace Adbc.Drivers.Build.Packaging
{
    internal sealed class PackageManifestException : Exception
    {
        public PackageManifestException(string message)
            : base(message)
        {
        }

        public PackageManifestException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    /// The <c>MANIFEST</c> file carried inside a driver archive.
    /// </summary>
    /// <remarks>
    /// Real archives are looser than the documented shape: the Snowflake 1.11.0 package,
    /// for instance, has no <c>manifest_version</c> key and no <c>[Driver]</c> table.
    /// Both are therefore optional. <c>Files.driver</c> is the one hard requirement,
    /// because without it there is no way to know which extracted file the ADBC driver
    /// manager should load.
    /// </remarks>
    internal sealed class PackageManifest
    {
        private PackageManifest(
            int? manifestVersion,
            string? name,
            string? description,
            string? publisher,
            string? license,
            string? version,
            string? adbcVersion,
            string? entrypoint,
            string driverFile,
            string? signatureFile,
            IReadOnlyList<KeyValuePair<string, string>> files)
        {
            ManifestVersion = manifestVersion;
            Name = name;
            Description = description;
            Publisher = publisher;
            License = license;
            Version = version;
            AdbcVersion = adbcVersion;
            Entrypoint = entrypoint;
            DriverFile = driverFile;
            SignatureFile = signatureFile;
            Files = files;
        }

        public int? ManifestVersion { get; }

        public string? Name { get; }

        public string? Description { get; }

        public string? Publisher { get; }

        public string? License { get; }

        /// <summary>Driver version as spelled in the archive, which may carry a <c>v</c> prefix.</summary>
        public string? Version { get; }

        public string? AdbcVersion { get; }

        /// <summary>Driver init symbol, or null when the archive does not state one.</summary>
        public string? Entrypoint { get; }

        /// <summary>Archive-relative name of the shared library. Always present.</summary>
        public string DriverFile { get; }

        /// <summary>Archive-relative name of the detached signature, when included.</summary>
        public string? SignatureFile { get; }

        /// <summary>Every entry of the <c>[Files]</c> table, in declaration order.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Files { get; }

        public static PackageManifest Parse(string toml)
        {
            if (toml is null) throw new ArgumentNullException(nameof(toml));

            TomlTable table;
            try
            {
                table = TomlParser.Parse(toml);
            }
            catch (TomlParseException ex)
            {
                throw new PackageManifestException($"The package MANIFEST is malformed: {ex.Message}", ex);
            }

            int? manifestVersion = table.GetInt32("manifest_version");
            if (manifestVersion.HasValue && manifestVersion.Value != 1)
            {
                throw new PackageManifestException(
                    $"The package MANIFEST declares manifest_version {manifestVersion.Value}; only version 1 is understood.");
            }

            TomlTable? files = table.GetTable("Files");
            if (files is null)
            {
                throw new PackageManifestException("The package MANIFEST has no [Files] table.");
            }

            string? driverFile = files.GetString("driver");
            if (string.IsNullOrWhiteSpace(driverFile))
            {
                throw new PackageManifestException(
                    "The package MANIFEST does not name a driver under [Files]; the shared library to load cannot be determined.");
            }

            ValidateFileName(driverFile!, "driver");

            string? signatureFile = files.GetString("signature");
            if (!string.IsNullOrWhiteSpace(signatureFile))
            {
                ValidateFileName(signatureFile!, "signature");
            }
            else
            {
                signatureFile = null;
            }

            List<KeyValuePair<string, string>> allFiles = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> entry in files.Values)
            {
                ValidateFileName(entry.Value, entry.Key);
                allFiles.Add(entry);
            }

            return new PackageManifest(
                manifestVersion,
                table.GetString("name"),
                table.GetString("description"),
                table.GetString("publisher"),
                table.GetString("license"),
                table.GetString("version"),
                table.GetTable("ADBC")?.GetString("version"),
                table.GetTable("Driver")?.GetString("entrypoint"),
                driverFile!.Trim(),
                signatureFile?.Trim(),
                allFiles);
        }

        /// <summary>
        /// A MANIFEST file reference is used to locate a file inside the extraction
        /// directory, so it must not be able to point anywhere else.
        /// </summary>
        private static void ValidateFileName(string value, string key)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new PackageManifestException($"The package MANIFEST has an empty value for [Files].{key}.");
            }

            string trimmed = value.Trim();
            if (trimmed.IndexOf('\\') >= 0
                || trimmed.StartsWith("/", StringComparison.Ordinal)
                || (trimmed.Length >= 2 && trimmed[1] == ':'))
            {
                throw new PackageManifestException(
                    $"The package MANIFEST value for [Files].{key} ('{trimmed}') is not an archive-relative path.");
            }

            foreach (string segment in trimmed.Split('/'))
            {
                if (segment.Length == 0
                    || string.Equals(segment, ".", StringComparison.Ordinal)
                    || string.Equals(segment, "..", StringComparison.Ordinal))
                {
                    throw new PackageManifestException(
                        $"The package MANIFEST value for [Files].{key} ('{trimmed}') is not an archive-relative path.");
                }
            }
        }
    }
}
