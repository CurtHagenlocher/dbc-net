using Adbc.Drivers.Build.Model;
using Xunit;

namespace Adbc.Drivers.Build.Tests
{
    public sealed class RuntimeIdentifierMapTests
    {
        [Theory]
        [InlineData("win-x64", "windows_amd64")]
        [InlineData("win-arm64", "windows_arm64")]
        [InlineData("linux-x64", "linux_amd64")]
        [InlineData("linux-arm64", "linux_arm64")]
        [InlineData("linux-musl-x64", "linux_amd64_musl")]
        [InlineData("osx-x64", "macos_amd64")]
        [InlineData("osx-arm64", "macos_arm64")]
        public void MapsPortableRidsToAdbcPlatforms(string rid, string platform)
        {
            Assert.True(RuntimeIdentifierMap.TryGetAdbcPlatform(rid, out string? actual));
            Assert.Equal(platform, actual);

            Assert.True(RuntimeIdentifierMap.TryGetRuntimeIdentifier(platform, out string? roundTripped));
            Assert.Equal(rid, roundTripped);
        }

        [Fact]
        public void RejectsUnknownRuntimeIdentifiers()
        {
            Assert.False(RuntimeIdentifierMap.TryGetAdbcPlatform("win-x86", out _));
            Assert.False(RuntimeIdentifierMap.TryGetAdbcPlatform("", out _));
        }

        [Theory]
        [InlineData("win10-x64", "win-x64")]
        [InlineData("win81-arm64", "win-arm64")]
        [InlineData("ubuntu.22.04-x64", "linux-x64")]
        [InlineData("debian-arm64", "linux-arm64")]
        [InlineData("rhel.9-x64", "linux-x64")]
        [InlineData("alpine.3.19-x64", "linux-musl-x64")]
        [InlineData("linux-musl-arm64", "linux-musl-arm64")]
        [InlineData("osx.14-arm64", "osx-arm64")]
        [InlineData("win-x64", "win-x64")]
        public void CollapsesVersionAndDistributionQualifiedRids(string rid, string expected) =>
            Assert.Equal(expected, RuntimeIdentifierMap.Normalize(rid));

        [Fact]
        public void LeavesUnrecognizedRidsAloneSoTheErrorNamesWhatWasAsked() =>
            Assert.Equal("win-x86", RuntimeIdentifierMap.Normalize("win-x86"));

        [Fact]
        public void ResolvesAHostRuntimeIdentifier()
        {
            // The tests only run on platforms the table covers.
            string? host = RuntimeIdentifierMap.TryGetHostRuntimeIdentifier();
            Assert.NotNull(host);
            Assert.True(RuntimeIdentifierMap.TryGetAdbcPlatform(host!, out _));
        }
    }
}
