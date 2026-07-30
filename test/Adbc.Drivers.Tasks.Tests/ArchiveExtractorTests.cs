using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Adbc.Drivers.Build.Archives;
using Adbc.Drivers.Build.Tests.TestSupport;
using Adbc.Drivers.Build.Util;
using Xunit;

namespace Adbc.Drivers.Build.Tests
{
    public sealed class ArchiveExtractorTests
    {
        [Fact]
        public void ExtractsAWellFormedDriverPackage()
        {
            using TempDirectory temp = new TempDirectory("extract");
            string archive = TarGzBuilder
                .CreateDriverPackage("libadbc_driver_fixture.so", "driver bytes")
                .WriteTarGz(temp.Combine("package.tar.gz"));

            IReadOnlyList<ExtractedFile> files = ArchiveExtractor.ExtractTarGz(archive, temp.Combine("out"));

            Assert.Equal(
                new[] { "LICENSE", "MANIFEST", "NOTICE", "libadbc_driver_fixture.so", "libadbc_driver_fixture.so.sig" },
                files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToArray());

            Assert.True(File.Exists(temp.Combine("out", "MANIFEST")));
            Assert.Equal("driver bytes", File.ReadAllText(temp.Combine("out", "libadbc_driver_fixture.so")));
        }

        [Fact]
        public void HashesEveryExtractedFile()
        {
            using TempDirectory temp = new TempDirectory("hash");
            string archive = new TarGzBuilder()
                .AddFile("MANIFEST", "[Files]\ndriver = \"d.so\"\n")
                .AddFile("d.so", "content")
                .WriteTarGz(temp.Combine("p.tar.gz"));

            IReadOnlyList<ExtractedFile> files = ArchiveExtractor.ExtractTarGz(archive, temp.Combine("out"));

            ExtractedFile driver = files.Single(f => f.RelativePath == "d.so");
            Assert.Equal(Hashing.Sha256Bytes(Encoding.UTF8.GetBytes("content")), driver.Sha256);
            Assert.Equal(7, driver.Length);
        }

        [Fact]
        public void ExtractsNestedDirectories()
        {
            using TempDirectory temp = new TempDirectory("nested");
            string archive = new TarGzBuilder()
                .AddDirectory("sub")
                .AddFile("sub/inner.txt", "x")
                .AddFile("MANIFEST", "[Files]\ndriver = \"sub/inner.txt\"\n")
                .WriteTarGz(temp.Combine("p.tar.gz"));

            IReadOnlyList<ExtractedFile> files = ArchiveExtractor.ExtractTarGz(archive, temp.Combine("out"));

            Assert.Contains(files, f => f.RelativePath == "sub/inner.txt");
            Assert.True(File.Exists(temp.Combine("out", "sub", "inner.txt")));
        }

        [Fact]
        public void AcceptsNamesUpToThePathLengthLimit()
        {
            using TempDirectory temp = new TempDirectory("longname");
            string longName = new string('a', 90) + ".so";

            string archive = new TarGzBuilder()
                .AddFile(longName, "x")
                .AddFile("MANIFEST", "[Files]\ndriver = \"x\"\n")
                .WriteTarGz(temp.Combine("p.tar.gz"));

            IReadOnlyList<ExtractedFile> files = ArchiveExtractor.ExtractTarGz(archive, temp.Combine("out"));
            Assert.Contains(files, f => f.RelativePath == longName);
        }

