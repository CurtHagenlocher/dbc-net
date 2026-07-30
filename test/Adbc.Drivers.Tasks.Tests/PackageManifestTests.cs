using System;
using Adbc.Drivers.Build.Packaging;
using Adbc.Drivers.Build.Text;
using Xunit;

namespace Adbc.Drivers.Build.Tests
{
    public sealed class PackageManifestTests
    {
        /// <summary>
        /// The MANIFEST from the real Snowflake 1.11.0 Windows package. It has neither
        /// <c>manifest_version</c> nor a <c>[Driver]</c> table, which the documented
        /// format implies are present, so both must be optional.
        /// </summary>
        private const string RealSnowflakeManifest = @"# Copyright (c) 2025 ADBC Drivers Contributors
#
# Licensed under the Apache License, Version 2.0 (the ""License"");
# you may not use this file except in compliance with the License.

name = ""ADBC Driver Foundry Driver for Snowflake""
description = ""An ADBC driver for Snowflake developed by the ADBC Driver Foundry""
publisher = ""ADBC Drivers Contributors""
license = ""Apache-2.0""
version = ""v1.11.0""

[ADBC]
version = ""v1.1.0""

[Files]
driver = ""libadbc_driver_snowflake.dll""
signature = ""libadbc_driver_snowflake.dll.sig""
";

        [Fact]
        public void ParsesARealDriverManifest()
        {
            PackageManifest manifest = PackageManifest.Parse(RealSnowflakeManifest);

            Assert.Null(manifest.ManifestVersion);
            Assert.Equal("ADBC Driver Foundry Driver for Snowflake", manifest.Name);
            Assert.Equal("ADBC Drivers Contributors", manifest.Publisher);
            Assert.Equal("Apache-2.0", manifest.License);
            Assert.Equal("v1.11.0", manifest.Version);
            Assert.Equal("v1.1.0", manifest.AdbcVersion);
            Assert.Null(manifest.Entrypoint);
            Assert.Equal("libadbc_driver_snowflake.dll", manifest.DriverFile);
            Assert.Equal("libadbc_driver_snowflake.dll.sig", manifest.SignatureFile);
            Assert.Equal(2, manifest.Files.Count);
        }

        [Fact]
        public void AcceptsAnExplicitManifestVersionOfOne()
        {
            PackageManifest manifest = PackageManifest.Parse(
                "manifest_version = 1\n[Files]\ndriver = \"d.so\"\n");
            Assert.Equal(1, manifest.ManifestVersion);
        }

        [Fact]
        public void RejectsAnUnknownManifestVersion()
        {
            PackageManifestException ex = Assert.Throws<PackageManifestException>(
                () => PackageManifest.Parse("manifest_version = 2\n[Files]\ndriver = \"d.so\"\n"));
            Assert.Contains("manifest_version 2", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ReadsAnEntrypointWhenDeclared()
        {
            PackageManifest manifest = PackageManifest.Parse(
                "[Driver]\nentrypoint = \"AdbcDriverInit\"\n\n[Files]\ndriver = \"d.so\"\n");
            Assert.Equal("AdbcDriverInit", manifest.Entrypoint);
        }

        [Fact]
        public void RequiresAFilesTable()
        {
            PackageManifestException ex = Assert.Throws<PackageManifestException>(
                () => PackageManifest.Parse("name = \"x\"\n"));
            Assert.Contains("[Files]", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RequiresADriverEntry()
        {
            // Without it there is no way to know which extracted file to load, so this is
            // stricter than dbc, which simply skips verification in that case.
            PackageManifestException ex = Assert.Throws<PackageManifestException>(
                () => PackageManifest.Parse("[Files]\nsignature = \"d.sig\"\n"));
            Assert.Contains("does not name a driver", ex.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("../escape.so")]
        [InlineData("/absolute.so")]
        [InlineData("C:\\windows.dll")]
        [InlineData("sub\\back.so")]
        [InlineData("./here.so")]
        public void RejectsFileReferencesThatEscapeTheArchive(string value)
        {
            // A MANIFEST entry is used to locate a file inside the extraction directory,
            // so it must not be able to point outside it.
            Assert.Throws<PackageManifestException>(
                () => PackageManifest.Parse($"[Files]\ndriver = \"{value.Replace("\\", "\\\\")}\"\n"));
        }

        [Fact]
        public void ReportsMalformedTomlAsAManifestProblem()
        {
            PackageManifestException ex = Assert.Throws<PackageManifestException>(
                () => PackageManifest.Parse("this is not toml\n"));
            Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    public sealed class TomlTests
    {
        [Fact]
        public void ParsesTablesValuesAndComments()
        {
            TomlTable table = TomlParser.Parse(
                "# comment\nname = \"x\"\ncount = 3\nflag = true\n\n[section]\nkey = 'literal'\n\n[a.b]\ndeep = \"yes\"\n");

            Assert.Equal("x", table.GetString("name"));
            Assert.Equal(3, table.GetInt32("count"));
            Assert.Equal("true", table.GetString("flag"));
            Assert.Equal("literal", table.GetTable("section")?.GetString("key"));
            Assert.Equal("yes", table.GetTablePath("a", "b")?.GetString("deep"));
        }

        [Fact]
        public void UnescapesBasicStrings()
        {
            TomlTable table = TomlParser.Parse("p = \"C:\\\\dir\\\\file.dll\"\nt = \"a\\tb\"\n");
            Assert.Equal(@"C:\dir\file.dll", table.GetString("p"));
            Assert.Equal("a\tb", table.GetString("t"));
        }

        [Fact]
        public void IgnoresCommentsAfterValues() =>
            Assert.Equal("v", TomlParser.Parse("k = \"v\" # trailing\n").GetString("k"));

        [Theory]
        [InlineData("a = [1, 2]")]
        [InlineData("a = {b = 1}")]
        [InlineData("[[table]]\na = 1")]
        [InlineData("a = \"\"\"multi\"\"\"")]
        [InlineData("no equals here")]
        [InlineData("[unterminated\n")]
        public void RejectsUnsupportedOrMalformedInput(string toml) =>
            Assert.Throws<TomlParseException>(() => TomlParser.Parse(toml));

        [Fact]
        public void WritesWindowsPathsWithEscapedBackslashes()
        {
            TomlTable table = new TomlTable();
            table.GetOrAddTablePath("Driver", "shared")
                .SetString("windows_amd64", @"C:\app\adbc\d.dll");

            string text = TomlWriter.Write(table, null);

            Assert.Contains(@"windows_amd64 = ""C:\\app\\adbc\\d.dll""", text, StringComparison.Ordinal);
            Assert.Equal(
                @"C:\app\adbc\d.dll",
                TomlParser.Parse(text).GetTablePath("Driver", "shared")?.GetString("windows_amd64"));
        }

        [Fact]
        public void KeepsVersionStringsQuoted()
        {
            // "1.11.0" must not be emitted bare and read back as a float.
            TomlTable table = new TomlTable();
            table.SetString("version", "1.11.0");
            table.SetString("manifest_version", "1");

            string text = TomlWriter.Write(table, null);

            Assert.Contains("version = \"1.11.0\"", text, StringComparison.Ordinal);
            Assert.Contains("manifest_version = 1", text, StringComparison.Ordinal);
        }
    }
}
