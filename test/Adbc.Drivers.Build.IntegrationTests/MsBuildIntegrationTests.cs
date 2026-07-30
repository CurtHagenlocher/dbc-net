using System;
using System.IO;
using Adbc.Drivers.Build.IntegrationTests.TestSupport;
using Adbc.Drivers.Build.Tests.TestSupport;
using Xunit;

namespace Adbc.Drivers.Build.IntegrationTests
{
    /// <summary>
    /// Builds real projects with the packed NuGet package against a local
    /// <c>file://</c> fixture registry, so the default run is deterministic, offline,
    /// and independent of any third-party service.
    /// </summary>
    [Collection("msbuild")]
    public sealed class MsBuildIntegrationTests
    {
        private const string DriverFileName = "libadbc_driver_fixture.dll";
        private const string DriverContent = "fixture driver payload";

        private static string DriverItem(string extraMetadata = "") =>
            $"    <AdbcDriver Include=\"fixture\" Version=\"1.0.0\" Rids=\"win-x64\"{extraMetadata} />\n";

        /// <summary>
        /// Publishes a fixture registry inside the consumer's temporary directory and
        /// resolves a lock file against it.
        /// </summary>
        private static string ResolveAgainstFixtureRegistry(
            ConsumerProject consumer,
            string version = "v1.0.0",
            string? entrypoint = null)
        {
            FixtureRegistry registry = new FixtureRegistry(Path.Combine(consumer.ProjectDirectory, "..", "registry"));
            registry.AddDriver("fixture", "Fixture ADBC Driver")
                .AddRelease(version)
                .AddPackage("windows_amd64", DriverFileName, DriverContent, entrypoint: entrypoint);
            registry.Write();

            BuildResult resolve = consumer.Run(
                "build -t:ResolveAdbcDriverLock",
                $"-p:AdbcDriverRegistries={registry.BaseUri.AbsoluteUri}");

            Assert.True(resolve.Succeeded, resolve.ToString());
            Assert.True(File.Exists(consumer.LockFile), "The resolve target did not write a lock file.");
            return registry.BaseUri.AbsoluteUri;
        }

