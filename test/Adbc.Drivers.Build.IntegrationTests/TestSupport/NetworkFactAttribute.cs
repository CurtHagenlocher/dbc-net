using System;
using Xunit;

namespace Adbc.Drivers.Build.IntegrationTests.TestSupport
{
    /// <summary>
    /// A test that reaches a real third-party registry over the network, and is skipped
    /// unless <c>ADBC_DRIVERS_TESTS_NETWORK=1</c>.
    /// </summary>
    /// <remarks>
    /// Kept opt-in so the default suite is deterministic and offline: everything else
    /// runs against a local <c>file://</c> fixture registry. These tests are the ones
    /// that confirm the real index schema and a real driver archive still parse, so they
    /// are worth running in a scheduled job even though they cannot gate every commit.
    /// </remarks>
    public sealed class NetworkFactAttribute : FactAttribute
    {
        public const string EnvironmentVariable = "ADBC_DRIVERS_TESTS_NETWORK";

        public NetworkFactAttribute()
        {
            if (!IsEnabled)
            {
                Skip = $"Set {EnvironmentVariable}=1 to run tests that download from the public driver registry.";
            }
        }

        public static bool IsEnabled =>
            string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);
    }

    /// <summary>
    /// Serializes the tests that spawn MSBuild, so that several concurrent
    /// <c>dotnet build</c> processes do not contend for the same package feed.
    /// </summary>
    [CollectionDefinition("msbuild", DisableParallelization = true)]
    public sealed class MsBuildCollection
    {
    }
}
