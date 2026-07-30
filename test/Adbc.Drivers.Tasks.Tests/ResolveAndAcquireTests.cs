using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Adbc.Drivers.Build.Acquisition;
using Adbc.Drivers.Build.Caching;
using Adbc.Drivers.Build.Locking;
using Adbc.Drivers.Build.Registry;
using Adbc.Drivers.Build.Security;
using Adbc.Drivers.Build.Tests.TestSupport;
using Xunit;

namespace Adbc.Drivers.Build.Tests
{
    /// <summary>
    /// End-to-end resolution and acquisition against a <c>file://</c> fixture registry,
    /// so the default test run is deterministic and needs no network.
    /// </summary>
    public sealed class ResolveAndAcquireTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private static FixtureRegistry BuildRegistry(TempDirectory temp)
        {
            FixtureRegistry registry = new FixtureRegistry(temp.Combine("registry"));

            FixtureRegistry.DriverFixture snowflake = registry.AddDriver("snowflake", "ASF Snowflake Driver");
            snowflake.AddRelease("1.9.0")
                .AddPackage("windows_amd64", "libadbc_driver_snowflake.dll", "old windows driver")
                .AddPackage("linux_amd64", "libadbc_driver_snowflake.so", "old linux driver");
            snowflake.AddRelease("v1.11.0")
                .AddPackage("windows_amd64", "libadbc_driver_snowflake.dll", "windows driver bytes")
                .AddPackage("linux_amd64", "libadbc_driver_snowflake.so", "linux driver bytes");
            snowflake.AddRelease("2.0.0-rc.1")
                .AddPackage("windows_amd64", "libadbc_driver_snowflake.dll", "prerelease driver");

            FixtureRegistry.DriverFixture duckdb = registry.AddDriver("duckdb", "DuckDB Driver", "MIT");
            duckdb.AddRelease("v1.4.2")
                .AddPackage("windows_amd64", "libadbc_driver_duckdb.dll", "duckdb driver", entrypoint: "DuckDbInit");

            return registry.Write();
        }

        private static DriverRequest Request(
            string id,
            string versionSpec,
            params string[] rids) =>
            RequestWithPrerelease(id, versionSpec, null, rids);

        private static DriverRequest RequestWithPrerelease(
            string id,
            string versionSpec,
            bool? allowPrerelease,
            params string[] rids) =>
            new DriverRequest(
                id,
                versionSpec,
                rids,
                manifestName: id,
                entrypoint: null,
                adbcVersion: null,
                platformOverrides: new Dictionary<string, string>(),
                copyToBuildOutput: true,
                copyToPublishDirectory: true,
                allowPrerelease: allowPrerelease);

        private static DriverResolver CreateResolver(TempDirectory temp, List<string>? warnings = null) =>
            new DriverResolver(
                new DefaultRegistryTransport(Timeout),
                new ContentAddressedCache(temp.Combine("cache")),
                NullSignatureVerifier.Instance,
                log: null,
                warn: warnings is null ? null : warnings.Add);

        private static ResolutionOptions Options(FixtureRegistry registry, bool allowPrerelease = false) =>
            new ResolutionOptions
            {
                Registries = new[] { registry.BaseUri },
                AllowPrerelease = allowPrerelease,
                LockTimeout = Timeout,
            };

        [Fact]
        public void ResolvesAnExactVersionAndRecordsHashesAndMetadata()
        {
            using TempDirectory temp = new TempDirectory("resolve");
            FixtureRegistry registry = BuildRegistry(temp);

            DriverLock resolved = CreateResolver(temp).Resolve(
                new[] { Request("snowflake", "1.11.0", "win-x64", "linux-x64") },
                Options(registry));

            LockedDriver driver = Assert.Single(resolved.Drivers);
            Assert.Equal("snowflake", driver.Id);
            Assert.Equal("1.11.0", driver.Version);
            Assert.Equal("Apache-2.0", driver.License);
            Assert.Equal("Fixture Publisher", driver.Publisher);
            Assert.Equal("v1.1.0", driver.AdbcVersion);
            Assert.Equal(2, driver.Artifacts.Count);

            LockedArtifact windows = driver.FindArtifact("win-x64")!;
            Assert.Equal("windows_amd64", windows.AdbcPlatform);
            Assert.Equal("libadbc_driver_snowflake.dll", windows.DriverFile);
            Assert.Equal(64, windows.ArchiveSha256.Length);
            Assert.Equal(64, windows.DriverSha256.Length);
            Assert.True(windows.ArchiveLength > 0);
            Assert.Contains("snowflake_windows_amd64_v1.11.0.tar.gz", windows.Url, StringComparison.Ordinal);

            // No signature check has been made, so no fingerprint is claimed.
            Assert.Null(windows.SignatureKeyFingerprint);
            Assert.Equal("libadbc_driver_snowflake.dll.sig", windows.SignatureFile);
        }

