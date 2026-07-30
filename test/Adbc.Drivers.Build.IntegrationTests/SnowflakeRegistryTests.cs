using System;
using System.IO;
using Adbc.Drivers.Build.IntegrationTests.TestSupport;
using Xunit;

namespace Adbc.Drivers.Build.IntegrationTests
{
    /// <summary>
    /// Acquires the real Apache Snowflake ADBC driver from the public Columnar registry.
    /// </summary>
    /// <remarks>
    /// Opt-in via <c>ADBC_DRIVERS_TESTS_NETWORK=1</c>. These download roughly 33 MB and
    /// depend on a third-party CDN, so they cannot gate every commit — but they are the
    /// only tests that prove the live index schema and a real driver archive still parse,
    /// which a fixture registry can never establish.
    /// </remarks>
    [Collection("msbuild")]
    public sealed class SnowflakeRegistryTests
    {
        private const string SnowflakeItem =
            "    <AdbcDriver Include=\"snowflake\" Version=\"1.11.0\" Rids=\"win-x64;linux-x64\" />\n";

        /// <summary>The lock file committed alongside the sample, reused verbatim.</summary>
        private static string CommittedLockFile =>
            Path.Combine(ConsumerProject.RepoRoot, "samples", "SnowflakeSample", "adbc.drivers.lock.json");

        [NetworkFact]
        public void BuildsAgainstTheCommittedLockFile()
        {
            using ConsumerProject consumer = new ConsumerProject("snowflake");
            consumer.WriteProject(SnowflakeItem);

            // The committed lock is the input under test: an ordinary build must reproduce
            // exactly the artifacts it names.
            File.Copy(CommittedLockFile, consumer.LockFile, overwrite: true);

            BuildResult build = consumer.Run("build", "-p:AdbcDriverNetworkMode=Online");
            Assert.True(build.Succeeded, build.ToString());

            Assert.True(consumer.OutputFileExists(
                "adbc", "snowflake", "1.11.0", "win-x64", "libadbc_driver_snowflake.dll"));
            Assert.True(consumer.OutputFileExists(
                "adbc", "snowflake", "1.11.0", "linux-x64", "libadbc_driver_snowflake.so"));

            // The Apache licence and notice must travel with the driver.
            Assert.True(consumer.OutputFileExists("adbc", "snowflake", "1.11.0", "win-x64", "LICENSE"));
            Assert.True(consumer.OutputFileExists("adbc", "snowflake", "1.11.0", "win-x64", "NOTICE"));

            string manifest = consumer.ReadOutputFile("adbc", "snowflake.toml");
            Assert.Contains("name = \"ASF Snowflake Driver\"", manifest, StringComparison.Ordinal);
            Assert.Contains("version = \"1.11.0\"", manifest, StringComparison.Ordinal);
            Assert.Contains("license = \"Apache-2.0\"", manifest, StringComparison.Ordinal);
            Assert.Contains("windows_amd64 = ", manifest, StringComparison.Ordinal);
            Assert.Contains("linux_amd64 = ", manifest, StringComparison.Ordinal);
        }

        [NetworkFact]
        public void ResolvingAgainstTheLiveRegistryReproducesTheCommittedLockFile()
        {
            // If this fails, either the registry republished 1.11.0 with different bytes
            // or the resolver's output stopped being deterministic. Both are worth knowing.
            using ConsumerProject consumer = new ConsumerProject("snowflake-resolve");
            consumer.WriteProject(SnowflakeItem);

            BuildResult resolve = consumer.Run("build -t:ResolveAdbcDriverLock");
            Assert.True(resolve.Succeeded, resolve.ToString());

            Assert.Equal(
                File.ReadAllText(CommittedLockFile).Replace("\r\n", "\n"),
                File.ReadAllText(consumer.LockFile).Replace("\r\n", "\n"));
        }

        [NetworkFact]
        public void RefusesToUseAnArchiveThatDoesNotMatchTheLockedHash()
        {
            using ConsumerProject consumer = new ConsumerProject("snowflake-tampered");
            consumer.WriteProject(SnowflakeItem);

            string lockJson = File.ReadAllText(CommittedLockFile);
            const string Marker = "\"archiveSha256\": \"";
            int index = lockJson.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
            File.WriteAllText(
                consumer.LockFile,
                lockJson.Substring(0, index) + new string('b', 64) + lockJson.Substring(index + 64));

            BuildResult build = consumer.Run("build", "-p:AdbcDriverNetworkMode=Online");

            Assert.False(build.Succeeded, "A driver whose hash disagrees with the lock must not be used.");
            Assert.Contains("Refusing to use it", build.Output, StringComparison.Ordinal);
        }

        [NetworkFact]
        public void PublishesTheRealDriverWithAUsableManifest()
        {
            using ConsumerProject consumer = new ConsumerProject("snowflake-publish");
            consumer.WriteProject(SnowflakeItem);
            File.Copy(CommittedLockFile, consumer.LockFile, overwrite: true);

            BuildResult publish = consumer.Run("publish -c Debug -f net8.0", "-p:AdbcDriverNetworkMode=Online");
            Assert.True(publish.Succeeded, publish.ToString());

            string driver = Path.Combine(
                consumer.PublishDirectory, "adbc", "snowflake", "1.11.0", "win-x64", "libadbc_driver_snowflake.dll");
            Assert.True(File.Exists(driver), publish.ToString());

            // The published manifest must name the published driver, not the build one.
            string manifest = File.ReadAllText(Path.Combine(consumer.PublishDirectory, "adbc", "snowflake.toml"));
            Assert.Contains(driver.Replace("\\", "\\\\"), manifest, StringComparison.Ordinal);
        }
    }
}
