using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Adbc.Drivers.Build.Model;
using Adbc.Drivers.Build.Registry;
using Xunit;

namespace Adbc.Drivers.Build.Tests
{
    /// <summary>
    /// Parses a committed snapshot of the live public Columnar registry index.
    /// </summary>
    /// <remarks>
    /// The synthetic YAML in <see cref="YamlParserTests"/> covers the constructs the
    /// index format is documented to use. This covers the ones it actually uses, which
    /// turned out to be a wider set — for example values wrapped onto the line after
    /// their key, and inconsistent <c>v</c> prefixes on versions of the same driver.
    /// Regenerate with:
    /// <code>curl -o test/Adbc.Drivers.Tasks.Tests/Fixtures/public-index-snapshot.yaml https://dbc-cdn.columnar.tech/index.yaml</code>
    /// </remarks>
    public sealed class PublicIndexSnapshotTests
    {
        private static readonly Uri Base = new Uri("https://dbc-cdn.columnar.tech/");

        private static string Snapshot()
        {
            Assembly assembly = typeof(PublicIndexSnapshotTests).Assembly;
            string name = assembly.GetManifestResourceNames()
                .Single(n => n.EndsWith("public-index-snapshot.yaml", StringComparison.Ordinal));

            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using StreamReader reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static RegistryIndex Parse() => RegistryIndex.Parse(Snapshot(), Base, "index.yaml");

        [Fact]
        public void ParsesTheWholeIndex()
        {
            RegistryIndex index = Parse();

            // The snapshot documents 17 driver slugs.
            Assert.Equal(17, index.Drivers.Count);
            Assert.All(index.Drivers, d => Assert.False(string.IsNullOrWhiteSpace(d.Slug)));
            Assert.All(index.Drivers, d => Assert.NotEmpty(d.Releases));
        }

        [Fact]
        public void ContainsTheExpectedDriverSlugs()
        {
            string[] slugs = Parse().Drivers.Select(d => d.Slug).OrderBy(s => s, StringComparer.Ordinal).ToArray();

            Assert.Equal(
                new[]
                {
                    "bigquery", "clickhouse", "databricks", "datafusion", "duckdb", "exasol", "flightsql",
                    "mssql", "mysql", "postgresql", "quack", "redshift", "singlestore", "snowflake",
                    "spark", "sqlite", "trino",
                },
                slugs);
        }

        [Fact]
        public void ResolvesEveryPackageToAnAbsoluteHttpsUrl()
        {
            RegistryIndex index = Parse();
            int packages = 0;

            foreach (DriverEntry driver in index.Drivers)
            {
                foreach (DriverRelease release in driver.Releases)
                {
                    foreach (DriverPackage package in release.Packages)
                    {
                        Uri url = driver.ResolvePackageUrl(release, package, out _);
                        Assert.Equal(Uri.UriSchemeHttps, url.Scheme);
                        Assert.EndsWith(".tar.gz", url.AbsolutePath, StringComparison.Ordinal);
                        packages++;
                    }
                }
            }

            Assert.True(packages > 100, $"Expected the snapshot to describe many packages but found {packages}.");
        }

        [Fact]
        public void HandlesTheInconsistentVersionPrefixWithinOneDriver()
        {
            // snowflake publishes both "1.9.0" and "v1.10.0"; both must normalize, and
            // the original spelling must survive for URL construction.
            DriverEntry snowflake = Parse().Drivers.Single(d => d.Slug == "snowflake");

            Assert.Contains(snowflake.Releases, r => r.RawVersion == "1.9.0");
            Assert.Contains(snowflake.Releases, r => r.RawVersion.StartsWith("v", StringComparison.Ordinal));
            Assert.Contains(snowflake.Releases, r => r.Version.ToNormalizedString() == "1.11.0");
        }

        [Fact]
        public void ReadsUrlsThatAreWrappedOntoTheLineAfterTheirKey()
        {
            // clickhouse writes "url:" with the value on the next, more-indented line.
            DriverEntry clickhouse = Parse().Drivers.Single(d => d.Slug == "clickhouse");
            DriverRelease release = clickhouse.Releases[0];
            DriverPackage package = release.FindPackage("linux_amd64")!;

            Assert.False(string.IsNullOrWhiteSpace(package.Url));
            Assert.DoesNotContain(" ", package.Url!, StringComparison.Ordinal);

            Uri url = clickhouse.ResolvePackageUrl(release, package, out bool derived);
            Assert.False(derived);
            Assert.StartsWith("https://dbc-cdn.columnar.tech/clickhouse/", url.AbsoluteUri, StringComparison.Ordinal);
        }

        [Fact]
        public void SelectsASnowflakeVersionTheWayAResolveWould()
        {
            DriverEntry snowflake = Parse().Drivers.Single(d => d.Slug == "snowflake");
            List<SemanticVersion> versions = snowflake.Releases.Select(r => r.Version).ToList();

            SemanticVersion? selected = VersionRange.Parse("^1.9.0").SelectBest(versions, allowPrerelease: false);

            Assert.NotNull(selected);
            Assert.NotNull(snowflake.FindRelease(selected!)!.FindPackage("windows_amd64"));
        }

        [Fact]
        public void MapsEveryPublishedPlatformThisPackageClaimsToSupport()
        {
            // Any platform tuple in the index that has no RID mapping would be
            // unreachable from a project, so this makes that visible rather than
            // surfacing as a confusing "no package for" error later.
            HashSet<string> unmapped = new HashSet<string>(StringComparer.Ordinal);

            foreach (DriverEntry driver in Parse().Drivers)
            {
                foreach (DriverRelease release in driver.Releases)
                {
                    foreach (DriverPackage package in release.Packages)
                    {
                        if (!RuntimeIdentifierMap.TryGetRuntimeIdentifier(package.Platform, out _))
                        {
                            unmapped.Add(package.Platform);
                        }
                    }
                }
            }

            Assert.True(
                unmapped.Count == 0,
                "The registry publishes platform tuples with no RID mapping: " + string.Join(", ", unmapped));
        }
    }
}
