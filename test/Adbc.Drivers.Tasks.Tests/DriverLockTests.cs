using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Adbc.Drivers.Build.Locking;
using Adbc.Drivers.Build.Tests.TestSupport;
using Adbc.Drivers.Build.Text;
using Xunit;

namespace Adbc.Drivers.Build.Tests
{
    public sealed class DriverLockTests
    {
        private const string ArchiveHash = "3f09dc49bb7970faaea2222a5ee74303ab102aa73ee921ab35a0f8f9abe17127";
        private const string DriverHash = "11af17a7452456617dbe143bcd1e4aeeeb1caa5976356855d5d4ce89a25dd652";

        private static DriverLock Sample() => new DriverLock(
            DriverLock.CurrentSchemaVersion,
            new[] { "https://dbc-cdn.columnar.tech/" },
            new[]
            {
                new LockedDriver(
                    "snowflake",
                    "1.11.0",
                    "ASF Snowflake Driver",
                    "ADBC Drivers Contributors",
                    "Apache-2.0",
                    "1.1.0",
                    null,
                    new[]
                    {
                        new LockedArtifact(
                            "win-x64",
                            "windows_amd64",
                            "https://dbc-cdn.columnar.tech/snowflake/v1.11.0/snowflake_windows_amd64_v1.11.0.tar.gz",
                            ArchiveHash,
                            16130052,
                            "libadbc_driver_snowflake.dll",
                            DriverHash,
                            "libadbc_driver_snowflake.dll.sig",
                            null,
                            null),
                    }),
            });

        [Fact]
        public void RoundTripsThroughJson()
        {
            DriverLock original = Sample();
            DriverLock parsed = DriverLock.ParseJson(original.ToJson(), "test");

            LockedDriver driver = Assert.Single(parsed.Drivers);
            Assert.Equal("snowflake", driver.Id);
            Assert.Equal("1.11.0", driver.Version);
            Assert.Equal("Apache-2.0", driver.License);

            LockedArtifact artifact = Assert.Single(driver.Artifacts);
            Assert.Equal("win-x64", artifact.Rid);
            Assert.Equal("windows_amd64", artifact.AdbcPlatform);
            Assert.Equal(ArchiveHash, artifact.ArchiveSha256);
            Assert.Equal(16130052, artifact.ArchiveLength);
            Assert.Equal("libadbc_driver_snowflake.dll", artifact.DriverFile);
            Assert.Null(artifact.SignatureKeyFingerprint);
        }

        [Fact]
        public void SerializesDriversAndArtifactsInAStableOrder()
        {
            // A resolve that changes nothing must produce a byte-identical file, or every
            // re-resolve shows up as a spurious diff.
            DriverLock a = new DriverLock(
                1,
                new[] { "https://b/", "https://a/" },
                new[] { Driver("zulu", "linux-x64", "win-x64"), Driver("alpha", "win-x64") });

            DriverLock b = new DriverLock(
                1,
                new[] { "https://a/", "https://b/" },
                new[] { Driver("alpha", "win-x64"), Driver("zulu", "win-x64", "linux-x64") });

            Assert.Equal(a.ToJson(), b.ToJson());
            Assert.Equal(a.ComputeDigest(), b.ComputeDigest());
        }

        [Fact]
        public void ProducesADifferentDigestWhenAHashChanges()
        {
            DriverLock a = Sample();
            DriverLock b = new DriverLock(
                1,
                a.Registries,
                new[]
                {
                    new LockedDriver(
                        "snowflake", "1.11.0", null, null, null, null, null,
                        new[]
                        {
                            new LockedArtifact(
                                "win-x64", "windows_amd64", "https://x/", new string('a', 64), 1,
                                "d.dll", new string('b', 64), null, null, null),
                        }),
                });

            Assert.NotEqual(a.ComputeDigest(), b.ComputeDigest());
        }

        [Fact]
        public void WritesAtomicallyAndOnlyWhenTheContentChanges()
        {
            using TempDirectory temp = new TempDirectory("lock");
            string path = temp.Combine("adbc.drivers.lock.json");

            Assert.True(Sample().Save(path));
            DateTime firstWrite = File.GetLastWriteTimeUtc(path);

            // An unchanged rewrite must not touch the timestamp, or it would retrigger
            // every downstream incremental build.
            Assert.False(Sample().Save(path));
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(path));

            Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp*"));
        }

        [Fact]
        public void LoadsWhatItSaved()
        {
            using TempDirectory temp = new TempDirectory("lock");
            string path = temp.Combine("adbc.drivers.lock.json");
            Sample().Save(path);

            DriverLock loaded = DriverLock.Load(path);
            Assert.Equal("snowflake", Assert.Single(loaded.Drivers).Id);
            Assert.Equal(Sample().ComputeDigest(), loaded.ComputeDigest());
        }

        [Fact]
        public void FindsDriversAndArtifactsCaseInsensitively()
        {
            DriverLock parsed = Sample();
            Assert.NotNull(parsed.FindDriver("SNOWFLAKE"));
            Assert.NotNull(parsed.FindDriver("snowflake")!.FindArtifact("Win-X64"));
            Assert.Null(parsed.FindDriver("snowflake")!.FindArtifact("linux-x64"));
        }

