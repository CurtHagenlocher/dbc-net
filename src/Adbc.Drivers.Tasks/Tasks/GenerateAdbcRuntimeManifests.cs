using System;
using System.Collections.Generic;
using System.IO;
using Adbc.Drivers.Build.Acquisition;
using Adbc.Drivers.Build.Packaging;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Adbc.Drivers.Build.Tasks
{
    /// <summary>
    /// Writes ADBC runtime driver manifests for a specific deployment directory.
    /// </summary>
    /// <remarks>
    /// Separate from acquisition because a manifest's <c>Driver.shared</c> path must be
    /// absolute, and therefore cannot be known until the final output directory is. Build
    /// and publish each get their own generated manifest.
    /// </remarks>
    public sealed class GenerateAdbcRuntimeManifests : Microsoft.Build.Utilities.Task
    {
        /// <summary>Plan written by <see cref="AcquireAdbcDrivers"/>.</summary>
        [Required]
        public string DeploymentPlanFile { get; set; } = string.Empty;

        /// <summary>
        /// The deployed <c>adbc</c> directory the manifests should point into, for example
        /// <c>$(TargetDir)adbc</c>.
        /// </summary>
        [Required]
        public string DeploymentRoot { get; set; } = string.Empty;

        /// <summary>Where to write the manifests. Defaults to <see cref="DeploymentRoot"/>.</summary>
        public string? OutputDirectory { get; set; }

        /// <summary>
        /// Emit paths relative to the manifest rather than absolute ones. Off by default:
        /// ADBC driver managers reject relative shared-library paths unless configured to
        /// allow them.
        /// </summary>
        public bool UseRelativePaths { get; set; }

        [Output]
        public ITaskItem[] ManifestFiles { get; private set; } = Array.Empty<ITaskItem>();

        public override bool Execute()
        {
            try
            {
                string planPath = Path.GetFullPath(DeploymentPlanFile);
                if (!File.Exists(planPath))
                {
                    Log.LogError($"The ADBC deployment plan '{planPath}' does not exist. This usually means AcquireAdbcDrivers did not run.");
                    return false;
                }

                DeploymentPlan plan = DeploymentPlan.Load(planPath);
                string deploymentRoot = Path.GetFullPath(DeploymentRoot);
                string outputDirectory = string.IsNullOrWhiteSpace(OutputDirectory)
                    ? deploymentRoot
                    : Path.GetFullPath(OutputDirectory!);

                IReadOnlyList<string> written = RuntimeManifestGenerator.Generate(
                    plan,
                    deploymentRoot,
                    outputDirectory,
                    UseRelativePaths);

                List<ITaskItem> items = new List<ITaskItem>(written.Count);
                foreach (string path in written)
                {
                    TaskItem item = new TaskItem(path);
                    item.SetMetadata("AdbcRelativePath", Path.GetFileName(path));
                    items.Add(item);
                }

                ManifestFiles = items.ToArray();

                if (UseRelativePaths)
                {
                    Log.LogMessage(
                        MessageImportance.Normal,
                        "Generated ADBC driver manifests with relative shared-library paths. The ADBC driver manager must be configured to accept them.");
                }

                Log.LogMessage(MessageImportance.Normal, $"Generated {ManifestFiles.Length} ADBC driver manifest(s) in '{outputDirectory}'.");
                return !Log.HasLoggedErrors;
            }
            catch (Exception ex) when (ex is PackageManifestException or InvalidDataException or Text.JsonParseException)
            {
                Log.LogError(ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, showStackTrace: true);
                return false;
            }
        }
    }
}