        [Fact]
        public void ResolveWritesALockFileWithExactVersionsAndHashes()
        {
            using ConsumerProject consumer = new ConsumerProject("resolve");
            consumer.WriteProject(DriverItem());

            ResolveAgainstFixtureRegistry(consumer);

            string lockJson = File.ReadAllText(consumer.LockFile);
            Assert.Contains("\"id\": \"fixture\"", lockJson, StringComparison.Ordinal);
            Assert.Contains("\"version\": \"1.0.0\"", lockJson, StringComparison.Ordinal);
            Assert.Contains("\"rid\": \"win-x64\"", lockJson, StringComparison.Ordinal);
            Assert.Contains("\"adbcPlatform\": \"windows_amd64\"", lockJson, StringComparison.Ordinal);
            Assert.Contains("\"archiveSha256\"", lockJson, StringComparison.Ordinal);
            Assert.Contains("\"driverSha256\"", lockJson, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildDeploysDriversAndAGeneratedManifest()
        {
            using ConsumerProject consumer = new ConsumerProject("build");
            consumer.WriteProject(DriverItem());
            ResolveAgainstFixtureRegistry(consumer);

            // Default CacheOnly: the resolve step already populated the cache, so this
            // build never touches the network.
            BuildResult build = consumer.Run("build");
            Assert.True(build.Succeeded, build.ToString());

            Assert.True(
                consumer.OutputFileExists("adbc", "fixture", "1.0.0", "win-x64", DriverFileName),
                build.ToString());
            Assert.Equal(
                DriverContent,
                consumer.ReadOutputFile("adbc", "fixture", "1.0.0", "win-x64", DriverFileName));

            // Licences and notices travel with the driver.
            Assert.True(consumer.OutputFileExists("adbc", "fixture", "1.0.0", "win-x64", "LICENSE"));
            Assert.True(consumer.OutputFileExists("adbc", "fixture", "1.0.0", "win-x64", "NOTICE"));

            string manifest = consumer.ReadOutputFile("adbc", "fixture.toml");
            Assert.Contains("manifest_version = 1", manifest, StringComparison.Ordinal);
            Assert.Contains("[Driver.shared]", manifest, StringComparison.Ordinal);

            // The manifest must point at the driver that was actually deployed.
            string expected = Path.Combine(
                consumer.OutputDirectory, "adbc", "fixture", "1.0.0", "win-x64", DriverFileName);
            Assert.Contains(expected.Replace("\\", "\\\\"), manifest, StringComparison.Ordinal);
        }

        [Fact]
        public void PublishIncludesDriversAndAManifestPointingAtThePublishDirectory()
        {
            using ConsumerProject consumer = new ConsumerProject("publish");
            consumer.WriteProject(DriverItem());
            ResolveAgainstFixtureRegistry(consumer);

            BuildResult publish = consumer.Run("publish -c Debug -f net8.0");
            Assert.True(publish.Succeeded, publish.ToString());

            Assert.True(
                consumer.PublishFileExists("adbc", "fixture", "1.0.0", "win-x64", DriverFileName),
                publish.ToString());
            Assert.True(consumer.PublishFileExists("adbc", "fixture.toml"));

            // A manifest baked for $(TargetDir) would be wrong here, which is why publish
            // generates its own.
            string manifest = File.ReadAllText(
                Path.Combine(consumer.PublishDirectory, "adbc", "fixture.toml"));
            string expected = Path.Combine(
                consumer.PublishDirectory, "adbc", "fixture", "1.0.0", "win-x64", DriverFileName);
            Assert.Contains(expected.Replace("\\", "\\\\"), manifest, StringComparison.Ordinal);
        }

        [Fact]
        public void CacheOnlyBuildFailsWithAnActionableMessageWhenTheCacheIsEmpty()
        {
            using ConsumerProject consumer = new ConsumerProject("cacheonly");
            consumer.WriteProject(DriverItem());
            ResolveAgainstFixtureRegistry(consumer);

            // Exactly the CI situation: a committed lock, but no warm cache.
            TempDirectory.DeleteRecursive(consumer.DriverCacheDirectory);

            BuildResult build = consumer.Run("build");

            Assert.False(build.Succeeded, "A CacheOnly build with an empty cache must fail.");
            Assert.Contains("not in the driver cache", build.Output, StringComparison.Ordinal);
            Assert.Contains("AdbcDriverNetworkMode", build.Output, StringComparison.Ordinal);
        }

        [Fact]
        public void OnlineBuildRepopulatesAnEmptyCache()
        {
            using ConsumerProject consumer = new ConsumerProject("online");
            consumer.WriteProject(DriverItem());
            ResolveAgainstFixtureRegistry(consumer);
            TempDirectory.DeleteRecursive(consumer.DriverCacheDirectory);

            BuildResult build = consumer.Run("build", "-p:AdbcDriverNetworkMode=Online");

            Assert.True(build.Succeeded, build.ToString());
            Assert.True(consumer.OutputFileExists("adbc", "fixture", "1.0.0", "win-x64", DriverFileName));
        }

        [Fact]
        public void BuildFailsWhenTheLockFileIsMissing()
        {
            using ConsumerProject consumer = new ConsumerProject("nolock");
            consumer.WriteProject(DriverItem());

            BuildResult build = consumer.Run("build");

            Assert.False(build.Succeeded, "A build with no lock file must fail rather than resolve silently.");
            Assert.Contains("ResolveAdbcDriverLock", build.Output, StringComparison.Ordinal);
        }

        [Fact]
        public void BuildFailsWhenTheLockedHashDoesNotMatchTheArchive()
        {
            using ConsumerProject consumer = new ConsumerProject("tampered");
            consumer.WriteProject(DriverItem());
            ResolveAgainstFixtureRegistry(consumer);
            TempDirectory.DeleteRecursive(consumer.DriverCacheDirectory);

            // Simulates the registry serving different bytes than the lock was reviewed
            // against.
            string lockJson = File.ReadAllText(consumer.LockFile);
            int index = lockJson.IndexOf("\"archiveSha256\": \"", StringComparison.Ordinal)
                + "\"archiveSha256\": \"".Length;
            string tampered = lockJson.Substring(0, index) + new string('a', 64) + lockJson.Substring(index + 64);
            File.WriteAllText(consumer.LockFile, tampered);

            BuildResult build = consumer.Run("build", "-p:AdbcDriverNetworkMode=Online");

            Assert.False(build.Succeeded, "A hash mismatch must fail the build.");
            Assert.Contains("Refusing to use it", build.Output, StringComparison.Ordinal);
        }

        [Fact]
        public void RefreshLockIsRejectedDuringAnOrdinaryBuild()
        {
            using ConsumerProject consumer = new ConsumerProject("refresh");
            consumer.WriteProject(DriverItem());
            ResolveAgainstFixtureRegistry(consumer);

            BuildResult build = consumer.Run("build", "-p:AdbcDriverNetworkMode=RefreshLock");

            Assert.False(build.Succeeded, "RefreshLock must never apply to an ordinary build.");
            Assert.Contains("ResolveAdbcDriverLock", build.Output, StringComparison.Ordinal);
        }

        [Fact]
        public void RebuildIsIncrementalAndStable()
        {
            using ConsumerProject consumer = new ConsumerProject("incremental");
            consumer.WriteProject(DriverItem());
            ResolveAgainstFixtureRegistry(consumer);

            Assert.True(consumer.Run("build").Succeeded);
            string deployed = Path.Combine(
                consumer.OutputDirectory, "adbc", "fixture", "1.0.0", "win-x64", DriverFileName);
            DateTime firstWrite = File.GetLastWriteTimeUtc(deployed);

            BuildResult second = consumer.Run("build");

            Assert.True(second.Succeeded, second.ToString());
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(deployed));
        }

        [Fact]
        public void HonoursCopyToBuildOutputFalse()
        {
            using ConsumerProject consumer = new ConsumerProject("nocopy");
            consumer.WriteProject(DriverItem(" CopyToBuildOutput=\"false\""));
            ResolveAgainstFixtureRegistry(consumer);

            BuildResult build = consumer.Run("build");

            Assert.True(build.Succeeded, build.ToString());
            Assert.False(consumer.OutputFileExists("adbc", "fixture", "1.0.0", "win-x64", DriverFileName));
        }

        [Fact]
        public void HonoursACustomOutputSubdirectoryAndManifestName()
        {
            using ConsumerProject consumer = new ConsumerProject("custom");
            consumer.WriteProject(
                DriverItem(" ManifestName=\"my-driver\""),
                extraProperties: "    <AdbcDriverOutputSubdirectory>drivers</AdbcDriverOutputSubdirectory>\n");
            ResolveAgainstFixtureRegistry(consumer);

            BuildResult build = consumer.Run("build");

            Assert.True(build.Succeeded, build.ToString());
            Assert.True(consumer.OutputFileExists("drivers", "fixture", "1.0.0", "win-x64", DriverFileName));
            Assert.True(consumer.OutputFileExists("drivers", "my-driver.toml"));
        }

        [Fact]
        public void EmitsRelativeManifestPathsOnlyWhenOptedIn()
        {
            using ConsumerProject consumer = new ConsumerProject("relative");
            consumer.WriteProject(DriverItem());
            ResolveAgainstFixtureRegistry(consumer);

            BuildResult build = consumer.Run("build", "-p:AdbcDriverRelativeManifestPaths=true");

            Assert.True(build.Succeeded, build.ToString());
            string manifest = consumer.ReadOutputFile("adbc", "fixture.toml");
            Assert.Contains(
                "windows_amd64 = \"fixture/1.0.0/win-x64/" + DriverFileName + "\"",
                manifest,
                StringComparison.Ordinal);
        }

        [Fact]
        public void ReportsAnUnmappableRuntimeIdentifier()
        {
            using ConsumerProject consumer = new ConsumerProject("badrid");
            consumer.WriteProject("    <AdbcDriver Include=\"fixture\" Version=\"1.0.0\" Rids=\"win-x86\" />\n");

            BuildResult build = consumer.Run("build -t:ResolveAdbcDriverLock");

            Assert.False(build.Succeeded, "An unmappable RID must fail rather than be silently skipped.");
            Assert.Contains("does not map to an ADBC platform", build.Output, StringComparison.Ordinal);
        }

        [Fact]
        public void DoesNotDeployDriversWhenPacking()
        {
            // Redistributing a native driver inside another package needs rights this
            // package cannot grant, so `dotnet pack` must not sweep drivers in.
            using ConsumerProject consumer = new ConsumerProject("packing");
            consumer.WriteProject(
                DriverItem(),
                extraProperties: "    <IsPackable>true</IsPackable>\n    <Version>1.0.0</Version>\n");
            ResolveAgainstFixtureRegistry(consumer);

            BuildResult pack = consumer.Run("pack -c Debug");

            Assert.True(pack.Succeeded, pack.ToString());
            Assert.False(
                consumer.OutputFileExists("adbc", "fixture", "1.0.0", "win-x64", DriverFileName),
                "Driver files must not be deployed during pack.");
            Assert.False(
                consumer.OutputFileExists("adbc", "fixture.toml"),
                "Driver manifests must not be generated during pack.");
        }

        [Fact]
        public void DeploysPerRuntimeIdentifierForAMultiPlatformProject()
        {
            using ConsumerProject consumer = new ConsumerProject("multirid");
            consumer.WriteProject("    <AdbcDriver Include=\"fixture\" Version=\"1.0.0\" Rids=\"win-x64;linux-x64\" />\n");

            FixtureRegistry registry = new FixtureRegistry(Path.Combine(consumer.ProjectDirectory, "..", "registry"));
            registry.AddDriver("fixture", "Fixture ADBC Driver")
                .AddRelease("v1.0.0")
                .AddPackage("windows_amd64", DriverFileName, "windows payload")
                .AddPackage("linux_amd64", "libadbc_driver_fixture.so", "linux payload");
            registry.Write();

            Assert.True(consumer.Run(
                "build -t:ResolveAdbcDriverLock",
                $"-p:AdbcDriverRegistries={registry.BaseUri.AbsoluteUri}").Succeeded);

            BuildResult build = consumer.Run("build");
            Assert.True(build.Succeeded, build.ToString());

            // Separate directories per RID, so the two packages' MANIFEST and LICENSE
            // files cannot collide.
            Assert.Equal("windows payload", consumer.ReadOutputFile("adbc", "fixture", "1.0.0", "win-x64", DriverFileName));
            Assert.Equal("linux payload", consumer.ReadOutputFile("adbc", "fixture", "1.0.0", "linux-x64", "libadbc_driver_fixture.so"));

            // One manifest maps both platforms.
            string manifest = consumer.ReadOutputFile("adbc", "fixture.toml");
            Assert.Contains("windows_amd64 = ", manifest, StringComparison.Ordinal);
            Assert.Contains("linux_amd64 = ", manifest, StringComparison.Ordinal);
        }
    }
}
