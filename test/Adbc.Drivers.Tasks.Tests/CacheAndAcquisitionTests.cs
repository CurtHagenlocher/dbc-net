using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Adbc.Drivers.Build.Acquisition;
using Adbc.Drivers.Build.Archives;
using Adbc.Drivers.Build.Caching;
using Adbc.Drivers.Build.Locking;
using Adbc.Drivers.Build.Registry;
using Adbc.Drivers.Build.Security;
using Adbc.Drivers.Build.Tests.TestSupport;
using Adbc.Drivers.Build.Util;
using Xunit;

namespace Adbc.Drivers.Build.Tests
{
    public sealed class ContentAddressedCacheTests
    {
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

        [Fact]
        public void InstallsVerifiesAndExtractsAnArchive()
        {
            using TempDirectory temp = new TempDirectory("cache");
            (Uri url, string hash) = PublishArchive(temp, "libadbc_driver_fixture.so", "driver bytes");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            CacheEntry entry = Install(cache, url, hash);

            Assert.True(entry.Cached);
            Assert.Equal(hash, entry.ArchiveSha256);
            Assert.Equal("libadbc_driver_fixture.so", entry.Receipt.DriverFile);
            Assert.Equal("Fixture Publisher", entry.Receipt.Publisher);
            Assert.Equal("Apache-2.0", entry.Receipt.License);
            Assert.True(File.Exists(entry.DriverPath));
            Assert.Equal("driver bytes", File.ReadAllText(entry.DriverPath));
        }

        [Fact]
        public void LaysTheCacheOutByContentHash()
        {
            using TempDirectory temp = new TempDirectory("cache");
            (Uri url, string hash) = PublishArchive(temp, "d.so", "x");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            Install(cache, url, hash);

            string entryDirectory = cache.EntryDirectory(hash);
            Assert.True(File.Exists(Path.Combine(entryDirectory, "archive.tar.gz")));
            Assert.True(File.Exists(Path.Combine(entryDirectory, "receipt.json")));
            Assert.True(Directory.Exists(Path.Combine(entryDirectory, "extract")));
            Assert.Equal(hash, Path.GetFileName(entryDirectory));

            // Nothing is left behind in staging.
            string staging = Path.Combine(cache.Root, "tmp");
            Assert.True(!Directory.Exists(staging) || Directory.GetDirectories(staging).Length == 0);
        }

        [Fact]
        public void ServesASecondRequestFromTheCacheWithoutDownloading()
        {
            using TempDirectory temp = new TempDirectory("cache");
            (Uri url, string hash) = PublishArchive(temp, "d.so", "x");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            Install(cache, url, hash);

            CountingTransport counting = new CountingTransport(new DefaultRegistryTransport(LockTimeout));
            CacheEntry second = cache.Install(url, hash, counting, NullSignatureVerifier.Instance, ExtractionLimits.Default, LockTimeout);

            Assert.Equal(0, counting.Reads);
            Assert.Equal(hash, second.ArchiveSha256);
        }

        [Fact]
        public void RefusesAnArchiveWhoseHashDoesNotMatchTheLock()
        {
            using TempDirectory temp = new TempDirectory("cache");
            (Uri url, _) = PublishArchive(temp, "d.so", "x");
            string wrongHash = new string('a', 64);

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));

            IntegrityException ex = Assert.Throws<IntegrityException>(() => Install(cache, url, wrongHash));
            Assert.Contains("Refusing to use it", ex.Message, StringComparison.Ordinal);

