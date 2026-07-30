using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Adbc.Drivers.Build.Archives;
using Adbc.Drivers.Build.Packaging;
using Adbc.Drivers.Build.Text;

namespace Adbc.Drivers.Build.Caching
{
    /// <summary>
    /// Records exactly what a cache entry contains and how it was validated.
    /// </summary>
    /// <remarks>
    /// Receipts carry no timestamps or machine identifiers, so the receipt for a given
    /// archive is byte-identical on every machine. That makes a receipt directly
    /// comparable across a developer machine and CI, which a timestamped one would not
    /// be.
    /// </remarks>
    internal sealed class CacheReceipt
    {
        public const int CurrentSchemaVersion = 1;

        public CacheReceipt(
            int schemaVersion,
            string taskVersion,
            string archiveSha256,
            long archiveLength,
            string? sourceUrl,
            string driverFile,
            string driverSha256,
            string? signatureFile,
            string? signatureSha256,
            string? manifestName,
            string? manifestVersion,
            string? publisher,
            string? license,
            string? adbcVersion,
            string? entrypoint,
            string signatureVerification,
            IReadOnlyList<ExtractedFile> files)
        {
            SchemaVersion = schemaVersion;
            TaskVersion = taskVersion;
            ArchiveSha256 = archiveSha256;
            ArchiveLength = archiveLength;
            SourceUrl = sourceUrl;
            DriverFile = driverFile;
            DriverSha256 = driverSha256;
            SignatureFile = signatureFile;
            SignatureSha256 = signatureSha256;
            ManifestName = manifestName;
            ManifestVersion = manifestVersion;
            Publisher = publisher;
            License = license;
            AdbcVersion = adbcVersion;
            Entrypoint = entrypoint;
            SignatureVerification = signatureVerification;
            Files = files;
        }

        public int SchemaVersion { get; }

        /// <summary>Version of the task that produced the entry.</summary>
        public string TaskVersion { get; }

        public string ArchiveSha256 { get; }

        public long ArchiveLength { get; }

        public string? SourceUrl { get; }

        public string DriverFile { get; }

        public string DriverSha256 { get; }

        public string? SignatureFile { get; }

        public string? SignatureSha256 { get; }

        public string? ManifestName { get; }

        public string? ManifestVersion { get; }

        public string? Publisher { get; }

        public string? License { get; }

        public string? AdbcVersion { get; }

        public string? Entrypoint { get; }

        /// <summary>
        /// How the detached signature was treated: <c>NotAttempted</c>, <c>NotPresent</c>,
        /// or <c>Verified</c>. Recorded so a later build can tell whether an entry was
        /// admitted under a weaker policy than the current one.
        /// </summary>
        public string SignatureVerification { get; }

        public IReadOnlyList<ExtractedFile> Files { get; }

        public static CacheReceipt Create(
            string taskVersion,
            string archiveSha256,
            long archiveLength,
            Uri? sourceUrl,
            PackageManifest manifest,
            string signatureVerification,
            IReadOnlyList<ExtractedFile> files)
        {
            if (manifest is null) throw new ArgumentNullException(nameof(manifest));
            if (files is null) throw new ArgumentNullException(nameof(files));

            ExtractedFile driver = Find(files, manifest.DriverFile)
                ?? throw new PackageManifestException(
                    $"The package MANIFEST names '{manifest.DriverFile}' as the driver, but the archive does not contain it.");

            ExtractedFile? signature = manifest.SignatureFile is null ? null : Find(files, manifest.SignatureFile);
            if (manifest.SignatureFile is not null && signature is null)
            {
                throw new PackageManifestException(
                    $"The package MANIFEST names '{manifest.SignatureFile}' as the driver signature, but the archive does not contain it.");
            }

            return new CacheReceipt(
                CurrentSchemaVersion,
                taskVersion,
                archiveSha256,
                archiveLength,
                sourceUrl is null ? null : Registry.DefaultRegistryTransport.Redact(sourceUrl),
                driver.RelativePath,
                driver.Sha256,
                signature?.RelativePath,
                signature?.Sha256,
                manifest.Name,
                manifest.Version,
                manifest.Publisher,
                manifest.License,
                manifest.AdbcVersion,
                manifest.Entrypoint,
                signatureVerification,
                files);
        }

