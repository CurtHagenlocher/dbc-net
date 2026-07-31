using System;
using System.Collections.Generic;
using System.IO;
using Adbc.Drivers.Build.Acquisition;
using Adbc.Drivers.Build.Packaging;
using Adbc.Drivers.Build.Tests.TestSupport;
using Adbc.Drivers.Build.Text;
using Xunit;

namespace Adbc.Drivers.Build.Tests
{
    public sealed class RuntimeManifestGeneratorTests
    {
        private static DeploymentPlan Plan(string? entrypoint = null, string? adbcVersion = "v1.1.0") =>
            new DeploymentPlan(new[]
            {
                new DeployedDriver(
                    "snowflake",
                    "1.11.0",
                    "snowflake",
                    "ASF Snowflake Driver",
                    "ADBC Drivers Contributors",
                    "Apache-2.0",
                    adbcVersion,
                    entrypoint,
                    new[]
                    {
                        new DeployedArtifact("win-x64", "windows_amd64", "snowflake/1.11.0/win-x64", "libadbc_driver_snowflake.dll"),
                        new DeployedArtifact("linux-x64", "linux_amd64", "snowflake/1.11.0/linux-x64", "libadbc_driver_snowflake.so"),
                    }),
            });

        [Fact]
        public void WritesAPlatformMapOfAbsolutePaths()
        {
            using TempDirectory temp = new TempDirectory("manifest");
            string root = temp.Combine("bin", "adbc");

            IReadOnlyList<string> written = RuntimeManifestGenerator.Generate(Plan(), root, root, useRelativePaths: false);

            string path = Assert.Single(written);
            Assert.Equal("snowflake.toml", Path.GetFileName(path));

            TomlTable manifest = TomlParser.Parse(File.ReadAllText(path));
            TomlTable shared = manifest.GetTablePath("Driver", "shared")!;

            Assert.Equal(
                Path.Combine(root, "snowflake", "1.11.0", "win-x64", "libadbc_driver_snowflake.dll"),
                shared.GetString("windows_amd64"));
            Assert.Equal(
                Path.Combine(root, "snowflake", "1.11.0", "linux-x64", "libadbc_driver_snowflake.so"),
                shared.GetString("linux_amd64"));
        }

        [Fact]
        public void WritesTheRequiredIdentityFields()
        {
            using TempDirectory temp = new TempDirectory("manifest");
            string root = temp.Combine("adbc");

            RuntimeManifestGenerator.Generate(Plan(), root, root, useRelativePaths: false);
            TomlTable manifest = TomlParser.Parse(File.ReadAllText(Path.Combine(root, "snowflake.toml")));

            Assert.Equal(1, manifest.GetInt32("manifest_version"));
            Assert.Equal("ASF Snowflake Driver", manifest.GetString("name"));
            Assert.Equal("1.11.0", manifest.GetString("version"));
            Assert.Equal("ADBC Drivers Contributors", manifest.GetString("publisher"));
            Assert.Equal("Apache-2.0", manifest.GetString("license"));
        }

        [Fact]
        public void RecordsWhatGeneratedTheManifest()
        {
            // The format has a 'source' field for exactly this, surfaced as
            // DriverManifest.Source, so a deployed driver says where it came from without
            // anyone needing access to the build that produced it.
            using TempDirectory temp = new TempDirectory("manifest");
            string root = temp.Combine("adbc");

            RuntimeManifestGenerator.Generate(Plan(), root, root, useRelativePaths: true);
            TomlTable manifest = TomlParser.Parse(File.ReadAllText(Path.Combine(root, "snowflake.toml")));

            Assert.Equal("Adbc.Drivers.Build", manifest.GetString("source"));
        }

        [Fact]
        public void StripsTheVPrefixFromTheAdbcVersion()
        {
            using TempDirectory temp = new TempDirectory("manifest");
            string root = temp.Combine("adbc");

            RuntimeManifestGenerator.Generate(Plan(adbcVersion: "v1.1.0"), root, root, useRelativePaths: false);
            TomlTable manifest = TomlParser.Parse(File.ReadAllText(Path.Combine(root, "snowflake.toml")));

            Assert.Equal("1.1.0", manifest.GetTable("ADBC")?.GetString("version"));
        }

        [Fact]
        public void OmitsTheEntrypointWhenTheArchiveDidNotDeclareOne()
        {
            // Guessing "AdbcDriverInit" would override the driver manager's own default
            // resolution and could name a symbol the library does not export.
            using TempDirectory temp = new TempDirectory("manifest");
            string root = temp.Combine("adbc");

            RuntimeManifestGenerator.Generate(Plan(entrypoint: null), root, root, useRelativePaths: false);
            TomlTable manifest = TomlParser.Parse(File.ReadAllText(Path.Combine(root, "snowflake.toml")));

            Assert.Null(manifest.GetTable("Driver")?.GetString("entrypoint"));
            Assert.NotNull(manifest.GetTablePath("Driver", "shared"));
        }

