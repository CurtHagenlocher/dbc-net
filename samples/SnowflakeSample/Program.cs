using System;
using System.IO;

namespace SnowflakeSample;

/// <summary>
/// Reports what Adbc.Drivers.Build deployed next to this application.
/// </summary>
/// <remarks>
/// The sample deliberately does not open a Snowflake connection: that would need
/// credentials and a live account, and the point here is the build integration. What it
/// does show is the one thing a real application has to get right — telling the ADBC
/// driver manager where the deployed manifests are.
/// </remarks>
internal static class Program
{
    private static int Main()
    {
        string driverRoot = Path.Combine(AppContext.BaseDirectory, "adbc");

        if (!Directory.Exists(driverRoot))
        {
            Console.Error.WriteLine($"No drivers were deployed to '{driverRoot}'.");
            Console.Error.WriteLine("Build with -p:AdbcDriverNetworkMode=Online to populate the driver cache.");
            return 1;
        }

        Console.WriteLine($"ADBC driver root: {driverRoot}");
        Console.WriteLine();

        // An ADBC driver manager discovers manifests through ADBC_DRIVER_PATH before it
        // looks in user or system locations, so this is all a deployed application needs.
        Console.WriteLine("To let the ADBC driver manager find these drivers, set:");
        Console.WriteLine($"  ADBC_DRIVER_PATH={driverRoot}");
        Console.WriteLine();

        foreach (string manifest in Directory.GetFiles(driverRoot, "*.toml"))
        {
            Console.WriteLine($"Manifest: {Path.GetFileName(manifest)}");
            foreach (string line in File.ReadAllLines(manifest))
            {
                if (line.Length > 0 && line[0] != '#')
                {
                    Console.WriteLine("  " + line);
                }
            }

            Console.WriteLine();
        }

        Console.WriteLine("Deployed files:");
        foreach (string file in Directory.GetFiles(driverRoot, "*", SearchOption.AllDirectories))
        {
            FileInfo info = new FileInfo(file);
            Console.WriteLine($"  {Path.GetRelativePath(driverRoot, file),-70} {info.Length,12:N0} bytes");
        }

        return 0;
    }
}
