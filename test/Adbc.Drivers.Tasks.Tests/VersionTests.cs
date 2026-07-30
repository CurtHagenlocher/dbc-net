using System;
using System.Collections.Generic;
using Adbc.Drivers.Build.Model;
using Xunit;

namespace Adbc.Drivers.Build.Tests
{
    public sealed class SemanticVersionTests
    {
        [Theory]
        [InlineData("1.11.0", 1, 11, 0)]
        [InlineData("v1.11.0", 1, 11, 0)]
        [InlineData("V1.11.0", 1, 11, 0)]
        [InlineData("1.9", 1, 9, 0)]
        [InlineData("2", 2, 0, 0)]
        [InlineData("1.11.0-rc.1", 1, 11, 0)]
        [InlineData("1.11.0+build.5", 1, 11, 0)]
        public void ParsesSupportedForms(string text, int major, int minor, int patch)
        {
            Assert.True(SemanticVersion.TryParse(text, out SemanticVersion? version));
            Assert.Equal(major, version!.Major);
            Assert.Equal(minor, version.Minor);
            Assert.Equal(patch, version.Patch);
        }

        [Fact]
        public void PreservesTheOriginalSpelling()
        {
            // Package URLs are built from the version as the index spelled it, and the
            // public index is not consistent about the "v" prefix.
            SemanticVersion version = SemanticVersion.Parse("v1.10.1");
            Assert.Equal("v1.10.1", version.Original);
            Assert.Equal("1.10.1", version.ToNormalizedString());
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("1.2.3.4")]
        [InlineData("1.02.3")]
        [InlineData("abc")]
        [InlineData("1.x.0")]
        [InlineData("1.2.3-")]
        [InlineData("1.2.3+")]
        [InlineData("1.2.3-alpha..1")]
        public void RejectsMalformedVersions(string text) =>
            Assert.False(SemanticVersion.TryParse(text, out _));

        [Fact]
        public void OrdersByPrecedence()
        {
            List<SemanticVersion> versions = new List<SemanticVersion>
            {
                SemanticVersion.Parse("1.10.0"),
                SemanticVersion.Parse("1.9.0"),
                SemanticVersion.Parse("2.0.0"),
                SemanticVersion.Parse("1.10.1"),
            };

            versions.Sort();

            Assert.Equal(
                new[] { "1.9.0", "1.10.0", "1.10.1", "2.0.0" },
                versions.ConvertAll(v => v.ToNormalizedString()).ToArray());
        }

        [Fact]
        public void RanksAReleaseAboveItsPrereleases()
        {
            Assert.True(SemanticVersion.Parse("1.11.0").CompareTo(SemanticVersion.Parse("1.11.0-rc.1")) > 0);
            Assert.True(SemanticVersion.Parse("1.11.0-rc.1").CompareTo(SemanticVersion.Parse("1.11.0-rc.2")) < 0);
            Assert.True(SemanticVersion.Parse("1.11.0-alpha").CompareTo(SemanticVersion.Parse("1.11.0-beta")) < 0);
        }

        [Fact]
        public void OrdersNumericPrereleaseIdentifiersNumerically() =>
            Assert.True(SemanticVersion.Parse("1.0.0-2").CompareTo(SemanticVersion.Parse("1.0.0-10")) < 0);

        [Fact]
        public void RanksNumericPrereleaseIdentifiersBelowAlphanumericOnes() =>
            Assert.True(SemanticVersion.Parse("1.0.0-1").CompareTo(SemanticVersion.Parse("1.0.0-alpha")) < 0);

        [Fact]
        public void IgnoresBuildMetadataForPrecedence() =>
            Assert.Equal(SemanticVersion.Parse("1.0.0+a"), SemanticVersion.Parse("1.0.0+b"));

        [Fact]
        public void TreatsTheVPrefixAsEquivalent() =>
            Assert.Equal(SemanticVersion.Parse("v1.11.0"), SemanticVersion.Parse("1.11.0"));
    }

    public sealed class VersionRangeTests
    {
        private static readonly string[] Catalog =
        {
            "1.8.0", "1.9.0", "1.10.0", "1.10.1", "1.10.3", "1.11.0", "2.0.0-rc.1",
        };

