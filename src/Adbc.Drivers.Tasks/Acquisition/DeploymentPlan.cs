using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Adbc.Drivers.Build.Text;

namespace Adbc.Drivers.Build.Acquisition
{
    internal sealed class DeployedArtifact
    {
        public DeployedArtifact(string rid, string adbcPlatform, string relativeDirectory, string driverFile)
        {
            Rid = rid;
            AdbcPlatform = adbcPlatform;
            RelativeDirectory = relativeDirectory;
            DriverFile = driverFile;
        }

        public string Rid { get; }

        public string AdbcPlatform { get; }

        /// <summary>
        /// Forward-slash directory, relative to the deployed <c>adbc</c> folder, holding
        /// this artifact's files.
        /// </summary>
        public string RelativeDirectory { get; }

        /// <summary>File name of the shared library within <see cref="RelativeDirectory"/>.</summary>
        public string DriverFile { get; }

        public string RelativeDriverPath => RelativeDirectory + "/" + DriverFile;
    }

    internal sealed class DeployedDriver
    {
        public DeployedDriver(
            string id,
            string version,
            string manifestName,
            string? name,
            string? publisher,
            string? license,
            string? adbcVersion,
            string? entrypoint,
            IReadOnlyList<DeployedArtifact> artifacts)
        {
            Id = id;
            Version = version;
            ManifestName = manifestName;
            Name = name;
            Publisher = publisher;
            License = license;
            AdbcVersion = adbcVersion;
            Entrypoint = entrypoint;
            Artifacts = artifacts;
        }

        public string Id { get; }

        public string Version { get; }

        /// <summary>Base name of the generated <c>.toml</c> runtime manifest.</summary>
        public string ManifestName { get; }

        public string? Name { get; }

        public string? Publisher { get; }

        public string? License { get; }

        public string? AdbcVersion { get; }

        public string? Entrypoint { get; }

        public IReadOnlyList<DeployedArtifact> Artifacts { get; }
    }

    /// <summary>
    /// What was staged for deployment, handed from the acquire task to the manifest
    /// generator.
    /// </summary>
    /// <remarks>
    /// Passed as a file rather than as MSBuild item metadata because the RID-to-driver
    /// mapping is a list per driver, and encoding a list inside an item metadata string
    /// would be both lossy and hard to debug. The file also makes the intermediate state
    /// inspectable when a build goes wrong.
    /// </remarks>
    internal sealed class DeploymentPlan
    {
        public const int CurrentSchemaVersion = 1;

        public DeploymentPlan(IReadOnlyList<DeployedDriver> drivers)
        {
            Drivers = drivers;
        }

        public IReadOnlyList<DeployedDriver> Drivers { get; }

        public static DeploymentPlan Load(string path)
        {
            if (path is null) throw new ArgumentNullException(nameof(path));

            JsonValue root = JsonParser.Parse(File.ReadAllText(path, Encoding.UTF8));
            int? schemaVersion = root["schemaVersion"].AsInt32();
            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"'{path}' declares deployment plan schema version {schemaVersion?.ToString() ?? "(none)"}; only version {CurrentSchemaVersion} is understood.");
            }

            List<DeployedDriver> drivers = new List<DeployedDriver>();
            foreach (JsonValue driver in root["drivers"].AsArray())
            {
                List<DeployedArtifact> artifacts = new List<DeployedArtifact>();
                foreach (JsonValue artifact in driver["artifacts"].AsArray())
                {
                    artifacts.Add(new DeployedArtifact(
                        Require(artifact, "rid", path),
                        Require(artifact, "adbcPlatform", path),
                        Require(artifact, "relativeDirectory", path),
                        Require(artifact, "driverFile", path)));
                }

                drivers.Add(new DeployedDriver(
                    Require(driver, "id", path),
                    Require(driver, "version", path),
                    Require(driver, "manifestName", path),
                    driver["name"].AsString(),
                    driver["publisher"].AsString(),
                    driver["license"].AsString(),
                    driver["adbcVersion"].AsString(),
                    driver["entrypoint"].AsString(),
                    artifacts));
            }

            return new DeploymentPlan(drivers);
        }

        public string ToJson()
        {
            JsonTextWriter writer = new JsonTextWriter();
            writer.StartObject();
            writer.Property("schemaVersion", CurrentSchemaVersion);
            writer.Name("drivers").StartArray();
            foreach (DeployedDriver driver in Drivers)
            {
                writer.StartObject();
                writer.Property("id", driver.Id);
                writer.Property("version", driver.Version);
                writer.Property("manifestName", driver.ManifestName);
                writer.Property("name", driver.Name);
                writer.Property("publisher", driver.Publisher);
                writer.Property("license", driver.License);
                writer.Property("adbcVersion", driver.AdbcVersion);
                writer.Property("entrypoint", driver.Entrypoint);
                writer.Name("artifacts").StartArray();
                foreach (DeployedArtifact artifact in driver.Artifacts)
                {
                    writer.StartObject();
                    writer.Property("rid", artifact.Rid);
                    writer.Property("adbcPlatform", artifact.AdbcPlatform);
                    writer.Property("relativeDirectory", artifact.RelativeDirectory);
                    writer.Property("driverFile", artifact.DriverFile);
                    writer.EndObject();
                }

                writer.EndArray();
                writer.EndObject();
            }

            writer.EndArray();
            writer.EndObject();
            return writer.ToString() + "\n";
        }

        /// <summary>Writes the plan, leaving the file untouched when nothing changed.</summary>
        public bool Save(string path)
        {
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

            File.WriteAllText(full, json, new UTF8Encoding(false));
            return true;
        }

        private static string Require(JsonValue owner, string name, string path)
        {
            string? value = owner[name].AsString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException($"'{path}' is missing '{name}'.");
            }

            return value!;
        }
    }
}