        [Fact]
        public void RejectsNamesLongerThanThePathLengthLimit()
        {
            using TempDirectory temp = new TempDirectory("toolong");
            string archive = new TarGzBuilder()
                .AddFile(new string('a', 90) + ".so", "x")
                .WriteTarGz(temp.Combine("p.tar.gz"));

            UnsafeArchiveException ex = Assert.Throws<UnsafeArchiveException>(
                () => ArchiveExtractor.ExtractTarGz(
                    archive,
                    temp.Combine("out"),
                    new ExtractionLimits { MaxPathLength = 32 }));
            Assert.Contains("longer than 32", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // Hostile archives. Extraction happens before any signature check, so these are
        // the checks that stand between a downloaded archive and the file system.

        [Theory]
        [InlineData("../escaped.txt", "escapes")]
        [InlineData("sub/../../escaped.txt", "escapes")]
        [InlineData("/etc/passwd", "absolute")]
        [InlineData("C:/windows/system32/evil.dll", "drive-qualified")]
        [InlineData("sub\\evil.txt", "backslash")]
        [InlineData("./relative.txt", "'.' segment")]
        [InlineData("sub//double.txt", "empty path segment")]
        public void RejectsEntryNamesThatEscapeTheDestination(string name, string expectedFragment)
        {
            using TempDirectory temp = new TempDirectory("escape");
            string archive = new TarGzBuilder()
                .AddFile(name, "payload")
                .WriteTarGz(temp.Combine("p.tar.gz"));

            UnsafeArchiveException ex = Assert.Throws<UnsafeArchiveException>(
                () => ArchiveExtractor.ExtractTarGz(archive, temp.Combine("out")));

            Assert.Contains(expectedFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(temp.Path, "escaped.txt")));
        }

        [Fact]
        public void RejectsReservedDeviceNames()
        {
            // Rejected on every platform so an archive extracts identically on Linux and
            // Windows rather than succeeding on one and failing on the other.
            using TempDirectory temp = new TempDirectory("reserved");
            string archive = new TarGzBuilder().AddFile("CON.txt", "x").WriteTarGz(temp.Combine("p.tar.gz"));

            UnsafeArchiveException ex = Assert.Throws<UnsafeArchiveException>(
                () => ArchiveExtractor.ExtractTarGz(archive, temp.Combine("out")));
            Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RejectsSymbolicLinks()
        {
            using TempDirectory temp = new TempDirectory("symlink");
            string archive = new TarGzBuilder()
                .AddFile("MANIFEST", "x")
                .AddSymbolicLink("libadbc.so", "/usr/lib/attacker.so")
                .WriteTarGz(temp.Combine("p.tar.gz"));

            UnsafeArchiveException ex = Assert.Throws<UnsafeArchiveException>(
                () => ArchiveExtractor.ExtractTarGz(archive, temp.Combine("out")));
            Assert.Contains("link", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RejectsHardLinks()
        {
            using TempDirectory temp = new TempDirectory("hardlink");
            string archive = new TarGzBuilder()
                .AddFile("MANIFEST", "x")
                .AddHardLink("copy", "MANIFEST")
                .WriteTarGz(temp.Combine("p.tar.gz"));

            Assert.Throws<UnsafeArchiveException>(() => ArchiveExtractor.ExtractTarGz(archive, temp.Combine("out")));
        }

        [Theory]
        [InlineData('3')]
        [InlineData('4')]
        [InlineData('6')]
        public void RejectsDeviceAndFifoEntries(char typeFlag)
        {
            using TempDirectory temp = new TempDirectory("special");
            string archive = new TarGzBuilder()
                .AddFile("MANIFEST", "x")
                .AddEntryWithTypeFlag("special", typeFlag)
                .WriteTarGz(temp.Combine("p.tar.gz"));

            UnsafeArchiveException ex = Assert.Throws<UnsafeArchiveException>(
                () => ArchiveExtractor.ExtractTarGz(archive, temp.Combine("out")));
            Assert.Contains("unsupported type", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RejectsDuplicateEntries()
        {
            // A duplicate lets a later entry quietly replace a file that was already
            // hashed and recorded.
            using TempDirectory temp = new TempDirectory("duplicate");
            string archive = new TarGzBuilder()
                .AddFile("driver.so", "first")
                .AddFile("driver.so", "second")
                .WriteTarGz(temp.Combine("p.tar.gz"));

            UnsafeArchiveException ex = Assert.Throws<UnsafeArchiveException>(
                () => ArchiveExtractor.ExtractTarGz(archive, temp.Combine("out")));
            Assert.Contains("more than one entry", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RejectsTooManyEntries()
        {
            using TempDirectory temp = new TempDirectory("count");
            TarGzBuilder builder = new TarGzBuilder();
            for (int i = 0; i < 20; i++)
            {
                builder.AddFile($"f{i}.txt", "x");
            }

            string archive = builder.WriteTarGz(temp.Combine("p.tar.gz"));

            UnsafeArchiveException ex = Assert.Throws<UnsafeArchiveException>(
                () => ArchiveExtractor.ExtractTarGz(
                    archive,
                    temp.Combine("out"),
                    new ExtractionLimits { MaxEntryCount = 5 }));
            Assert.Contains("more than 5 entries", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RejectsArchivesThatExpandBeyondTheLimit()
        {
            using TempDirectory temp = new TempDirectory("bomb");
            string archive = new TarGzBuilder()
                .AddFile("big.bin", new byte[4096])
                .WriteTarGz(temp.Combine("p.tar.gz"));

            UnsafeArchiveException ex = Assert.Throws<UnsafeArchiveException>(
                () => ArchiveExtractor.ExtractTarGz(
                    archive,
                    temp.Combine("out"),
                    new ExtractionLimits { MaxTotalBytes = 1024, MaxEntryBytes = 1024 }));
            Assert.Contains("limit", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RejectsAnEntryThatRunsPastTheEndOfTheArchive()
        {
            // A header can claim more bytes than the archive holds. Within one 512-byte
            // block the padding makes the claim unverifiable, which is inherent to tar;
            // what must never happen is silently accepting a truncated file.
            using TempDirectory temp = new TempDirectory("truncated");
            string archive = new TarGzBuilder()
                .AddFileWithLyingSize("f.bin", Encoding.UTF8.GetBytes("short"), declaredSize: 100_000)
                .WriteTarGz(temp.Combine("p.tar.gz"));

            InvalidDataException ex = Assert.ThrowsAny<InvalidDataException>(
                () => ArchiveExtractor.ExtractTarGz(archive, temp.Combine("out")));
            Assert.Contains("ended", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RejectsAnEmptyArchive()
        {
            using TempDirectory temp = new TempDirectory("empty");
            string archive = new TarGzBuilder().WriteTarGz(temp.Combine("p.tar.gz"));

            InvalidDataException ex = Assert.Throws<InvalidDataException>(
                () => ArchiveExtractor.ExtractTarGz(archive, temp.Combine("out")));
            Assert.Contains("no files", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RejectsSomethingThatIsNotATarArchive()
        {
            using TempDirectory temp = new TempDirectory("garbage");
            string path = temp.Combine("p.tar.gz");
            using (FileStream file = File.Create(path))
            using (System.IO.Compression.GZipStream gzip =
                new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Compress))
            {
                byte[] garbage = Encoding.UTF8.GetBytes(new string('q', 2048));
                gzip.Write(garbage, 0, garbage.Length);
            }

            Assert.ThrowsAny<InvalidDataException>(() => ArchiveExtractor.ExtractTarGz(path, temp.Combine("out")));
        }

        [Fact]
        public void SkipsEntriesTheCallerDoesNotRead()
        {
            // The reader must consume an unread entry's payload and its block padding, or
            // every entry after the first would be misaligned.
            using TempDirectory temp = new TempDirectory("skip");
            byte[] tar = new TarGzBuilder()
                .AddFile("a.txt", new string('a', 700))
                .AddFile("b.txt", "second")
                .AddFile("c.txt", new string('c', 1300))
                .ToTar();

            List<string> names = new List<string>();
            using (MemoryStream stream = new MemoryStream(tar))
            using (TarReader reader = new TarReader(stream))
            {
                TarEntry? entry;
                while ((entry = reader.MoveNext()) is not null)
                {
                    names.Add(entry.Name);
                }
            }

            Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, names.ToArray());
        }
    }
}