        [Fact]
        public void WritesTheEntrypointWhenItIsKnown()
        {
            using TempDirectory temp = new TempDirectory("manifest");
            string root = temp.Combine("adbc");

            RuntimeManifestGenerator.Generate(Plan(entrypoint: "AdbcDriverInit"), root, root, useRelativePaths: false);
            TomlTable manifest = TomlParser.Parse(File.ReadAllText(Path.Combine(root, "snowflake.toml")));

            Assert.Equal("AdbcDriverInit", manifest.GetTable("Driver")?.GetString("entrypoint"));
        }

        [Fact]
        public void WritesRelativePathsOnlyWhenAskedTo()
        {
            using TempDirectory temp = new TempDirectory("manifest");
            string root = temp.Combine("adbc");

            RuntimeManifestGenerator.Generate(Plan(), root, root, useRelativePaths: true);
            TomlTable manifest = TomlParser.Parse(File.ReadAllText(Path.Combine(root, "snowflake.toml")));

            Assert.Equal(
                "snowflake/1.11.0/win-x64/libadbc_driver_snowflake.dll",
                manifest.GetTablePath("Driver", "shared")?.GetString("windows_amd64"));
        }

        [Fact]
        public void PointsAtTheDeploymentRootEvenWhenStagedElsewhere()
        {
            // Publish stages manifests in obj but their paths must target the publish
            // directory, which is what makes a published manifest usable.
            using TempDirectory temp = new TempDirectory("manifest");
            string deploymentRoot = temp.Combine("publish", "adbc");
            string staging = temp.Combine("obj", "publish-manifests");

            RuntimeManifestGenerator.Generate(Plan(), deploymentRoot, staging, useRelativePaths: false);

            string path = Path.Combine(staging, "snowflake.toml");
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(Path.Combine(deploymentRoot, "snowflake.toml")));

            Assert.Equal(
                Path.Combine(deploymentRoot, "snowflake", "1.11.0", "win-x64", "libadbc_driver_snowflake.dll"),
                TomlParser.Parse(File.ReadAllText(path)).GetTablePath("Driver", "shared")?.GetString("windows_amd64"));
        }

        [Fact]
        public void LeavesAnUnchangedManifestUntouched()
        {
            using TempDirectory temp = new TempDirectory("manifest");
            string root = temp.Combine("adbc");

            RuntimeManifestGenerator.Generate(Plan(), root, root, useRelativePaths: false);
            string path = Path.Combine(root, "snowflake.toml");
            DateTime firstWrite = File.GetLastWriteTimeUtc(path);

            RuntimeManifestGenerator.Generate(Plan(), root, root, useRelativePaths: false);

            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(path));
        }

        [Fact]
        public void RejectsTwoDriversThatWouldShareAManifestName()
        {
            using TempDirectory temp = new TempDirectory("manifest");
            DeploymentPlan plan = new DeploymentPlan(new[]
            {
                new DeployedDriver("a", "1.0.0", "shared", null, null, null, null, null,
                    new[] { new DeployedArtifact("win-x64", "windows_amd64", "a/1.0.0/win-x64", "a.dll") }),
                new DeployedDriver("b", "1.0.0", "shared", null, null, null, null, null,
                    new[] { new DeployedArtifact("win-x64", "windows_amd64", "b/1.0.0/win-x64", "b.dll") }),
            });

            PackageManifestException ex = Assert.Throws<PackageManifestException>(
                () => RuntimeManifestGenerator.Generate(plan, temp.Path, temp.Path, useRelativePaths: false));
            Assert.Contains("ManifestName", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RoundTripsTheDeploymentPlan()
        {
            using TempDirectory temp = new TempDirectory("plan");
            string path = temp.Combine("deployment-plan.json");

            Assert.True(Plan("AdbcDriverInit").Save(path));

            // An unchanged save leaves the file alone.
            Assert.False(Plan("AdbcDriverInit").Save(path));

            DeploymentPlan loaded = DeploymentPlan.Load(path);
            DeployedDriver driver = Assert.Single(loaded.Drivers);

            Assert.Equal("snowflake", driver.Id);
            Assert.Equal("1.11.0", driver.Version);
            Assert.Equal("AdbcDriverInit", driver.Entrypoint);
            Assert.Equal(2, driver.Artifacts.Count);
            Assert.Equal(
                "snowflake/1.11.0/win-x64/libadbc_driver_snowflake.dll",
                driver.Artifacts[0].RelativeDriverPath);
        }

        [Fact]
        public void RejectsAnUnknownDeploymentPlanSchemaVersion()
        {
            using TempDirectory temp = new TempDirectory("plan");
            string path = temp.Combine("plan.json");
            File.WriteAllText(path, "{\"schemaVersion\": 42, \"drivers\": []}");

            Assert.Throws<InvalidDataException>(() => DeploymentPlan.Load(path));
        }
    }
}
