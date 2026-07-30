using System;
using Adbc.Drivers.Build.Model;
using Adbc.Drivers.Build.Registry;
using Adbc.Drivers.Build.Text;
using Xunit;

namespace Adbc.Drivers.Build.Tests
{
    public sealed class RegistryIndexTests
    {
        private static readonly Uri Base = new Uri("https://dbc-cdn.columnar.tech/");

        /// <summary>
        /// Mirrors the public index, including its inconsistent version spelling: the
        /// same driver publishes both bare and <c>v</c>-prefixed versions.
        /// </summary>
        private const string Index = @"drivers:
- name: ASF Snowflake Driver
  description: An ADBC driver for Snowflake
  license: Apache-2.0
  path: snowflake
  urls:
  - https://arrow.apache.org/adbc/
  pkginfo:
  - packages:
    - platform: linux_amd64
      url: snowflake/1.9.0/snowflake_linux_amd64_1.9.0.tar.gz
    version: 1.9.0
  - packages:
    - platform: linux_amd64
      url: snowflake/v1.11.0/snowflake_linux_amd64_v1.11.0.tar.gz
    - platform: windows_amd64
      url: https://mirror.example.com/snowflake_windows_amd64_v1.11.0.tar.gz
    - platform: macos_arm64
    version: v1.11.0
- name: DuckDB Driver
  license: MIT
  path: duckdb
  pkginfo:
  - packages:
    - platform: windows_amd64
      url: duckdb/v1.4.2/duckdb_windows_amd64_v1.4.2.tar.gz
    version: v1.4.2
";

        [Fact]
        public void ParsesDriversAndReleases()
        {
            RegistryIndex index = RegistryIndex.Parse(Index, Base, "index.yaml");

            Assert.Equal(2, index.Drivers.Count);

            DriverEntry snowflake = index.Drivers[0];
            Assert.Equal("snowflake", snowflake.Slug);
            Assert.Equal("ASF Snowflake Driver", snowflake.Name);
            Assert.Equal("Apache-2.0", snowflake.License);
            Assert.Equal(2, snowflake.Releases.Count);
        }

        [Fact]
        public void NormalizesVersionsWhilePreservingTheSpellingUsedInUrls()
        {
            RegistryIndex index = RegistryIndex.Parse(Index, Base, "index.yaml");
            DriverEntry snowflake = index.Drivers[0];

            Assert.Equal("1.9.0", snowflake.Releases[0].RawVersion);
            Assert.Equal("v1.11.0", snowflake.Releases[1].RawVersion);
            Assert.Equal("1.9.0", snowflake.Releases[0].Version.ToNormalizedString());
            Assert.Equal("1.11.0", snowflake.Releases[1].Version.ToNormalizedString());
        }

        [Fact]
        public void ResolvesRelativePackageUrlsAgainstTheRegistryBase()
        {
            RegistryIndex index = RegistryIndex.Parse(Index, Base, "index.yaml");
            DriverEntry snowflake = index.Drivers[0];
            DriverRelease release = snowflake.Releases[1];

            Uri url = snowflake.ResolvePackageUrl(release, release.FindPackage("linux_amd64")!, out bool derived);

            Assert.False(derived);
            Assert.Equal(
                "https://dbc-cdn.columnar.tech/snowflake/v1.11.0/snowflake_linux_amd64_v1.11.0.tar.gz",
                url.AbsoluteUri);
        }

        [Fact]
        public void KeepsAbsolutePackageUrls()
        {
            RegistryIndex index = RegistryIndex.Parse(Index, Base, "index.yaml");
            DriverEntry snowflake = index.Drivers[0];
            DriverRelease release = snowflake.Releases[1];

            Uri url = snowflake.ResolvePackageUrl(release, release.FindPackage("windows_amd64")!, out bool derived);

            Assert.False(derived);
            Assert.Equal("https://mirror.example.com/snowflake_windows_amd64_v1.11.0.tar.gz", url.AbsoluteUri);
        }

        [Fact]
        public void DerivesAPackageUrlWhenTheIndexOmitsOne()
        {
            RegistryIndex index = RegistryIndex.Parse(Index, Base, "index.yaml");
            DriverEntry snowflake = index.Drivers[0];
            DriverRelease release = snowflake.Releases[1];

            Uri url = snowflake.ResolvePackageUrl(release, release.FindPackage("macos_arm64")!, out bool derived);

            // Reported so the caller can warn: a derived URL is a guess about a naming
            // convention, not something the registry actually stated.
            Assert.True(derived);
            Assert.Equal(
                "https://dbc-cdn.columnar.tech/snowflake/v1.11.0/snowflake_macos_arm64_v1.11.0.tar.gz",
                url.AbsoluteUri);
        }

        [Fact]
        public void FindsReleasesByNormalizedVersion()
        {
            RegistryIndex index = RegistryIndex.Parse(Index, Base, "index.yaml");
            DriverEntry snowflake = index.Drivers[0];

            Assert.NotNull(snowflake.FindRelease(SemanticVersion.Parse("1.11.0")));
            Assert.NotNull(snowflake.FindRelease(SemanticVersion.Parse("v1.11.0")));
            Assert.Null(snowflake.FindRelease(SemanticVersion.Parse("1.10.0")));
        }

        [Fact]
        public void SkipsReleasesWithAnUnparseableVersion()
        {
            // One bad release must not make the whole registry unusable.
            RegistryIndex index = RegistryIndex.Parse(
                "drivers:\n- path: x\n  pkginfo:\n  - packages:\n    - platform: linux_amd64\n    version: not-a-version\n  - packages:\n    - platform: linux_amd64\n    version: 1.0.0\n",
                Base,
                "index.yaml");

            DriverRelease release = Assert.Single(index.Drivers[0].Releases);
            Assert.Equal("1.0.0", release.Version.ToNormalizedString());
        }

        [Fact]
        public void RequiresAPathField()
        {
            YamlParseException ex = Assert.Throws<YamlParseException>(
                () => RegistryIndex.Parse("drivers:\n- name: No Path\n", Base, "index.yaml"));
            Assert.Contains("'path'", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RequiresAPlatformOnEachPackage()
        {
            Assert.Throws<YamlParseException>(() => RegistryIndex.Parse(
                "drivers:\n- path: x\n  pkginfo:\n  - packages:\n    - url: a.tar.gz\n    version: 1.0.0\n",
                Base,
                "index.yaml"));
        }

        [Fact]
        public void GivesTheFirstRegistryPrecedenceAndReportsTheShadowing()
        {
            RegistryIndex primary = RegistryIndex.Parse(Index, Base, "primary");
            RegistryIndex secondary = RegistryIndex.Parse(
                "drivers:\n- path: snowflake\n  license: Proprietary\n  pkginfo:\n  - packages:\n    - platform: linux_amd64\n    version: 9.9.9\n",
                new Uri("https://private.example.com/"),
                "secondary");

            RegistryCatalog catalog = new RegistryCatalog(new[] { primary, secondary });

            Assert.Equal("Apache-2.0", catalog.Find("snowflake")!.License);
            Assert.Contains(catalog.ShadowedDrivers, s => s.StartsWith("snowflake", StringComparison.Ordinal));
        }

        [Fact]
        public void LooksUpDriversCaseInsensitively()
        {
            RegistryCatalog catalog = new RegistryCatalog(new[] { RegistryIndex.Parse(Index, Base, "i") });

            Assert.NotNull(catalog.Find("SnowFlake"));
            Assert.NotNull(catalog.Find(" duckdb "));
            Assert.Null(catalog.Find("postgresql"));
        }
    }
}
