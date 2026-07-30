using System;
using Adbc.Drivers.Build.Text;
using Xunit;

namespace Adbc.Drivers.Build.Tests
{
    public sealed class YamlParserTests
    {
        /// <summary>
        /// The exact shape of the public Columnar index: a sequence of driver mappings,
        /// each with a nested sequence of releases, each with a nested sequence of
        /// packages, and a sibling key after the inner sequence.
        /// </summary>
        private const string RegistryShape = @"drivers:
- name: ASF Snowflake Driver
  description: An ADBC driver for Snowflake developed under the Apache Software
    Foundation
  license: Apache-2.0
  path: snowflake
  urls:
  - https://arrow.apache.org/adbc/
  docs_url: https://adbc-drivers.org/drivers/snowflake/
  pkginfo:
  - packages:
    - platform: linux_amd64
      url: snowflake/v1.11.0/snowflake_linux_amd64_v1.11.0.tar.gz
    - platform: windows_amd64
      url: snowflake/v1.11.0/snowflake_windows_amd64_v1.11.0.tar.gz
    version: v1.11.0
  - packages:
    - platform: linux_amd64
      url: snowflake/1.9.0/snowflake_linux_amd64_1.9.0.tar.gz
    version: 1.9.0
";

        [Fact]
        public void ParsesTheRegistryIndexShape()
        {
            YamlNode root = YamlParser.Parse(RegistryShape);

            YamlNode driver = Assert.Single(root["drivers"].AsSequence());
            Assert.Equal("ASF Snowflake Driver", driver["name"].AsString());
            Assert.Equal("snowflake", driver["path"].AsString());
            Assert.Equal("Apache-2.0", driver["license"].AsString());
            Assert.Equal("https://adbc-drivers.org/drivers/snowflake/", driver["docs_url"].AsString());

            YamlNode url = Assert.Single(driver["urls"].AsSequence());
            Assert.Equal("https://arrow.apache.org/adbc/", url.AsString());

            Assert.Equal(2, driver["pkginfo"].AsSequence().Count);
        }

        [Fact]
        public void FoldsPlainMultiLineScalarsIntoOneLine()
        {
            YamlNode root = YamlParser.Parse(RegistryShape);
            YamlNode driver = root["drivers"].AsSequence()[0];

            Assert.Equal(
                "An ADBC driver for Snowflake developed under the Apache Software Foundation",
                driver["description"].AsString());
        }

        [Fact]
        public void KeepsASiblingKeyThatFollowsANestedSequence()
        {
            // "version" sits after the "packages" sequence at the same indentation as
            // "packages"; a parser that lets the inner sequence swallow it would lose it.
            YamlNode root = YamlParser.Parse(RegistryShape);
            YamlNode releases = root["drivers"].AsSequence()[0]["pkginfo"];

            Assert.Equal("v1.11.0", releases.AsSequence()[0]["version"].AsString());
            Assert.Equal("1.9.0", releases.AsSequence()[1]["version"].AsString());
            Assert.Equal(2, releases.AsSequence()[0]["packages"].AsSequence().Count);
            Assert.Single(releases.AsSequence()[1]["packages"].AsSequence());
        }

        [Fact]
        public void ReadsAValueWrittenOnTheLineAfterItsKey()
        {
            // The public index wraps long package URLs like this, leaving nothing after
            // the colon:
            //     url:
            //       clickhouse/v0.1.0-alpha.1/clickhouse_linux_amd64_v0.1.0-alpha.1.tar.gz
            YamlNode root = YamlParser.Parse(
                "packages:\n- platform: linux_amd64\n  url: \n    clickhouse/v0.1.0/clickhouse_linux_amd64.tar.gz\n- platform: linux_arm64\n  url: b.tar.gz\n");

            Assert.Equal(2, root["packages"].AsSequence().Count);
            Assert.Equal(
                "clickhouse/v0.1.0/clickhouse_linux_amd64.tar.gz",
                root["packages"].AsSequence()[0]["url"].AsString());
            Assert.Equal("linux_arm64", root["packages"].AsSequence()[1]["platform"].AsString());
        }

        [Fact]
        public void FoldsAWrappedValueWrittenAfterItsKey()
        {
            YamlNode root = YamlParser.Parse("a:\n  first part\n  second part\nb: after\n");
            Assert.Equal("first part second part", root["a"].AsString());
            Assert.Equal("after", root["b"].AsString());
        }

        [Fact]
        public void StillTreatsADeeperMappingAsANestedMapping()
        {
            YamlNode root = YamlParser.Parse("a:\n  b: 1\n  c: 2\n");
            Assert.Equal("1", root["a"]["b"].AsString());
            Assert.Equal("2", root["a"]["c"].AsString());
        }

        [Fact]
        public void ParsesSequenceAtTheSameIndentationAsItsKey()
        {
            YamlNode root = YamlParser.Parse("key:\n- a\n- b\n");
            Assert.Equal(2, root["key"].AsSequence().Count);
            Assert.Equal("a", root["key"].AsSequence()[0].AsString());
        }

