using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Adbc.Drivers.Build.Tests.TestSupport
{
    /// <summary>
    /// A driver registry on disk, addressed with <c>file://</c> URLs.
    /// </summary>
    /// <remarks>
    /// Lets resolution, caching, and acquisition be tested end to end without a network,
    /// keeping the default test run deterministic and offline. The layout and index shape
    /// mirror the public Columnar registry, including its inconsistent use of a <c>v</c>
    /// prefix on versions.
    /// </remarks>
    internal sealed class FixtureRegistry
    {
        private readonly List<DriverFixture> _drivers = new List<DriverFixture>();

        public FixtureRegistry(string root)
        {
            Root = Path.GetFullPath(root);
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public Uri BaseUri => new Uri(Root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? Root
            : Root + Path.DirectorySeparatorChar);

        public string IndexPath => Path.Combine(Root, "index.yaml");

        public DriverFixture AddDriver(string slug, string? name = null, string license = "Apache-2.0")
        {
            DriverFixture driver = new DriverFixture(this, slug, name ?? (slug + " fixture driver"), license);
            _drivers.Add(driver);
            return driver;
        }

        /// <summary>Writes <c>index.yaml</c>. Call after all drivers are added.</summary>
        public FixtureRegistry Write()
        {
            StringBuilder yaml = new StringBuilder();
            yaml.Append("drivers:\n");

            foreach (DriverFixture driver in _drivers)
            {
                yaml.Append("- name: ").Append(driver.Name).Append('\n');
                yaml.Append("  description: A fixture driver used by the Adbc.Drivers.Build tests\n");
                yaml.Append("  license: ").Append(driver.License).Append('\n');
                yaml.Append("  path: ").Append(driver.Slug).Append('\n');
                yaml.Append("  urls:\n");
                yaml.Append("  - https://example.invalid/").Append(driver.Slug).Append('\n');
                yaml.Append("  pkginfo:\n");

                foreach (ReleaseFixture release in driver.Releases)
                {
                    yaml.Append("  - packages:\n");
                    foreach (KeyValuePair<string, string> package in release.Packages)
                    {
                        yaml.Append("    - platform: ").Append(package.Key).Append('\n');
                        if (package.Value.Length > 0)
                        {
                            yaml.Append("      url: ").Append(package.Value).Append('\n');
                        }
                    }

                    yaml.Append("    version: ").Append(release.RawVersion).Append('\n');
                }
            }

            File.WriteAllText(IndexPath, yaml.ToString(), new UTF8Encoding(false));
            return this;
        }

        internal sealed class DriverFixture
        {
            private readonly FixtureRegistry _registry;
            private readonly List<ReleaseFixture> _releases = new List<ReleaseFixture>();

            internal DriverFixture(FixtureRegistry registry, string slug, string name, string license)
            {
                _registry = registry;
                Slug = slug;
                Name = name;
                License = license;
            }

            public string Slug { get; }

            public string Name { get; }

            public string License { get; }

            public IReadOnlyList<ReleaseFixture> Releases => _releases;

            public ReleaseFixture AddRelease(string rawVersion)
            {
                ReleaseFixture release = new ReleaseFixture(_registry, this, rawVersion);
                _releases.Add(release);
                return release;
            }

            public FixtureRegistry Registry => _registry;
        }

        internal sealed class ReleaseFixture
        {
            private readonly FixtureRegistry _registry;
            private readonly DriverFixture _driver;
            private readonly List<KeyValuePair<string, string>> _packages = new List<KeyValuePair<string, string>>();

            internal ReleaseFixture(FixtureRegistry registry, DriverFixture driver, string rawVersion)
            {
                _registry = registry;
                _driver = driver;
                RawVersion = rawVersion;
            }

            public string RawVersion { get; }

            /// <summary>Platform to index URL. An empty URL omits the field entirely.</summary>
            public IReadOnlyList<KeyValuePair<string, string>> Packages => _packages;

            /// <summary>
            /// Writes a driver archive for a platform and records it in the index.
            /// </summary>
            /// <param name="omitUrlFromIndex">
            /// Leave the <c>url</c> field out so the conventional location must be derived.
            /// </param>
            public ReleaseFixture AddPackage(
                string platform,
                string driverFileName,
                string driverContent,
                bool omitUrlFromIndex = false,
                string? entrypoint = null,
                bool includeSignature = true)
            {
                string relative = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/{1}/{0}_{2}_{1}.tar.gz",
                    _driver.Slug,
                    RawVersion,
                    platform);

                TarGzBuilder.CreateDriverPackage(
                        driverFileName,
                        driverContent,
                        manifestName: _driver.Name,
                        version: RawVersion,
                        entrypoint: entrypoint,
                        includeSignature: includeSignature)
                    .WriteTarGz(Path.Combine(_registry.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

                _packages.Add(new KeyValuePair<string, string>(platform, omitUrlFromIndex ? string.Empty : relative));
                return this;
            }

            public DriverFixture Driver => _driver;

            public FixtureRegistry Registry => _registry;

            public string ArchivePath(string platform) => Path.Combine(
                _registry.Root,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/{1}/{0}_{2}_{1}.tar.gz",
                    _driver.Slug,
                    RawVersion,
                    platform).Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
