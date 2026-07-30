using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Adbc.Drivers.Build.Text;
using Adbc.Drivers.Build.Util;

namespace Adbc.Drivers.Build.Locking
{
    internal sealed class DriverLockException : Exception
    {
        public DriverLockException(string message)
            : base(message)
        {
        }

        public DriverLockException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    /// One driver artifact: a specific driver version built for a specific RID.
    /// </summary>
    internal sealed class LockedArtifact
    {
        public LockedArtifact(
            string rid,
            string adbcPlatform,
            string url,
            string archiveSha256,
            long archiveLength,
            string driverFile,
            string driverSha256,
            string? signatureFile,
            string? signatureSha256,
            string? signatureKeyFingerprint)
        {
            Rid = rid;
            AdbcPlatform = adbcPlatform;
            Url = url;
            ArchiveSha256 = archiveSha256;
            ArchiveLength = archiveLength;
            DriverFile = driverFile;
            DriverSha256 = driverSha256;
            SignatureFile = signatureFile;
            SignatureSha256 = signatureSha256;
            SignatureKeyFingerprint = signatureKeyFingerprint;
        }

        public string Rid { get; }

        public string AdbcPlatform { get; }

        /// <summary>The exact, immutable URL a build is permitted to fetch.</summary>
        public string Url { get; }

        public string ArchiveSha256 { get; }

        public long ArchiveLength { get; }

        public string DriverFile { get; }

        public string DriverSha256 { get; }

        public string? SignatureFile { get; }

        public string? SignatureSha256 { get; }

        /// <summary>
        /// Fingerprint of the key the signature was verified against, when signature
        /// verification was performed. Null means no signature check has been made.
        /// </summary>
        public string? SignatureKeyFingerprint { get; }
    }

    internal sealed class LockedDriver
    {
        public LockedDriver(
            string id,
            string version,
            string? name,
            string? publisher,
            string? license,
            string? adbcVersion,
            string? entrypoint,
            IReadOnlyList<LockedArtifact> artifacts)
        {
            Id = id;
            Version = version;
            Name = name;
            Publisher = publisher;
            License = license;
            AdbcVersion = adbcVersion;
            Entrypoint = entrypoint;
            Artifacts = artifacts;
        }

        public string Id { get; }

        /// <summary>Normalized exact version. Never a range.</summary>
        public string Version { get; }

        public string? Name { get; }

        public string? Publisher { get; }

        public string? License { get; }

        public string? AdbcVersion { get; }

        public string? Entrypoint { get; }

        public IReadOnlyList<LockedArtifact> Artifacts { get; }

        public LockedArtifact? FindArtifact(string rid)
        {
            foreach (LockedArtifact artifact in Artifacts)
            {
                if (string.Equals(artifact.Rid, rid, StringComparison.OrdinalIgnoreCase))
                {
                    return artifact;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The committed record of exactly which driver bytes a build may use.
    /// </summary>
    /// <remarks>
    /// This file, not the registry, is the build's source of truth. <c>Build</c> and
    /// <c>Publish</c> read it and never rewrite it, so a mutable upstream index cannot
    /// change what a build produces; only the explicit resolve step updates it, and that
    /// shows up as a reviewable diff.
    /// </remarks>
    internal sealed class DriverLock
    {
        public const int CurrentSchemaVersion = 1;

        public DriverLock(int schemaVersion, IReadOnlyList<string> registries, IReadOnlyList<LockedDriver> drivers)
        {
            SchemaVersion = schemaVersion;
            Registries = registries;
            Drivers = drivers;
        }

        public int SchemaVersion { get; }

        /// <summary>Registries the entries were resolved from, recorded for provenance.</summary>
        public IReadOnlyList<string> Registries { get; }

        public IReadOnlyList<LockedDriver> Drivers { get; }

        public static DriverLock Empty { get; } =
            new DriverLock(CurrentSchemaVersion, Array.Empty<string>(), Array.Empty<LockedDriver>());

        public LockedDriver? FindDriver(string id)
        {
            foreach (LockedDriver driver in Drivers)
            {
                if (string.Equals(driver.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return driver;
                }
            }

            return null;
        }

        public static DriverLock Load(string path)
        {
            if (path is null) throw new ArgumentNullException(nameof(path));

            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new DriverLockException($"The driver lock file '{path}' could not be read: {ex.Message}", ex);
            }

            return ParseJson(text, path);
        }

        internal static DriverLock ParseJson(string text, string path)
        {
            JsonValue root;
            try
            {
                root = JsonParser.Parse(text);
            }
            catch (JsonParseException ex)
            {
                throw new DriverLockException($"The driver lock file '{path}' is not valid JSON: {ex.Message}", ex);
            }

            int? schemaVersion = root["schemaVersion"].AsInt32();
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new DriverLockException(
                    $"The driver lock file '{path}' declares schemaVersion {schemaVersion?.ToString() ?? "(none)"}; this version of Adbc.Drivers.Build understands {CurrentSchemaVersion}.");
            }

            List<string> registries = new List<string>();
            foreach (JsonValue registry in root["registries"].AsArray())
            {
                string? value = registry.AsString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    registries.Add(value!);
                }
            }

            List<LockedDriver> drivers = new List<LockedDriver>();
            foreach (JsonValue driver in root["drivers"].AsArray())
            {
                string id = Require(driver, "id", path);
                string version = Require(driver, "version", path);

                List<LockedArtifact> artifacts = new List<LockedArtifact>();
                foreach (JsonValue artifact in driver["artifacts"].AsArray())
                {
                    string rid = Require(artifact, "rid", path, id);
                    artifacts.Add(new LockedArtifact(
                        rid,
                        Require(artifact, "adbcPlatform", path, id),
                        Require(artifact, "url", path, id),
                        ValidateDigest(Require(artifact, "archiveSha256", path, id), "archiveSha256", path, id),
                        artifact["archiveLength"].AsInt64() ?? 0,
                        Require(artifact, "driverFile", path, id),
                        ValidateDigest(Require(artifact, "driverSha256", path, id), "driverSha256", path, id),
                        artifact["signatureFile"].AsString(),
                        artifact["signatureSha256"].AsString(),
                        artifact["signatureKeyFingerprint"].AsString()));
                }

                if (artifacts.Count == 0)
                {
                    throw new DriverLockException(
                        $"The driver lock file '{path}' has no artifacts for driver '{id}'.");
                }

                drivers.Add(new LockedDriver(
                    id,
                    version,
                    driver["name"].AsString(),
                    driver["publisher"].AsString(),
                    driver["license"].AsString(),
                    driver["adbcVersion"].AsString(),
                    driver["entrypoint"].AsString(),
                    artifacts));
            }

            return new DriverLock(schemaVersion.Value, registries, drivers);
        }

        /// <summary>
        /// Serializes the lock with drivers and artifacts in a stable order, so that
        /// re-resolving an unchanged configuration produces an unchanged file.
        /// </summary>
        public string ToJson()
        {
            List<LockedDriver> orderedDrivers = new List<LockedDriver>(Drivers);
            orderedDrivers.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id));

            List<string> orderedRegistries = new List<string>(Registries);
            orderedRegistries.Sort(StringComparer.Ordinal);

            JsonTextWriter writer = new JsonTextWriter();
            writer.StartObject();
            writer.Property("schemaVersion", SchemaVersion);

            writer.Name("registries").StartArray();
            foreach (string registry in orderedRegistries)
            {
                writer.String(registry);
            }

            writer.EndArray();

            writer.Name("drivers").StartArray();
            foreach (LockedDriver driver in orderedDrivers)
            {
                List<LockedArtifact> orderedArtifacts = new List<LockedArtifact>(driver.Artifacts);
                orderedArtifacts.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Rid, b.Rid));