            // Nothing was admitted to the cache, and in particular nothing was extracted.
            Assert.False(Directory.Exists(Path.Combine(cache.EntryDirectory(wrongHash), "extract")));
        }

        [Fact]
        public void TreatsAnEntryWithoutAReceiptAsAMiss()
        {
            // The receipt is written last, so a crash mid-promotion leaves a directory
            // that must never be mistaken for a complete entry.
            using TempDirectory temp = new TempDirectory("cache");
            (Uri url, string hash) = PublishArchive(temp, "d.so", "x");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            Install(cache, url, hash);

            File.Delete(Path.Combine(cache.EntryDirectory(hash), "receipt.json"));

            Assert.Null(cache.TryOpen(hash));
        }

        [Fact]
        public void TreatsAnUnreadableReceiptAsAMiss()
        {
            using TempDirectory temp = new TempDirectory("cache");
            (Uri url, string hash) = PublishArchive(temp, "d.so", "x");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            Install(cache, url, hash);

            string receipt = Path.Combine(cache.EntryDirectory(hash), "receipt.json");
            File.SetAttributes(receipt, FileAttributes.Normal);
            File.WriteAllText(receipt, "{ not json");

            Assert.Null(cache.TryOpen(hash));
        }

        [Fact]
        public void RepairsAnInterruptedPromotionOnTheNextInstall()
        {
            using TempDirectory temp = new TempDirectory("cache");
            (Uri url, string hash) = PublishArchive(temp, "d.so", "x");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            Install(cache, url, hash);
            File.Delete(Path.Combine(cache.EntryDirectory(hash), "receipt.json"));

            CacheEntry repaired = Install(cache, url, hash);

            Assert.Equal(hash, repaired.ArchiveSha256);
            Assert.True(File.Exists(Path.Combine(cache.EntryDirectory(hash), "receipt.json")));
        }

        [Fact]
        public void ProducesTheSameReceiptForTheSameArchive()
        {
            // Receipts carry no timestamps or machine identifiers, so one produced on a
            // developer machine is directly comparable with one produced in CI.
            using TempDirectory temp = new TempDirectory("cache");
            (Uri url, string hash) = PublishArchive(temp, "d.so", "x");

            ContentAddressedCache first = new ContentAddressedCache(temp.Combine("cache-a"));
            ContentAddressedCache second = new ContentAddressedCache(temp.Combine("cache-b"));

            string a = File.ReadAllText(Path.Combine(first.EntryDirectory(Install(first, url, hash).ArchiveSha256), "receipt.json"));
            string b = File.ReadAllText(Path.Combine(second.EntryDirectory(Install(second, url, hash).ArchiveSha256), "receipt.json"));

            Assert.Equal(a, b);
        }

        [Fact]
        public void RecordsThatNoSignatureCheckWasPerformed()
        {
            // Recorded honestly rather than implying verification that did not happen.
            using TempDirectory temp = new TempDirectory("cache");
            (Uri url, string hash) = PublishArchive(temp, "d.so", "x");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            Assert.Equal("NotAttempted", Install(cache, url, hash).Receipt.SignatureVerification);
        }

        [Fact]
        public void FailsWhenSignatureVerificationFails()
        {
            using TempDirectory temp = new TempDirectory("cache");
            (Uri url, string hash) = PublishArchive(temp, "d.so", "x");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));

            IntegrityException ex = Assert.Throws<IntegrityException>(() => cache.Install(
                url,
                hash,
                new DefaultRegistryTransport(LockTimeout),
                new RejectingSignatureVerifier(),
                ExtractionLimits.Default,
                LockTimeout));

            Assert.Contains("did not verify", ex.Message, StringComparison.Ordinal);
            Assert.Null(cache.TryOpen(hash));
        }

        [Fact]
        public void RejectsAnArchiveWithNoManifest()
        {
            using TempDirectory temp = new TempDirectory("cache");
            string archive = new TarGzBuilder().AddFile("d.so", "x").WriteTarGz(temp.Combine("feed", "p.tar.gz"));
            Uri url = new Uri(archive);
            string hash = Hashing.Sha256File(archive);

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));

            Packaging.PackageManifestException ex =
                Assert.Throws<Packaging.PackageManifestException>(() => Install(cache, url, hash));
            Assert.Contains("does not contain a MANIFEST", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void MaterializesWithoutCachingForReadOnlyBuilds()
        {
            using TempDirectory temp = new TempDirectory("cache");
            (Uri url, string hash) = PublishArchive(temp, "d.so", "payload");

            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));
            string destination = temp.Combine("ephemeral");

            CacheEntry entry = cache.MaterializeUncached(
                url, hash, new DefaultRegistryTransport(LockTimeout), NullSignatureVerifier.Instance,
                ExtractionLimits.Default, destination);

            Assert.False(entry.Cached);
            Assert.Equal("payload", File.ReadAllText(entry.DriverPath));
            Assert.Null(cache.TryOpen(hash));
            Assert.False(Directory.Exists(Path.Combine(cache.Root, "sha256")));
        }

        [Fact]
        public async Task SerializesConcurrentInstallsOfTheSameArchive()
        {
            // Parallel MSBuild puts several processes on one cache; the per-hash lock and
            // atomic promotion must leave exactly one intact entry.
            using TempDirectory temp = new TempDirectory("cache");
            (Uri url, string hash) = PublishArchive(temp, "d.so", "shared payload");

            string cacheRoot = temp.Combine("cache");
            Task<string>[] workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                ContentAddressedCache cache = new ContentAddressedCache(cacheRoot);
                return Install(cache, url, hash).DriverPath;
            })).ToArray();

            string[] paths = await Task.WhenAll(workers);

            Assert.All(paths, path => Assert.Equal("shared payload", File.ReadAllText(path)));

            ContentAddressedCache verifier = new ContentAddressedCache(cacheRoot);
            Assert.NotNull(verifier.TryOpen(hash));
            Assert.Single(Directory.GetDirectories(Path.Combine(cacheRoot, "sha256")));
        }

        [Fact]
        public void RejectsAHashThatIsNotSha256()
        {
            using TempDirectory temp = new TempDirectory("cache");
            ContentAddressedCache cache = new ContentAddressedCache(temp.Combine("cache"));

            Assert.Throws<ArgumentException>(() => cache.TryOpen("nope"));
            Assert.Throws<ArgumentException>(() => cache.TryOpen(new string('a', 63)));
        }

        internal static (Uri Url, string Sha256) PublishArchive(
            TempDirectory temp,
            string driverFileName,
            string driverContent,
            string? entrypoint = null)
        {
            string path = TarGzBuilder
                .CreateDriverPackage(driverFileName, driverContent, entrypoint: entrypoint)
                .WriteTarGz(temp.Combine("feed", Guid.NewGuid().ToString("N") + ".tar.gz"));

            return (new Uri(path), Hashing.Sha256File(path));
        }

        private static CacheEntry Install(ContentAddressedCache cache, Uri url, string hash) =>
            cache.Install(
                url,
                hash,
                new DefaultRegistryTransport(LockTimeout),
                NullSignatureVerifier.Instance,
                ExtractionLimits.Default,
                LockTimeout);

        private sealed class RejectingSignatureVerifier : ISignatureVerifier
        {
            public SignatureVerificationResult Verify(string driverPath, string? signaturePath) =>
                SignatureVerificationResult.Failed("the fixture verifier always fails");
        }

        private sealed class CountingTransport : IRegistryTransport
        {
            private readonly IRegistryTransport _inner;

            public CountingTransport(IRegistryTransport inner) => _inner = inner;

            public int Reads { get; private set; }

            public Stream OpenRead(Uri uri)
            {
                Reads++;
                return _inner.OpenRead(uri);
            }

            public string ReadAllText(Uri uri, long maxBytes)
            {
                Reads++;
                return _inner.ReadAllText(uri, maxBytes);
            }
        }
    }
}
