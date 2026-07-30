using System;
using System.Collections.Generic;
using System.IO;
using Adbc.Drivers.Build.Acquisition;
using Adbc.Drivers.Build.Caching;
using Adbc.Drivers.Build.Locking;
using Adbc.Drivers.Build.Registry;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Adbc.Drivers.Build.Tasks
{
    /// <summary>
    /// Materializes the drivers named in the lock file into the project's intermediate
    /// directory.
    /// </summary>
    /// <remarks>
    /// Runs during ordinary builds. It reads only the lock file: it never contacts a
    /// registry, interprets a version range, or rewrites the lock, so the bytes a build
    /// consumes cannot change without a reviewed change to the committed lock.
    /// </remarks>
    public sealed class AcquireAdbcDrivers : AdbcTaskBase
    {
        /// <summary>The project's <c>AdbcDriver</c> items.</summary>
        [Required]
        public ITaskItem[] Drivers { get; set; } = Array.Empty<ITaskItem>();

        /// <summary>Path to the committed driver lock file.</summary>
        [Required]
        public string LockFile { get; set; } = string.Empty;

        /// <summary>Directory the verified files are staged into, normally under <c>obj</c>.</summary>
        [Required]
        public string DestinationRoot { get; set; } = string.Empty;

        /// <summary>Runtime identifiers to use for items without <c>Rids</c> metadata.</summary>
        public string? DefaultRuntimeIdentifiers { get; set; }

        /// <summary>One of CacheOnly, Online, ReadOnly.</summary>
        public string? NetworkMode { get; set; }

        /// <summary>
        /// Re-hash every cached file on every build rather than trusting the immutable
        /// cache receipt. Correct but slow.
        /// </summary>
        public bool VerifyFileHashes { get; set; }

        /// <summary>Files staged for deployment, with <c>AdbcRelativePath</c> metadata.</summary>
        [Output]
        public ITaskItem[] DeploymentFiles { get; private set; } = Array.Empty<ITaskItem>();

        /// <summary>Path of the deployment plan consumed by GenerateAdbcRuntimeManifests.</summary>
        [Output]
        public string DeploymentPlanFile { get; private set; } = string.Empty;

        /// <summary>Digest of the lock file, usable as a CI cache key.</summary>
        [Output]
        public string LockFileDigest { get; private set; } = string.Empty;

        protected override void Run()
        {
            IReadOnlyList<DriverRequest> requests = ParseRequests(Drivers, DefaultRuntimeIdentifiers);
            if (requests.Count == 0)
            {
                Log.LogMessage(MessageImportance.Low, "No AdbcDriver items were supplied; nothing to acquire.");
                return;
            }

            if (!NetworkModeParser.TryParse(NetworkMode, out NetworkMode mode))
            {
                Log.LogError(
                    $"AdbcDriverNetworkMode '{NetworkMode}' is not recognized. Use one of: {NetworkModeParser.Describe()}.");
                return;
            }

            string lockPath = Path.GetFullPath(LockFile);
            if (!File.Exists(lockPath))
            {
                Log.LogError(
                    $"The driver lock file '{lockPath}' does not exist. Run the 'ResolveAdbcDriverLock' target to create it, review it, and commit it.");
                return;
            }

            DriverLock driverLock = DriverLock.Load(lockPath);
            LockFileDigest = driverLock.ComputeDigest();

            AcquisitionOptions options = new AcquisitionOptions
            {
                Mode = mode,
                Limits = CreateLimits(),
                LockTimeout = CreateLockTimeout(),
                DestinationRoot = Path.GetFullPath(DestinationRoot),
                VerifyFileHashes = VerifyFileHashes,
            };

            using (DefaultRegistryTransport transport = CreateTransport())
            {
                DriverAcquirer acquirer = new DriverAcquirer(
                    CreateCache(),
                    transport,
                    CreateSignatureVerifier(),
                    message => Log.LogMessage(MessageImportance.Normal, message));

                AcquisitionResult result = acquirer.Acquire(driverLock, requests, options);

                DeploymentPlanFile = Path.Combine(options.DestinationRoot, "deployment-plan.json");
                result.Plan.Save(DeploymentPlanFile);

                List<ITaskItem> items = new List<ITaskItem>(result.Files.Count);
                foreach (DeployedFile file in result.Files)
                {
                    TaskItem item = new TaskItem(file.SourcePath);

                    // The relative path is carried explicitly because MSBuild's
                    // RecursiveDir is only populated for wildcard-expanded items.
                    item.SetMetadata("AdbcRelativePath", file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    item.SetMetadata("AdbcDriverId", file.DriverId);
                    item.SetMetadata("AdbcRid", file.Rid);
                    item.SetMetadata("AdbcCopyToBuildOutput", file.CopyToBuildOutput ? "true" : "false");
                    item.SetMetadata("AdbcCopyToPublishDirectory", file.CopyToPublishDirectory ? "true" : "false");
                    items.Add(item);
                }

                DeploymentFiles = items.ToArray();

                Log.LogMessage(
                    MessageImportance.Normal,
                    $"Staged {DeploymentFiles.Length} file(s) for {result.Plan.Drivers.Count} ADBC driver(s) in '{options.DestinationRoot}'.");

                ReportLicenses(result);
            }
        }

        /// <summary>
        /// Driver licences are not implied by this package's own licence, so what is
        /// about to be copied into the output is stated in the build log.
        /// </summary>
        private void ReportLicenses(AcquisitionResult result)
        {
            foreach (DeployedDriver driver in result.Plan.Drivers)
            {
                Log.LogMessage(
                    MessageImportance.Normal,
                    $"  {driver.Id} {driver.Version} ({driver.License ?? "license not declared"}) for {string.Join(", ", RidsOf(driver))}");
            }
        }

        private static IEnumerable<string> RidsOf(DeployedDriver driver)
        {
            foreach (DeployedArtifact artifact in driver.Artifacts)
            {
                yield return artifact.Rid;
            }
        }
    }
}
