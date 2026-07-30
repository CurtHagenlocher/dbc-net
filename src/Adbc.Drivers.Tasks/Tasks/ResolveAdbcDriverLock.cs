using System;
using System.Collections.Generic;
using System.IO;
using Adbc.Drivers.Build.Acquisition;
using Adbc.Drivers.Build.Locking;
using Adbc.Drivers.Build.Registry;
using Microsoft.Build.Framework;

namespace Adbc.Drivers.Build.Tasks
{
    /// <summary>
    /// Resolves <c>AdbcDriver</c> items against the configured registries and writes the
    /// driver lock file.
    /// </summary>
    /// <remarks>
    /// Deliberately not part of <c>Build</c> or <c>Publish</c>. This is the only task
    /// that reads a registry index, applies a version constraint, or changes which bytes
    /// later builds will use, and running it is meant to be an explicit act whose output
    /// is reviewed and committed.
    /// </remarks>
    public sealed class ResolveAdbcDriverLock : AdbcTaskBase
    {
        [Required]
        public ITaskItem[] Drivers { get; set; } = Array.Empty<ITaskItem>();

        [Required]
        public string LockFile { get; set; } = string.Empty;

        /// <summary>
        /// Registry base URLs, highest precedence first. Defaults to the public
        /// Columnar registry.
        /// </summary>
        public string? Registries { get; set; }

        public string? DefaultRuntimeIdentifiers { get; set; }

        /// <summary>Allow prerelease driver versions to satisfy a constraint.</summary>
        public bool AllowPrerelease { get; set; }

        /// <summary>True when the lock file's content changed.</summary>
        [Output]
        public bool LockFileChanged { get; private set; }

        protected override void Run()
        {
            IReadOnlyList<DriverRequest> requests = ParseRequests(Drivers, DefaultRuntimeIdentifiers);
            if (requests.Count == 0)
            {
                Log.LogWarning("No AdbcDriver items were supplied, so the driver lock file was not written.");
                return;
            }

            List<Uri> registries = new List<Uri>();
            foreach (string registry in SplitList(Registries))
            {
                if (!Uri.TryCreate(registry, UriKind.Absolute, out Uri? uri))
                {
                    Log.LogError($"AdbcDriverRegistries contains '{registry}', which is not an absolute URL.");
                    return;
                }

                registries.Add(uri!);
            }

            ResolutionOptions options = new ResolutionOptions
            {
                Registries = registries,
                AllowPrerelease = AllowPrerelease,
                Limits = CreateLimits(),
                LockTimeout = CreateLockTimeout(),
            };

            string lockPath = Path.GetFullPath(LockFile);

            using (DefaultRegistryTransport transport = CreateTransport())
            {
                DriverResolver resolver = new DriverResolver(
                    transport,
                    CreateCache(),
                    CreateSignatureVerifier(),
                    message => Log.LogMessage(MessageImportance.High, message),
                    message => Log.LogWarning(message));

                DriverLock resolved = resolver.Resolve(requests, options);
                LockFileChanged = resolved.Save(lockPath);

                if (LockFileChanged)
                {
                    Log.LogMessage(MessageImportance.High, $"Wrote '{lockPath}'.");
                    ReportLicenses(resolved);
                    Log.LogMessage(
                        MessageImportance.High,
                        "Review the hashes and licenses above before committing. A hash learned from a download proves later builds get the same bytes; "
                        + "it does not independently authenticate that first download.");
                }
                else
                {
                    Log.LogMessage(MessageImportance.High, $"'{lockPath}' is already up to date.");
                }
            }
        }

        private void ReportLicenses(DriverLock resolved)
        {
            foreach (LockedDriver driver in resolved.Drivers)
            {
                Log.LogMessage(
                    MessageImportance.High,
                    $"  {driver.Id} {driver.Version} — license: {driver.License ?? "not declared"}, publisher: {driver.Publisher ?? "not declared"}");
            }
        }
    }
}