        private static List<SemanticVersion> Candidates()
        {
            List<SemanticVersion> versions = new List<SemanticVersion>();
            foreach (string text in Catalog)
            {
                versions.Add(SemanticVersion.Parse(text));
            }

            return versions;
        }

        [Theory]
        [InlineData("1.10.1", "1.10.1")]
        [InlineData("=1.10.1", "1.10.1")]
        [InlineData("v1.10.1", "1.10.1")]
        [InlineData("*", "1.11.0")]
        [InlineData("", "1.11.0")]
        [InlineData("latest", "1.11.0")]
        [InlineData(">=1.10.0", "1.11.0")]
        [InlineData("<1.10.0", "1.9.0")]
        [InlineData("<=1.10.1", "1.10.1")]
        [InlineData(">1.10.1", "1.11.0")]
        [InlineData("^1.10.0", "1.11.0")]
        [InlineData("~1.10.0", "1.10.3")]
        [InlineData(">=1.9.0 <1.10.3", "1.10.1")]
        [InlineData(">=1.9.0,<1.10.3", "1.10.1")]
        public void SelectsTheHighestSatisfyingRelease(string spec, string expected)
        {
            SemanticVersion? selected = VersionRange.Parse(spec).SelectBest(Candidates(), allowPrerelease: false);
            Assert.Equal(expected, selected?.ToNormalizedString());
        }

        [Fact]
        public void ExcludesPrereleasesByDefault()
        {
            // 2.0.0-rc.1 satisfies ">=1.11.0" numerically but must not be selected.
            SemanticVersion? selected = VersionRange.Parse(">=1.11.0").SelectBest(Candidates(), allowPrerelease: false);
            Assert.Equal("1.11.0", selected?.ToNormalizedString());
        }

        [Fact]
        public void IncludesPrereleasesWhenExplicitlyAllowed()
        {
            SemanticVersion? selected = VersionRange.Parse(">=1.11.0").SelectBest(Candidates(), allowPrerelease: true);
            Assert.Equal("2.0.0-rc.1", selected?.ToNormalizedString());
        }

        [Fact]
        public void IncludesPrereleasesWhenTheConstraintNamesOne()
        {
            VersionRange range = VersionRange.Parse(">=2.0.0-rc.1");
            Assert.True(range.MentionsPrerelease);
            Assert.Equal("2.0.0-rc.1", range.SelectBest(Candidates(), allowPrerelease: false)?.ToNormalizedString());
        }

        [Fact]
        public void CaretPinsTheLeftmostNonZeroComponent()
        {
            Assert.True(VersionRange.Parse("^1.10.0").Satisfies(SemanticVersion.Parse("1.99.0")));
            Assert.False(VersionRange.Parse("^1.10.0").Satisfies(SemanticVersion.Parse("2.0.0")));
            Assert.False(VersionRange.Parse("^1.10.0").Satisfies(SemanticVersion.Parse("1.9.0")));

            Assert.True(VersionRange.Parse("^0.3.1").Satisfies(SemanticVersion.Parse("0.3.9")));
            Assert.False(VersionRange.Parse("^0.3.1").Satisfies(SemanticVersion.Parse("0.4.0")));
        }

        [Fact]
        public void TildeAllowsPatchUpdatesOnly()
        {
            Assert.True(VersionRange.Parse("~1.10.0").Satisfies(SemanticVersion.Parse("1.10.9")));
            Assert.False(VersionRange.Parse("~1.10.0").Satisfies(SemanticVersion.Parse("1.11.0")));
        }

        [Fact]
        public void ReturnsNullWhenNothingSatisfiesTheConstraint() =>
            Assert.Null(VersionRange.Parse(">=99.0.0").SelectBest(Candidates(), allowPrerelease: true));

        [Theory]
        [InlineData("1.0.0 || 2.0.0")]
        [InlineData("not-a-version")]
        [InlineData("><1.0.0")]
        public void ReportsUnusableConstraints(string spec)
        {
            Assert.False(VersionRange.TryParse(spec, out _, out string? error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }
}