                writer.StartObject();
                writer.Property("id", driver.Id);
                writer.Property("version", driver.Version);
                writer.Property("name", driver.Name);
                writer.Property("publisher", driver.Publisher);
                writer.Property("license", driver.License);
                writer.Property("adbcVersion", driver.AdbcVersion);
                writer.Property("entrypoint", driver.Entrypoint);

                writer.Name("artifacts").StartArray();
                foreach (LockedArtifact artifact in orderedArtifacts)
                {
                    writer.StartObject();
                    writer.Property("rid", artifact.Rid);
                    writer.Property("adbcPlatform", artifact.AdbcPlatform);
                    writer.Property("url", artifact.Url);
                    writer.Property("archiveSha256", artifact.ArchiveSha256);
                    writer.Property("archiveLength", artifact.ArchiveLength);
                    writer.Property("driverFile", artifact.DriverFile);
                    writer.Property("driverSha256", artifact.DriverSha256);
                    writer.Property("signatureFile", artifact.SignatureFile);
                    writer.Property("signatureSha256", artifact.SignatureSha256);
                    writer.Property("signatureKeyFingerprint", artifact.SignatureKeyFingerprint);
                    writer.EndObject();
                }

                writer.EndArray();
                writer.EndObject();
            }

            writer.EndArray();
            writer.EndObject();
            return writer.ToString() + "\n";
        }

        /// <summary>
        /// Writes the lock atomically, and only when the content differs, so that an
        /// unchanged resolve does not touch the file's timestamp and retrigger builds.
        /// </summary>
        /// <returns>True when the file was changed.</returns>
        public bool Save(string path)
        {
            if (path is null) throw new ArgumentNullException(nameof(path));

            string json = ToJson();
            string full = Path.GetFullPath(path);

            if (File.Exists(full) && string.Equals(File.ReadAllText(full, Encoding.UTF8), json, StringComparison.Ordinal))
            {
                return false;
            }

            string? directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            // Same-directory temporary file so the replace is a same-volume move.
            string temporary = full + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            try
            {
                if (File.Exists(full))
                {
                    File.Delete(full);
                }

                File.Move(temporary, full);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }

            return true;
        }

        /// <summary>
        /// SHA-256 of the canonical serialization. Suitable as an MSBuild incrementality
        /// input and as a CI cache key.
        /// </summary>
        public string ComputeDigest() => Hashing.Sha256Bytes(new UTF8Encoding(false).GetBytes(ToJson()));

        private static string Require(JsonValue owner, string name, string path, string? driverId = null)
        {
            string? value = owner[name].AsString();
            if (string.IsNullOrWhiteSpace(value))
            {
                string where = driverId is null ? string.Empty : $" for driver '{driverId}'";
                throw new DriverLockException($"The driver lock file '{path}' is missing '{name}'{where}.");
            }

            return value!.Trim();
        }

        private static string ValidateDigest(string value, string name, string path, string driverId)
        {
            string trimmed = value.Trim();
            if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring("sha256:".Length);
            }

            if (trimmed.Length != 64 || !Hex.TryParse(trimmed, out _))
            {
                throw new DriverLockException(
                    $"The driver lock file '{path}' has a '{name}' value for driver '{driverId}' that is not a hexadecimal SHA-256 hash.");
            }

            return trimmed.ToLowerInvariant();
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