        public static CacheReceipt Load(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            JsonValue root = JsonParser.Parse(text);

            int? schemaVersion = root["schemaVersion"].AsInt32();
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"'{path}' declares receipt schema version {schemaVersion?.ToString() ?? "(none)"}; only version {CurrentSchemaVersion} is understood.");
            }

            List<ExtractedFile> files = new List<ExtractedFile>();
            foreach (JsonValue file in root["files"].AsArray())
            {
                files.Add(new ExtractedFile(
                    file["path"].AsString() ?? throw new InvalidDataException($"'{path}' has a file entry with no path."),
                    file["length"].AsInt64() ?? 0,
                    file["sha256"].AsString() ?? throw new InvalidDataException($"'{path}' has a file entry with no hash.")));
            }

            return new CacheReceipt(
                schemaVersion.Value,
                root["taskVersion"].AsString() ?? "unknown",
                root["archiveSha256"].AsString() ?? throw new InvalidDataException($"'{path}' has no archiveSha256."),
                root["archiveLength"].AsInt64() ?? 0,
                root["sourceUrl"].AsString(),
                root["driverFile"].AsString() ?? throw new InvalidDataException($"'{path}' has no driverFile."),
                root["driverSha256"].AsString() ?? throw new InvalidDataException($"'{path}' has no driverSha256."),
                root["signatureFile"].AsString(),
                root["signatureSha256"].AsString(),
                root["manifestName"].AsString(),
                root["manifestVersion"].AsString(),
                root["publisher"].AsString(),
                root["license"].AsString(),
                root["adbcVersion"].AsString(),
                root["entrypoint"].AsString(),
                root["signatureVerification"].AsString() ?? "Unknown",
                files);
        }

        public string ToJson()
        {
            JsonTextWriter writer = new JsonTextWriter();
            writer.StartObject();
            writer.Property("schemaVersion", SchemaVersion);
            writer.Property("taskVersion", TaskVersion);
            writer.Property("archiveSha256", ArchiveSha256);
            writer.Property("archiveLength", ArchiveLength);
            writer.Property("sourceUrl", SourceUrl);
            writer.Property("driverFile", DriverFile);
            writer.Property("driverSha256", DriverSha256);
            writer.Property("signatureFile", SignatureFile);
            writer.Property("signatureSha256", SignatureSha256);
            writer.Property("manifestName", ManifestName);
            writer.Property("manifestVersion", ManifestVersion);
            writer.Property("publisher", Publisher);
            writer.Property("license", License);
            writer.Property("adbcVersion", AdbcVersion);
            writer.Property("entrypoint", Entrypoint);
            writer.Property("signatureVerification", SignatureVerification);

            writer.Name("files").StartArray();
            foreach (ExtractedFile file in Files)
            {
                writer.StartObject();
                writer.Property("path", file.RelativePath);
                writer.Property("length", file.Length);
                writer.Property("sha256", file.Sha256);
                writer.EndObject();
            }

            writer.EndArray();
            writer.EndObject();
            return writer.ToString() + "\n";
        }

        public ExtractedFile? FindFile(string relativePath) => Find(Files, relativePath);

        private static ExtractedFile? Find(IReadOnlyList<ExtractedFile> files, string relativePath)
        {
            foreach (ExtractedFile file in files)
            {
                if (string.Equals(file.RelativePath, relativePath, StringComparison.Ordinal))
                {
                    return file;
                }
            }

            // Archives are flat in practice, but tolerate a case difference on Windows.
            foreach (ExtractedFile file in files)
            {
                if (string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }

            return null;
        }
    }
}