        [Fact]
        public void RejectsAnUnknownSchemaVersion()
        {
            DriverLockException ex = Assert.Throws<DriverLockException>(
                () => DriverLock.ParseJson("{\"schemaVersion\": 99, \"drivers\": []}", "test"));
            Assert.Contains("schemaVersion 99", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsMalformedJson() =>
            Assert.Throws<DriverLockException>(() => DriverLock.ParseJson("{not json", "test"));

        [Theory]
        [InlineData("not-hex-at-all")]
        [InlineData("abc")]
        [InlineData("zz09dc49bb7970faaea2222a5ee74303ab102aa73ee921ab35a0f8f9abe17127x")]
        public void RejectsAHashThatIsNotHexadecimalSha256(string hash)
        {
            string json = "{\"schemaVersion\":1,\"registries\":[],\"drivers\":[{\"id\":\"x\",\"version\":\"1.0.0\","
                + "\"artifacts\":[{\"rid\":\"win-x64\",\"adbcPlatform\":\"windows_amd64\",\"url\":\"https://x/\","
                + "\"archiveSha256\":\"" + hash + "\",\"driverFile\":\"d.dll\",\"driverSha256\":\"" + new string('a', 64) + "\"}]}]}";

            DriverLockException ex = Assert.Throws<DriverLockException>(() => DriverLock.ParseJson(json, "test"));
            Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsADriverWithNoArtifacts()
        {
            DriverLockException ex = Assert.Throws<DriverLockException>(() => DriverLock.ParseJson(
                "{\"schemaVersion\":1,\"drivers\":[{\"id\":\"x\",\"version\":\"1.0.0\",\"artifacts\":[]}]}",
                "test"));
            Assert.Contains("no artifacts", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AcceptsAnSha256Prefix()
        {
            string json = "{\"schemaVersion\":1,\"registries\":[],\"drivers\":[{\"id\":\"x\",\"version\":\"1.0.0\","
                + "\"artifacts\":[{\"rid\":\"win-x64\",\"adbcPlatform\":\"windows_amd64\",\"url\":\"https://x/\","
                + "\"archiveSha256\":\"sha256:" + ArchiveHash.ToUpperInvariant() + "\",\"driverFile\":\"d.dll\","
                + "\"driverSha256\":\"" + DriverHash + "\"}]}]}";

            DriverLock parsed = DriverLock.ParseJson(json, "test");
            Assert.Equal(ArchiveHash, parsed.Drivers[0].Artifacts[0].ArchiveSha256);
        }

        private static LockedDriver Driver(string id, params string[] rids)
        {
            List<LockedArtifact> artifacts = new List<LockedArtifact>();
            foreach (string rid in rids)
            {
                artifacts.Add(new LockedArtifact(
                    rid, "platform_" + rid, "https://example.com/" + id, new string('a', 64), 1,
                    "d", new string('b', 64), null, null, null));
            }

            return new LockedDriver(id, "1.0.0", null, null, null, null, null, artifacts);
        }
    }

    public sealed class JsonTests
    {
        [Fact]
        public void ParsesObjectsArraysAndScalars()
        {
            JsonValue root = JsonParser.Parse(
                "{\"s\":\"text\",\"n\":-12.5,\"i\":42,\"b\":true,\"z\":null,\"a\":[1,\"two\",{\"k\":\"v\"}]}");

            Assert.Equal("text", root["s"].AsString());
            Assert.Equal("-12.5", root["n"].AsString());
            Assert.Equal(42, root["i"].AsInt32());
            Assert.True(root["b"].AsBoolean());
            Assert.True(root["z"].IsNull);
            Assert.Equal(3, root["a"].AsArray().Count);
            Assert.Equal("v", root["a"].AsArray()[2]["k"].AsString());
        }

        [Fact]
        public void UnescapesStrings()
        {
            JsonValue root = JsonParser.Parse("{\"s\":\"a\\\\b\\\"c\\nd\\u0041\"}");
            Assert.Equal("a\\b\"c\ndA", root["s"].AsString());
        }

        [Fact]
        public void ReturnsNullValueForMissingMembers()
        {
            JsonValue root = JsonParser.Parse("{}");
            Assert.True(root["missing"].IsNull);
            Assert.Null(root["missing"].AsString());
            Assert.Empty(root["missing"].AsArray());
        }

        [Theory]
        [InlineData("{")]
        [InlineData("{\"a\":}")]
        [InlineData("{\"a\":1,}")]
        [InlineData("[1 2]")]
        [InlineData("{\"a\":1} trailing")]
        [InlineData("\"unterminated")]
        [InlineData("{\"a\": 1.2.3}")]
        public void RejectsMalformedJson(string json) =>
            Assert.Throws<JsonParseException>(() => JsonParser.Parse(json));

        [Fact]
        public void WritesIndentedOutputThatRoundTrips()
        {
            JsonTextWriter writer = new JsonTextWriter();
            writer.StartObject();
            writer.Property("schemaVersion", 1);
            writer.Property("name", "value with \"quotes\" and \\ backslash");
            writer.Property("missing", (string?)null);
            writer.Name("items").StartArray();
            writer.StartObject();
            writer.Property("id", "a");
            writer.EndObject();
            writer.EndArray();
            writer.Name("empty").StartArray();
            writer.EndArray();
            writer.EndObject();

            string json = writer.ToString();
            JsonValue parsed = JsonParser.Parse(json);

            Assert.Equal(1, parsed["schemaVersion"].AsInt32());
            Assert.Equal("value with \"quotes\" and \\ backslash", parsed["name"].AsString());
            Assert.True(parsed["missing"].IsNull);
            Assert.Equal("a", Assert.Single(parsed["items"].AsArray())["id"].AsString());
            Assert.Empty(parsed["empty"].AsArray());
            Assert.Contains("\n  \"schemaVersion\": 1", json, StringComparison.Ordinal);
        }
    }
}