        [Fact]
        public void SelectsTheHighestVersionSatisfyingAConstraint()
        {
            using TempDirectory temp = new TempDirectory("resolve");
            FixtureRegistry registry = BuildRegistry(temp);

            DriverLock resolved = CreateResolver(temp).Resolve(
                new[] { Request("snowflake", "^1.9.0", "win-x64") },
                Options(registry));

            Assert.Equal("1.11.0", resolved.Drivers[0].Version);
        }

        [Fact]
        public void ExcludesPrereleasesUnlessAllowed()
        {
            using TempDirectory temp = new TempDirectory("resolve");
            FixtureRegistry registry = BuildRegistry(temp);

            Assert.Equal(
                "1.11.0",
                CreateResolver(temp).Resolve(new[] { Request("snowflake", "*", "win-x64") }, Options(registry))
                    .Drivers[0].Version);

            Assert.Equal(
                "2.0.0-rc.1",
                CreateResolver(temp).Resolve(
                        new[] { Request("snowflake", "*", "win-x64") },
                        Options(registry, allowPrerelease: true))
                    .Drivers[0].Version);
        }

        [Fact]
        public void PerDriverPrereleaseMetadataOverridesTheProjectWideDefault()
        {
            using TempDirectory temp = new TempDirectory("resolve");
            FixtureRegistry registry = BuildRegistry(temp);

            // One driver opts in without the project-wide flag being set.
            DriverLock resolved = CreateResolver(temp).Resolve(
                new[] { RequestWithPrerelease("snowflake", "*", allowPrerelease: true, "win-x64") },
                Options(registry, allowPrerelease: false));

            Assert.Equal("2.0.0-rc.1", resolved.Drivers[0].Version);
        }

        [Fact]
        public void PerDriverPrereleaseMetadataCanOptOutOfTheProjectWideDefault()
        {
            using TempDirectory temp = new TempDirectory("resolve");
            FixtureRegistry registry = BuildRegistry(temp);

            DriverLock resolved = CreateResolver(temp).Resolve(
                new[] { RequestWithPrerelease("snowflake", "*", allowPrerelease: false, "win-x64") },
                Options(registry, allowPrerelease: true));

            Assert.Equal("1.11.0", resolved.Drivers[0].Version);
        }

        [Fact]
        public void SaysSoWhenADriverHasPublishedOnlyPrereleases()
        {
            // The real registry has drivers in exactly this state — clickhouse ships only
            // v0.1.0-alpha.1 — and "no version satisfies *" is a baffling thing to be told
            // when versions plainly exist.
            using TempDirectory temp = new TempDirectory("resolve");
            FixtureRegistry registry = new FixtureRegistry(temp.Combine("registry"));
            registry.AddDriver("earlybird")
                .AddRelease("v0.1.0-alpha.1")
                .AddPackage("windows_amd64", "d.dll", "prerelease only");
            registry.Write();

            ResolutionException ex = Assert.Throws<ResolutionException>(() => CreateResolver(temp)
                .Resolve(new[] { Request("earlybird", "*", "win-x64") }, Options(registry)));

            Assert.Contains("Only prerelease versions match", ex.Message, StringComparison.Ordinal);
            Assert.Contains("Prerelease=\"allow\"", ex.Message, StringComparison.Ordinal);

            // And opting in resolves it.
            DriverLock resolved = CreateResolver(temp).Resolve(
                new[] { RequestWithPrerelease("earlybird", "*", allowPrerelease: true, "win-x64") },
                Options(registry));
            Assert.Equal("0.1.0-alpha.1", resolved.Drivers[0].Version);
        }

