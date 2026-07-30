using System;
using System.Collections.Generic;
using System.Globalization;
using Adbc.Drivers.Build.Model;
using Adbc.Drivers.Build.Text;

namespace Adbc.Drivers.Build.Registry
{
    internal sealed class DriverPackage
    {
        public DriverPackage(string platform, string? url)
        {
            Platform = platform;
            Url = url;
        }

        /// <summary>ADBC platform tuple, for example <c>windows_amd64</c>.</summary>
        public string Platform { get; }

        /// <summary>
        /// Package location as recorded in the index: absolute, or relative to the
        /// registry base. Null when the index omits it and the URL must be derived.
        /// </summary>
        public string? Url { get; }
    }

    internal sealed class DriverRelease
    {
        public DriverRelease(string rawVersion, SemanticVersion version, IReadOnlyList<DriverPackage> packages)
        {
            RawVersion = rawVersion;
            Version = version;
            Packages = packages;
        }

        /// <summary>
        /// Version text exactly as the index spelled it. The public index is not
        /// consistent about the <c>v</c> prefix, and package URLs are built from this
        /// spelling rather than the normalized one.
        /// </summary>
        public string RawVersion { get; }

        public SemanticVersion Version { get; }

        public IReadOnlyList<DriverPackage> Packages { get; }

        public DriverPackage? FindPackage(string platform)
        {
            foreach (DriverPackage package in Packages)
            {
                if (string.Equals(package.Platform, platform, StringComparison.OrdinalIgnoreCase))
                {
                    return package;
                }
            }

            return null;
        }
    }

    internal sealed class DriverEntry
    {
        public DriverEntry(
            string slug,
            string? name,
            string? description,
            string? license,
            string? docsUrl,
            IReadOnlyList<string> urls,
            IReadOnlyList<DriverRelease> releases,
            Uri registryBaseUri)
        {
            Slug = slug;
            Name = name;
            Description = description;
            License = license;
            DocsUrl = docsUrl;
            Urls = urls;
            Releases = releases;
            RegistryBaseUri = registryBaseUri;
        }

        /// <summary>The <c>path</c> field: the identifier used in project items and URLs.</summary>
        public string Slug { get; }

        public string? Name { get; }

        public string? Description { get; }

        public string? License { get; }

        public string? DocsUrl { get; }

        public IReadOnlyList<string> Urls { get; }

        public IReadOnlyList<DriverRelease> Releases { get; }

        /// <summary>Registry the entry came from, used to resolve relative package URLs.</summary>
        public Uri RegistryBaseUri { get; }

        public DriverRelease? FindRelease(SemanticVersion version)
        {
            foreach (DriverRelease release in Releases)
            {
                if (release.Version.Equals(version))
                {
                    return release;
                }
            }

            return null;
        }

        /// <summary>
        /// Absolute package URL. Relative index URLs resolve against the registry base;
        /// a missing URL is derived from the naming convention the public registry uses.
        /// </summary>
        public Uri ResolvePackageUrl(DriverRelease release, DriverPackage package, out bool derived)
        {
            derived = false;

            if (!string.IsNullOrWhiteSpace(package.Url))
            {
                string text = package.Url!.Trim();
                if (Uri.TryCreate(text, UriKind.Absolute, out Uri? absolute))
                {
                    return absolute;
                }

                return new Uri(EnsureTrailingSlash(RegistryBaseUri), text);
            }

            derived = true;
            string relative = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/{1}/{0}_{2}_{1}.tar.gz",
                Slug,
                release.RawVersion,
                package.Platform);
            return new Uri(EnsureTrailingSlash(RegistryBaseUri), relative);
        }

