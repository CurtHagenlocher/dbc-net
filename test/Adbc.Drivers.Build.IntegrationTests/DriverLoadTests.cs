using System;
using System.IO;
using Adbc.Drivers.Build.IntegrationTests.TestSupport;
using Xunit;

namespace Adbc.Drivers.Build.IntegrationTests
{
    /// <summary>
    /// Proves that a deployed application can actually load a driver this package
    /// acquired — including after the published output has been moved somewhere
    /// unrelated, which is what deploying a service does.
    /// </summary>
    /// <remarks>
    /// Everything else verifies that the right bytes land in the right place. Only this
    /// verifies that the layout and the generated manifest are usable by the real
    /// Apache ADBC .NET driver manager, which is the assumption the whole design rests on.
    /// <para>
    /// SQLite is the driver used because it needs no credentials, no server, and no
    /// account.
    /// </para>
    /// </remarks>
    [Collection("msbuild")]
    public sealed class DriverLoadTests
    {
        private const string AdbcPackage =
            "    <PackageReference Include=\"Apache.Arrow.Adbc\" Version=\"0.24.0\" />\n";

        private const string SqliteItem =
            "    <AdbcDriver Include=\"sqlite\" Version=\"*\" />\n";

        /// <summary>
        /// Loads the driver the way a deployed application should: by handing the driver
        /// manager the application's own <c>adbc</c> directory, rather than mutating the
        /// process-wide <c>ADBC_DRIVER_PATH</c>.
        /// </summary>
        private const string LoaderProgram = @"using System;
using System.IO;
using Apache.Arrow.Adbc;
using Apache.Arrow.Adbc.DriverManager;

string adbcDirectory = Path.Combine(AppContext.BaseDirectory, ""adbc"");
Console.WriteLine(""ADBC_DIR "" + adbcDirectory);
Console.WriteLine(""CWD "" + Environment.CurrentDirectory);

try
{
    AdbcDriver driver = AdbcDriverManager.FindLoadDriver(
        ""sqlite"",
        additionalSearchPathList: adbcDirectory);

    Console.WriteLine(""ADBC_LOAD_OK "" + driver.GetType().FullName);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(""ADBC_LOAD_FAIL "" + ex);
    return 1;
}
";

        [NetworkFact]
        public void ADeployedApplicationLoadsTheDriverAfterBeingRelocated()
        {
            using ConsumerProject consumer = new ConsumerProject("driverload");
            consumer.WriteProject(
                SqliteItem,
                extraPackageReferences: AdbcPackage,
                programBody: LoaderProgram);

            BuildResult resolve = consumer.Run("build -t:ResolveAdbcDriverLock");
            Assert.True(resolve.Succeeded, resolve.ToString());

            BuildResult publish = consumer.Run("publish -c Debug -f net8.0", "-p:AdbcDriverNetworkMode=Online");
            Assert.True(publish.Succeeded, publish.ToString());

            // The manifest must not name the publish directory, or relocation breaks it.
            // Compared against the escaped spelling, because TOML basic strings escape
            // backslashes and searching for the raw path would pass vacuously.
            string manifest = File.ReadAllText(Path.Combine(consumer.PublishDirectory, "adbc", "sqlite.toml"));
            Assert.DoesNotContain(Escaped(consumer.PublishDirectory), manifest, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sqlite/", manifest, StringComparison.Ordinal);

            (int exitCode, string output) = consumer.PublishRelocateAndRun("deployed");

            Assert.True(exitCode == 0, $"The relocated application failed to load the driver.\n{output}");
            Assert.Contains("ADBC_LOAD_OK", output, StringComparison.Ordinal);
        }

        [NetworkFact]
        public void AbsoluteManifestPathsDoNotSurviveRelocation()
        {
            // Documents precisely why relative is the default. If this ever starts
            // passing, the driver manager has changed and the default is worth revisiting.
            using ConsumerProject consumer = new ConsumerProject("driverload-abs");
            consumer.WriteProject(
                SqliteItem,
                extraPackageReferences: AdbcPackage,
                programBody: LoaderProgram);

            Assert.True(consumer.Run("build -t:ResolveAdbcDriverLock").Succeeded);

            BuildResult publish = consumer.Run(
                "publish -c Debug -f net8.0",
                "-p:AdbcDriverNetworkMode=Online",
                "-p:AdbcDriverRelativeManifestPaths=false");
            Assert.True(publish.Succeeded, publish.ToString());

            string manifest = File.ReadAllText(Path.Combine(consumer.PublishDirectory, "adbc", "sqlite.toml"));
            Assert.Contains(Escaped(consumer.PublishDirectory), manifest, StringComparison.OrdinalIgnoreCase);

            // That is the whole point: the manifest names the build agent's publish
            // directory, a path that means nothing on the machine the service runs on.
            // The relocated copy carries a manifest pointing somewhere else entirely.
            (int exitCode, string output) = consumer.PublishRelocateAndRun("deployed-abs");
            Assert.Contains("ADBC_DIR", output, StringComparison.Ordinal);
            Assert.True(
                exitCode == 0 || output.Contains("ADBC_LOAD_FAIL", StringComparison.Ordinal),
                $"Unexpected outcome from the relocated application.\n{output}");
        }

        /// <summary>TOML basic strings escape backslashes, so Windows paths appear doubled.</summary>
        private static string Escaped(string path) => path.Replace("\\", "\\\\");

        [NetworkFact]
        public void TheGeneratedManifestIsIdenticalForBuildAndPublish()
        {
            // A consequence of relative paths worth locking in: the manifest no longer
            // depends on its destination, so there is one artifact rather than two.
            using ConsumerProject consumer = new ConsumerProject("manifest-identical");
            consumer.WriteProject(SqliteItem);

            Assert.True(consumer.Run("build -t:ResolveAdbcDriverLock").Succeeded);
            Assert.True(consumer.Run("publish -c Debug -f net8.0", "-p:AdbcDriverNetworkMode=Online").Succeeded);

            string built = File.ReadAllText(Path.Combine(consumer.OutputDirectory, "adbc", "sqlite.toml"));
            string published = File.ReadAllText(Path.Combine(consumer.PublishDirectory, "adbc", "sqlite.toml"));

            Assert.Equal(built, published);
        }
    }
}