        [Fact]
        public void ParsesSequenceIndentedBelowItsKey()
        {
            YamlNode root = YamlParser.Parse("key:\n  - a\n  - b\n");
            Assert.Equal(2, root["key"].AsSequence().Count);
        }

        [Theory]
        [InlineData("a: \"quoted value\"", "quoted value")]
        [InlineData("a: 'single quoted'", "single quoted")]
        [InlineData("a: 'it''s here'", "it's here")]
        [InlineData("a: \"tab\\there\"", "tab\there")]
        [InlineData("a: \"\\u0041\"", "A")]
        [InlineData("a: plain value", "plain value")]
        [InlineData("a: value # trailing comment", "value")]
        [InlineData("a: https://example.com/#fragment", "https://example.com/#fragment")]
        public void ParsesScalarForms(string yaml, string expected) =>
            Assert.Equal(expected, YamlParser.Parse(yaml)["a"].AsString());

        [Theory]
        [InlineData("a:")]
        [InlineData("a: ~")]
        [InlineData("a: null")]
        [InlineData("a: # only a comment")]
        public void TreatsEmptyAndNullLiteralsAsNull(string yaml) =>
            Assert.Null(YamlParser.Parse(yaml)["a"].AsString());

        [Fact]
        public void ParsesLiteralBlockScalars()
        {
            YamlNode root = YamlParser.Parse("a: |\n  line one\n  line two\nb: after\n");
            Assert.Equal("line one\nline two\n", root["a"].AsString());
            Assert.Equal("after", root["b"].AsString());
        }

        [Fact]
        public void ParsesFoldedBlockScalars()
        {
            YamlNode root = YamlParser.Parse("a: >\n  line one\n  line two\nb: after\n");
            Assert.Equal("line one line two\n", root["a"].AsString());
            Assert.Equal("after", root["b"].AsString());
        }

        [Fact]
        public void StripsBlockScalarTrailingNewlineWhenChomped()
        {
            YamlNode root = YamlParser.Parse("a: |-\n  only\n");
            Assert.Equal("only", root["a"].AsString());
        }

        [Fact]
        public void SkipsCommentsBlankLinesAndDocumentMarkers()
        {
            YamlNode root = YamlParser.Parse("---\n# a comment\n\na: 1\n\n# another\nb: 2\n...\n");
            Assert.Equal("1", root["a"].AsString());
            Assert.Equal("2", root["b"].AsString());
        }

        [Fact]
        public void ReturnsNullNodeForMissingKeys()
        {
            YamlNode root = YamlParser.Parse("a: 1");
            Assert.True(root["missing"].IsNull);
            Assert.Null(root["missing"].AsString());
            Assert.Empty(root["missing"].AsSequence());
        }

        [Fact]
        public void ParsesAnEmptyDocumentAsNull() =>
            Assert.True(YamlParser.Parse("# nothing but a comment\n").IsNull);

        // Constructs that are rejected rather than guessed at: mis-parsing a registry
        // index would change which bytes get downloaded, so ambiguity must fail loudly.

        [Theory]
        [InlineData("a: {b: 1}", "Flow-style")]
        [InlineData("a: [1, 2]", "Flow-style")]
        [InlineData("a: &anchor value", "Anchors")]
        [InlineData("a: *alias", "Aliases")]
        [InlineData("a: !!str value", "Tags")]
        public void RejectsUnsupportedConstructs(string yaml, string expectedFragment)
        {
            YamlParseException ex = Assert.Throws<YamlParseException>(() => YamlParser.Parse(yaml));
            Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsTabIndentation()
        {
            YamlParseException ex = Assert.Throws<YamlParseException>(() => YamlParser.Parse("a:\n\tb: 1\n"));
            Assert.Contains("Tabs", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsDuplicateKeys()
        {
            YamlParseException ex = Assert.Throws<YamlParseException>(() => YamlParser.Parse("a: 1\na: 2\n"));
            Assert.Contains("Duplicate key", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RejectsAnUnterminatedQuotedScalar() =>
            Assert.Throws<YamlParseException>(() => YamlParser.Parse("a: \"unterminated\n"));

        [Fact]
        public void RejectsABareScalarWhereAKeyIsExpected() =>
            Assert.Throws<YamlParseException>(() => YamlParser.Parse("just a scalar\n"));

        [Fact]
        public void ReportsTheLineNumberAndSourceName()
        {
            YamlParseException ex = Assert.Throws<YamlParseException>(
                () => YamlParser.Parse("a: 1\nb: {c: 2}\n", "index.yaml"));

            Assert.Equal(2, ex.Line);
            Assert.Contains("index.yaml(2)", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ThrowsWhenAScalarIsReadAsASequence()
        {
            YamlNode root = YamlParser.Parse("a: scalar");
            Assert.Throws<YamlParseException>(() => root["a"].AsSequence());
        }

        [Fact]
        public void ThrowsWhenAMappingIsReadAsAScalar()
        {
            YamlNode root = YamlParser.Parse("a:\n  b: 1\n");
            Assert.Throws<YamlParseException>(() => root["a"].AsString());
        }
    }
}