        internal static Uri EnsureTrailingSlash(Uri uri)
        {
            string text = uri.AbsoluteUri;
            return text.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(text + "/");
        }
    }

    /// <summary>
    /// A parsed registry <c>index.yaml</c>.
    /// </summary>
    internal sealed class RegistryIndex
    {
        private RegistryIndex(string? name, Uri baseUri, IReadOnlyList<DriverEntry> drivers)
        {
            Name = name;
            BaseUri = baseUri;
            Drivers = drivers;
        }

        public string? Name { get; }

        public Uri BaseUri { get; }

        public IReadOnlyList<DriverEntry> Drivers { get; }

        /// <summary>
        /// Parses a registry index. <paramref name="baseUri"/> is the registry root,
        /// not the index URL, and is remembered for relative package URLs.
        /// </summary>
        public static RegistryIndex Parse(string yaml, Uri baseUri, string? sourceName)
        {
            if (yaml is null) throw new ArgumentNullException(nameof(yaml));
            if (baseUri is null) throw new ArgumentNullException(nameof(baseUri));

            YamlNode root = YamlParser.Parse(yaml, sourceName);
            if (root.Kind != YamlKind.Mapping)
            {
                throw new YamlParseException(root.Line, "The registry index must be a mapping.", sourceName);
            }

            // The public index has no top-level "name"; dbc's documented shape does.
            string? name = root["name"].AsString();

            List<DriverEntry> drivers = new List<DriverEntry>();
            foreach (YamlNode driverNode in root["drivers"].AsSequence())
            {
                if (driverNode.Kind != YamlKind.Mapping)
                {
                    throw new YamlParseException(driverNode.Line, "Each driver entry must be a mapping.", sourceName);
                }

                string? slug = driverNode["path"].AsString();
                if (string.IsNullOrWhiteSpace(slug))
                {
                    throw new YamlParseException(driverNode.Line, "A driver entry is missing its 'path' field.", sourceName);
                }

                List<string> urls = new List<string>();
                foreach (YamlNode urlNode in driverNode["urls"].AsSequence())
                {
                    string? url = urlNode.AsString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        urls.Add(url!.Trim());
                    }
                }

                List<DriverRelease> releases = new List<DriverRelease>();
                foreach (YamlNode releaseNode in driverNode["pkginfo"].AsSequence())
                {
                    if (releaseNode.Kind != YamlKind.Mapping)
                    {
                        throw new YamlParseException(releaseNode.Line, "Each 'pkginfo' entry must be a mapping.", sourceName);
                    }

                    string? rawVersion = releaseNode["version"].AsString();
                    if (string.IsNullOrWhiteSpace(rawVersion))
                    {
                        throw new YamlParseException(
                            releaseNode.Line,
                            $"A 'pkginfo' entry for driver '{slug}' is missing its 'version' field.",
                            sourceName);
                    }

                    if (!SemanticVersion.TryParse(rawVersion, out SemanticVersion? version))
                    {
                        // Skipped rather than fatal: one malformed release must not make
                        // the whole registry unusable.
                        continue;
                    }

                    List<DriverPackage> packages = new List<DriverPackage>();
                    foreach (YamlNode packageNode in releaseNode["packages"].AsSequence())
                    {
                        if (packageNode.Kind != YamlKind.Mapping)
                        {
                            throw new YamlParseException(packageNode.Line, "Each package entry must be a mapping.", sourceName);
                        }

                        string? platform = packageNode["platform"].AsString();
                        if (string.IsNullOrWhiteSpace(platform))
                        {
                            throw new YamlParseException(
                                packageNode.Line,
                                $"A package entry for driver '{slug}' is missing its 'platform' field.",
                                sourceName);
                        }

                        packages.Add(new DriverPackage(platform!.Trim(), packageNode["url"].AsString()));
                    }

                    releases.Add(new DriverRelease(rawVersion!.Trim(), version!, packages));
                }

                drivers.Add(new DriverEntry(
                    slug!.Trim(),
                    driverNode["name"].AsString(),
                    driverNode["description"].AsString(),
                    driverNode["license"].AsString(),
                    driverNode["docs_url"].AsString(),
                    urls,
                    releases,
                    baseUri));
            }

            return new RegistryIndex(name, baseUri, drivers);
        }
    }

    /// <summary>
    /// Several registry indexes merged into one lookup. Earlier registries win, so the
    /// order a project configures them in is the order of precedence.
    /// </summary>
    internal sealed class RegistryCatalog
    {
        private readonly Dictionary<string, DriverEntry> _drivers =
            new Dictionary<string, DriverEntry>(StringComparer.OrdinalIgnoreCase);

        private readonly List<string> _shadowed = new List<string>();

        public RegistryCatalog(IEnumerable<RegistryIndex> indexes)
        {
            if (indexes is null) throw new ArgumentNullException(nameof(indexes));

            foreach (RegistryIndex index in indexes)
            {
                foreach (DriverEntry entry in index.Drivers)
                {
                    if (_drivers.ContainsKey(entry.Slug))
                    {
                        _shadowed.Add($"{entry.Slug} (also in {index.BaseUri})");
                        continue;
                    }

                    _drivers[entry.Slug] = entry;
                }
            }
        }

        /// <summary>Drivers that appeared in more than one registry, for diagnostics.</summary>
        public IReadOnlyList<string> ShadowedDrivers => _shadowed;

        public IEnumerable<string> Slugs => _drivers.Keys;

        public DriverEntry? Find(string slug) =>
            slug is not null && _drivers.TryGetValue(slug.Trim(), out DriverEntry? entry) ? entry : null;
    }
}