        [Fact]
        public void ProducesAStableLockWhenNothingChanged()
        {
            using TempDirectory temp = new TempDirectory("resolve");
            FixtureRegistry registry = BuildRegistry(temp);

            string first = CreateResolver(temp)
                .Resolve(new[] { Request("snowflake", "1.11.0", "win-x64") }, Options(registry)).ToJson();
            string second = CreateResolver(temp)
                .Resolve(new[] { Request("snowflake", "1.11.0", "win-x64") }, Options(registry)).ToJson();

            Assert.Equal(first, second);
        }

        [Fact]
        public void ReportsAnUnknownDriverWithTheAvailableChoices()
        {
            using TempDirectory temp = new TempDirectory("resolve");
            FixtureRegistry registry = BuildRegistry(temp);

            ResolutionException ex = Assert.Throws<ResolutionException>(() => CreateResolver(temp)
                .Resolve(new[] { Request("postgresql", "1.0.0", "win-x64") }, Options(registry)));

            Assert.Contains("postgresql", ex.Message, StringComparison.Ordinal);
            Assert.Contains("duckdb", ex.Message, StringComparison.Ordinal);
            Assert.Contains("snowflake", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ReportsAnUnsatisfiableConstraintWithThePublishedVersions()
        {
            using TempDirectory temp = new TempDirectory("resolve");
            FixtureRegistry registry = BuildRegistry(temp);

            ResolutionException ex = Assert.Throws<ResolutionException>(() => CreateResolver(temp)
                .Resolve(new[] { Request("snowflake", ">=5.0.0", "win-x64") }, Options(registry)));

            Assert.Contains("1.11.0", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ReportsAMissingPlatformWithTheAvailableOnes()
        {
            using TempDirectory temp = new TempDirectory("resolve");
            FixtureRegistry registry = BuildRegistry(temp);

            ResolutionException ex = Assert.Throws<ResolutionException>(() => CreateResolver(temp)
                .Resolve(new[] { Request("snowflake", "1.11.0", "osx-arm64") }, Options(registry)));

            Assert.Contains("macos_arm64", ex.Message, StringComparison.Ordinal);
            Assert.Contains("windows_amd64", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void WarnsWhenAPackageUrlHadToBeDerived()
        {
            using TempDirectory temp = new TempDirectory("resolve");
            FixtureRegistry registry = new FixtureRegistry(temp.Combine("registry"));
            registry.AddDriver("fixture")
                .AddRelease("v1.0.0")
                .AddPackage("windows_amd64", "d.dll", "x", omitUrlFromIndex: true);
            registry.Write();

            List<string> warnings = new List<string>();
            CreateResolver(temp, warnings).Resolve(
                new[] { Request("fixture", "1.0.0", "win-x64") },
                Options(registry));

            Assert.Contains(warnings, w => w.Contains("conventional location", StringComparison.Ordinal));
        }

        [Fact]
        public void AcquiresLockedDriversIntoTheIntermediateDirectory()
        {
            using TempDirectory temp = new TempDirectory("acquire");
            FixtureRegistry registry = BuildRegistry(temp);
            DriverRequest request = Request("snowflake", "1.11.0", "win-x64", "linux-x64");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            DriverLock resolved = new DriverResolver(
                new DefaultRegistryTransport(Timeout), cache, NullSignatureVerifier.Instance)
                .Resolve(new[] { request }, Options(registry));

            AcquisitionResult result = Acquire(temp, cache, resolved, request, NetworkMode.CacheOnly);

            // Each RID gets its own directory, so a multi-platform deployment cannot have
            // two drivers colliding on the same MANIFEST or LICENSE file name.
            Assert.Equal(
                "snowflake/1.11.0/win-x64",
                result.Plan.Drivers[0].Artifacts.Single(a => a.Rid == "win-x64").RelativeDirectory);

            string windowsDriver = temp.Combine(
                "obj", "snowflake", "1.11.0", "win-x64", "libadbc_driver_snowflake.dll");
            Assert.True(File.Exists(windowsDriver));
            Assert.Equal("windows driver bytes", File.ReadAllText(windowsDriver));

            // Licences and notices travel with the driver.
            Assert.True(File.Exists(temp.Combine("obj", "snowflake", "1.11.0", "win-x64", "LICENSE")));
            Assert.True(File.Exists(temp.Combine("obj", "snowflake", "1.11.0", "linux-x64", "NOTICE")));

            Assert.Equal(10, result.Files.Count);
            Assert.All(result.Files, f => Assert.True(File.Exists(f.SourcePath)));
        }

        [Fact]
        public void LeavesDeployedFilesWritable()
        {
            // Cached files are read-only; the working copies must not be, or the next
            // build's copy fails.
            using TempDirectory temp = new TempDirectory("acquire");
            FixtureRegistry registry = BuildRegistry(temp);
            DriverRequest request = Request("snowflake", "1.11.0", "win-x64");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            DriverLock resolved = new DriverResolver(
                new DefaultRegistryTransport(Timeout), cache, NullSignatureVerifier.Instance)
                .Resolve(new[] { request }, Options(registry));

            AcquisitionResult result = Acquire(temp, cache, resolved, request, NetworkMode.CacheOnly);

            foreach (DeployedFile file in result.Files)
            {
                Assert.False(File.GetAttributes(file.SourcePath).HasFlag(FileAttributes.ReadOnly));
            }

            // A second acquisition over the same destination must succeed.
            Acquire(temp, cache, resolved, request, NetworkMode.CacheOnly);
        }

        [Fact]
        public void FailsCacheOnlyBuildsWithAnActionableMessage()
        {
            using TempDirectory temp = new TempDirectory("acquire");
            FixtureRegistry registry = BuildRegistry(temp);
            DriverRequest request = Request("snowflake", "1.11.0", "win-x64");

            DriverLock resolved = new DriverResolver(
                new DefaultRegistryTransport(Timeout),
                new ContentAddressedCache(temp.Combine("resolve-cache")),
                NullSignatureVerifier.Instance)
                .Resolve(new[] { request }, Options(registry));

            // A different, empty cache: exactly the CI situation the mode is designed for.
            ContentAddressedCache empty = new ContentAddressedCache(temp.Combine("empty-cache"));

            CacheMissException ex = Assert.Throws<CacheMissException>(
                () => Acquire(temp, empty, resolved, request, NetworkMode.CacheOnly));

            Assert.Contains("snowflake", ex.Message, StringComparison.Ordinal);
            Assert.Contains("expected SHA-256", ex.Message, StringComparison.Ordinal);
            Assert.Contains("AdbcDriverNetworkMode", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void DownloadsOnDemandInOnlineMode()
        {
            using TempDirectory temp = new TempDirectory("acquire");
            FixtureRegistry registry = BuildRegistry(temp);
            DriverRequest request = Request("snowflake", "1.11.0", "win-x64");

            DriverLock resolved = new DriverResolver(
                new DefaultRegistryTransport(Timeout),
                new ContentAddressedCache(temp.Combine("resolve-cache")),
                NullSignatureVerifier.Instance)
                .Resolve(new[] { request }, Options(registry));

            ContentAddressedCache empty = new ContentAddressedCache(temp.Combine("empty-cache"));
            AcquisitionResult result = Acquire(temp, empty, resolved, request, NetworkMode.Online);

            Assert.NotEmpty(result.Files);
            Assert.NotNull(empty.TryOpen(resolved.Drivers[0].Artifacts[0].ArchiveSha256));
        }

        [Fact]
        public void RejectsRefreshLockDuringAnOrdinaryBuild()
        {
            using TempDirectory temp = new TempDirectory("acquire");
            DriverRequest request = Request("snowflake", "1.11.0", "win-x64");

            AcquisitionException ex = Assert.Throws<AcquisitionException>(() => Acquire(
                temp,
                new ContentAddressedCache(temp.Combine("cache")),
                DriverLock.Empty,
                request,
                NetworkMode.RefreshLock));

            Assert.Contains("ResolveAdbcDriverLock", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ReportsADriverThatIsNotInTheLock()
        {
            using TempDirectory temp = new TempDirectory("acquire");

            AcquisitionException ex = Assert.Throws<AcquisitionException>(() => Acquire(
                temp,
                new ContentAddressedCache(temp.Combine("cache")),
                DriverLock.Empty,
                Request("snowflake", "1.11.0", "win-x64"),
                NetworkMode.CacheOnly));

            Assert.Contains("not in the driver lock file", ex.Message, StringComparison.Ordinal);
            Assert.Contains("ResolveAdbcDriverLock", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ReportsARuntimeIdentifierThatIsNotInTheLock()
        {
            using TempDirectory temp = new TempDirectory("acquire");
            FixtureRegistry registry = BuildRegistry(temp);

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            DriverLock resolved = new DriverResolver(
                new DefaultRegistryTransport(Timeout), cache, NullSignatureVerifier.Instance)
                .Resolve(new[] { Request("snowflake", "1.11.0", "win-x64") }, Options(registry));

            AcquisitionException ex = Assert.Throws<AcquisitionException>(() => Acquire(
                temp, cache, resolved, Request("snowflake", "1.11.0", "linux-x64"), NetworkMode.CacheOnly));

            Assert.Contains("no 'linux-x64' artifact", ex.Message, StringComparison.Ordinal);
            Assert.Contains("win-x64", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void DetectsALockThatDisagreesWithTheCachedDriver()
        {
            using TempDirectory temp = new TempDirectory("acquire");
            FixtureRegistry registry = BuildRegistry(temp);
            DriverRequest request = Request("snowflake", "1.11.0", "win-x64");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            DriverLock resolved = new DriverResolver(
                new DefaultRegistryTransport(Timeout), cache, NullSignatureVerifier.Instance)
                .Resolve(new[] { request }, Options(registry));

            LockedArtifact original = resolved.Drivers[0].Artifacts[0];
            DriverLock tampered = new DriverLock(1, resolved.Registries, new[]
            {
                new LockedDriver("snowflake", "1.11.0", null, null, null, null, null, new[]
                {
                    new LockedArtifact(
                        original.Rid, original.AdbcPlatform, original.Url, original.ArchiveSha256,
                        original.ArchiveLength, original.DriverFile, new string('c', 64), null, null, null),
                }),
            });

            IntegrityException ex = Assert.Throws<IntegrityException>(
                () => Acquire(temp, cache, tampered, request, NetworkMode.CacheOnly));
            Assert.Contains("but the lock file requires", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void DetectsACacheEntryWhoseFilesWereTamperedWith()
        {
            using TempDirectory temp = new TempDirectory("acquire");
            FixtureRegistry registry = BuildRegistry(temp);
            DriverRequest request = Request("snowflake", "1.11.0", "win-x64");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            DriverLock resolved = new DriverResolver(
                new DefaultRegistryTransport(Timeout), cache, NullSignatureVerifier.Instance)
                .Resolve(new[] { request }, Options(registry));

            CacheEntry entry = cache.TryOpen(resolved.Drivers[0].Artifacts[0].ArchiveSha256)!;
            File.SetAttributes(entry.DriverPath, FileAttributes.Normal);
            File.WriteAllText(entry.DriverPath, "replaced by an attacker");

            IntegrityException ex = Assert.Throws<IntegrityException>(() => Acquire(
                temp, cache, resolved, request, NetworkMode.CacheOnly, verifyFileHashes: true));

            Assert.Contains("tampered with or corrupted", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void CarriesEntrypointAndAdbcVersionThroughToThePlan()
        {
            using TempDirectory temp = new TempDirectory("acquire");
            FixtureRegistry registry = BuildRegistry(temp);
            DriverRequest request = Request("duckdb", "1.4.2", "win-x64");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            DriverLock resolved = new DriverResolver(
                new DefaultRegistryTransport(Timeout), cache, NullSignatureVerifier.Instance)
                .Resolve(new[] { request }, Options(registry));

            Assert.Equal("DuckDbInit", resolved.Drivers[0].Entrypoint);

            AcquisitionResult result = Acquire(temp, cache, resolved, request, NetworkMode.CacheOnly);
            Assert.Equal("DuckDbInit", result.Plan.Drivers[0].Entrypoint);
            Assert.Equal("MIT", result.Plan.Drivers[0].License);
        }

        private static AcquisitionResult Acquire(
            TempDirectory temp,
            ContentAddressedCache cache,
            DriverLock driverLock,
            DriverRequest request,
            NetworkMode mode,
            bool verifyFileHashes = false)
        {
            DriverAcquirer acquirer = new DriverAcquirer(
                cache, new DefaultRegistryTransport(Timeout), NullSignatureVerifier.Instance);

            return acquirer.Acquire(driverLock, new[] { request }, new AcquisitionOptions
            {
                Mode = mode,
                DestinationRoot = temp.Combine("obj"),
                LockTimeout = Timeout,
                VerifyFileHashes = verifyFileHashes,
            });
        }
    }
}
