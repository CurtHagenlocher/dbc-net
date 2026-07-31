using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Adbc.Drivers.Build.Tests.TestSupport;

namespace Adbc.Drivers.Build.IntegrationTests.TestSupport
{
    internal sealed class BuildResult
    {
        public BuildResult(int exitCode, string output, string command)
        {
            ExitCode = exitCode;
            Output = output;
            Command = command;
        }

        public int ExitCode { get; }

        public string Output { get; }

        public string Command { get; }

        public bool Succeeded => ExitCode == 0;

        public override string ToString() =>
            $"`{Command}` exited with {ExitCode}.{Environment.NewLine}{Output}";
    }

    /// <summary>
    /// A throwaway consumer project that references the packed
    /// <c>Adbc.Drivers.Build</c> package from the repository's local feed.
    /// </summary>
    /// <remarks>
    /// Each instance gets its own project directory, NuGet package folder, HTTP cache,
    /// and driver cache, so tests neither pollute the developer's global caches nor see
    /// a stale copy of a previously packed version.
    /// </remarks>
    internal sealed class ConsumerProject : IDisposable
    {
        private static readonly Assembly TestAssembly = typeof(ConsumerProject).Assembly;

        private readonly TempDirectory _temp;

        public ConsumerProject(string name)
        {
            _temp = new TempDirectory(name);

            ProjectDirectory = Path.Combine(_temp.Path, name);
            Directory.CreateDirectory(ProjectDirectory);

            PackagesDirectory = Path.Combine(_temp.Path, "packages");
            DriverCacheDirectory = Path.Combine(_temp.Path, "driver-cache");
            Name = name;

            // Stops the throwaway project from inheriting anything from directories above
            // the temporary folder.
            File.WriteAllText(Path.Combine(_temp.Path, "Directory.Build.props"), "<Project />\n");
            File.WriteAllText(Path.Combine(_temp.Path, "Directory.Build.targets"), "<Project />\n");
            File.WriteAllText(Path.Combine(_temp.Path, "Directory.Packages.props"), "<Project />\n");

            WriteNuGetConfig();
        }

        public static string PackageFeed => Metadata("AdbcPackageFeed");

        public static string PackageVersion => Metadata("AdbcPackageVersion");

        public static string RepoRoot => Metadata("AdbcRepoRoot");

        public string Name { get; }

        public string ProjectDirectory { get; }

        public string PackagesDirectory { get; }

        public string DriverCacheDirectory { get; }

        public string ProjectFile => Path.Combine(ProjectDirectory, Name + ".csproj");

        public string LockFile => Path.Combine(ProjectDirectory, "adbc.drivers.lock.json");

        public string OutputDirectory => Path.Combine(ProjectDirectory, "bin", "Debug", "net8.0");

        public string PublishDirectory => Path.Combine(OutputDirectory, "publish");

        /// <summary>Writes a console project referencing the package under test.</summary>
        public ConsumerProject WriteProject(
            string driverItems,
            string? extraProperties = null,
            string? extraPackageReferences = null,
            string? programBody = null)
        {
            string project =
$@"<Project Sdk=""Microsoft.NET.Sdk"">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <RootNamespace>Consumer</RootNamespace>
    <AssemblyName>{Name}</AssemblyName>
{extraProperties ?? string.Empty}  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""Adbc.Drivers.Build"" Version=""{PackageVersion}"" PrivateAssets=""all"" />
{extraPackageReferences ?? string.Empty}  </ItemGroup>

  <ItemGroup>
{driverItems}  </ItemGroup>

</Project>
";
            File.WriteAllText(ProjectFile, project, new UTF8Encoding(false));

            File.WriteAllText(
                Path.Combine(ProjectDirectory, "Program.cs"),
                programBody
                    ?? "System.Console.WriteLine(System.IO.Path.Combine(System.AppContext.BaseDirectory, \"adbc\"));\n",
                new UTF8Encoding(false));

            return this;
        }

        /// <summary>
        /// Copies the publish output to an unrelated directory and runs it there, which is
        /// what deploying a service actually does to a published application.
        /// </summary>
        public (int ExitCode, string Output) PublishRelocateAndRun(string relocatedName)
        {
            string relocated = Path.Combine(_temp.Path, relocatedName);
            CopyDirectory(PublishDirectory, relocated);

            ProcessStartInfo startInfo = new ProcessStartInfo("dotnet", $"\"{Path.Combine(relocated, Name + ".dll")}\"")
            {
                // Deliberately not the application's own directory: a relative path
                // resolved against the working directory would pass here by accident.
                WorkingDirectory = _temp.Path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            StringBuilder output = new StringBuilder();
            using Process process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(milliseconds: 5 * 60 * 1000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The relocated application did not exit within 5 minutes.");
            }

            process.WaitForExit();
            return (process.ExitCode, output.ToString());
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, directory.Substring(source.Length + 1)));
            }

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(destination, file.Substring(source.Length + 1)), overwrite: true);
            }
        }

        public BuildResult Run(string arguments, params string[] extraProperties)
        {
            List<string> parts = new List<string>
            {
                arguments,
                "--nologo",
                "-v:normal",
                $"-p:AdbcDriverCachePath={Quote(DriverCacheDirectory)}",
                $"--packages {Quote(PackagesDirectory)}",
            };

            parts.AddRange(extraProperties);
            string commandLine = string.Join(" ", parts);

            ProcessStartInfo startInfo = new ProcessStartInfo("dotnet", commandLine)
            {
                WorkingDirectory = ProjectDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            // An isolated HTTP cache keeps a previously downloaded package version from
            // shadowing the one just packed.
            startInfo.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(_temp.Path, "http-cache");
            startInfo.Environment["NUGET_PACKAGES"] = PackagesDirectory;
            startInfo.Environment["ADBC_DRIVER_CACHE"] = DriverCacheDirectory;
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

            StringBuilder output = new StringBuilder();
            using Process process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(milliseconds: 15 * 60 * 1000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"`dotnet {commandLine}` did not finish within 15 minutes.");
            }

            process.WaitForExit();
            return new BuildResult(process.ExitCode, output.ToString(), "dotnet " + commandLine);
        }

        public string ReadOutputFile(params string[] relativeParts) =>
            File.ReadAllText(Path.Combine(OutputDirectory, Path.Combine(relativeParts)));

        public bool OutputFileExists(params string[] relativeParts) =>
            File.Exists(Path.Combine(OutputDirectory, Path.Combine(relativeParts)));

        public bool PublishFileExists(params string[] relativeParts) =>
            File.Exists(Path.Combine(PublishDirectory, Path.Combine(relativeParts)));

        public void Dispose() => _temp.Dispose();

        private void WriteNuGetConfig()
        {
            // clear removes any machine-wide or user-level sources, so the restore is
            // reproducible and, for the fixture tests, entirely offline.
            string config =
$@"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <packageSources>
    <clear />
    <add key=""adbc-local"" value=""{PackageFeed}"" />
    <add key=""nuget.org"" value=""https://api.nuget.org/v3/index.json"" />
  </packageSources>
</configuration>
";
            File.WriteAllText(Path.Combine(_temp.Path, "NuGet.config"), config, new UTF8Encoding(false));
        }

        private static string Quote(string value) => "\"" + value + "\"";

        private static string Metadata(string key)
        {
            foreach (AssemblyMetadataAttribute attribute in TestAssembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(attribute.Key, key, StringComparison.Ordinal))
                {
                    return attribute.Value ?? string.Empty;
                }
            }

            throw new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "The test assembly has no '{0}' metadata.", key));
        }
    }
}
