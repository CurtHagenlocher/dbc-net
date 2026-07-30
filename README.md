# dbc-net

Deploy [ADBC](https://arrow.apache.org/adbc/) database drivers into your .NET build and
publish output, reproducibly.

`Adbc.Drivers.Build` is a build-time-only NuGet package. You declare which drivers your
application needs; it fetches them from a
[Columnar-compatible](https://github.com/columnar-tech/dbc) driver registry, verifies
them, and lays them out next to your application together with the ADBC driver manifests
needed to load them.

```xml
<ItemGroup>
  <AdbcDriver Include="snowflake" Version="1.11.0" Rids="win-x64;linux-x64" />
</ItemGroup>
```

```text
bin/Debug/net8.0/adbc/
  snowflake.toml
  snowflake/1.11.0/win-x64/    libadbc_driver_snowflake.dll  + .sig, LICENSE, NOTICE, MANIFEST
  snowflake/1.11.0/linux-x64/  libadbc_driver_snowflake.so   + .sig, LICENSE, NOTICE, MANIFEST
```

---

## The one thing to understand first

Driver acquisition is split into **two operations that are deliberately not the same
operation**.

| | When it runs | What it reads | What it writes |
|---|---|---|---|
| **Resolve** | Only when you ask for it | Registry indexes, your version constraints | `adbc.drivers.lock.json` |
| **Build / Publish** | Every build | The lock file, a verified local cache | Your output directory |

Resolving picks exact versions and records their URLs and SHA-256 hashes in a lock file
that **you commit**. Building reads only that lock file. A build never contacts a
registry, never interprets a version range, and never rewrites the lock.

That means a change upstream — a republished archive, a new version, a moved URL —
cannot change what your build produces. It can only show up as a diff to a committed
file that someone reviews.

If you have used `package-lock.json`, `Cargo.lock`, or `packages.lock.json`, this is the
same idea.

---

## Getting started

### 1. Reference the package

```xml
<PackageReference Include="Adbc.Drivers.Build" Version="0.1.0" PrivateAssets="all" />
```

Reference it from the **executable** project that needs the drivers, not from a class
library. See [Why it does not flow to your consumers](#why-it-does-not-flow-to-your-consumers).

### 2. Declare the drivers you need

```xml
<ItemGroup>
  <AdbcDriver Include="snowflake" Version="1.11.0" Rids="win-x64;linux-x64" />
  <AdbcDriver Include="postgresql" Version="^1.11.0" />
</ItemGroup>
```

`Include` is the driver's registry id. Leaving `Rids` off means "whatever this project
targets" — see [Runtime identifiers](#runtime-identifiers).

### 3. Resolve once, and commit the result

```sh
dotnet build -t:ResolveAdbcDriverLock
```

This reads the registry, downloads and inspects the selected archives, and writes
`adbc.drivers.lock.json` next to your project. It prints the licence and publisher of
everything it selected. Review that, then commit the lock file.

```sh
git add adbc.drivers.lock.json
```

### 4. Build

```sh
dotnet build
```

By default this is **offline**. It uses drivers already in your local cache and fails if
something is missing. The resolve step in the previous stage populated that cache, so the
first build after a resolve just works.

On a fresh machine or in CI, either restore the cache directory or allow that build to
download:

```sh
dotnet build -p:AdbcDriverNetworkMode=Online
```

Either way, the bytes are checked against the hashes in the lock file before anything is
used.

---

## Loading the drivers at run time

This package puts the drivers on disk and writes the manifests. Pointing the ADBC driver
manager at them is one line in your application:

```csharp
Environment.SetEnvironmentVariable(
    "ADBC_DRIVER_PATH",
    Path.Combine(AppContext.BaseDirectory, "adbc"));
```

`ADBC_DRIVER_PATH` is checked before user and system locations, so this is enough for a
self-contained deployment.

### The absolute-path caveat — please read this one

The ADBC manifest format requires `Driver.shared` to point at the shared library, and
driver managers **reject relative paths by default** for security reasons. A manifest
therefore has to contain an absolute path, which cannot be known until the output
directory is known.

So manifests are generated **per destination**: once for your build output, and again
for your publish output.

The consequence: **a generated manifest is valid for the directory it was generated
into.** If you publish and then move or rename the folder, the paths inside
`<driver>.toml` no longer resolve. Publish again to the final location, or generate the
manifest at startup yourself.

If your driver manager is configured to accept relative paths, opt out:

```xml
<PropertyGroup>
  <AdbcDriverRelativeManifestPaths>true</AdbcDriverRelativeManifestPaths>
</PropertyGroup>
```

---

## Configuring

### `AdbcDriver` item metadata

| Metadata | Default | Meaning |
|---|---|---|
| `Version` | required | Exact version or constraint. Only the resolve step reads it. |
| `Rids` | the project's RID | Semicolon-separated portable RIDs to acquire. |
| `ManifestName` | the driver id | Base name of the generated `.toml`. Must be unique across items. |
| `Entrypoint` | from the archive | Driver init symbol. Omitted from the manifest when unknown. |
| `AdbcVersion` | from the archive | ADBC API version recorded in the manifest. |
| `Prerelease` | project default | `allow` or `deny`, overriding `AdbcDriverAllowPrerelease` for this driver only. |
| `PlatformOverride` | none | `rid=platform` pairs, for a registry publishing a tuple that is not in the built-in table. |
| `CopyToBuildOutput` | `true` | Deploy into `$(TargetDir)`. |
| `CopyToPublishDirectory` | `true` | Include in publish output. |

Version constraints accept an exact version (`1.11.0`), a wildcard (`*`), comparisons
(`>=1.10.0 <2.0.0`), caret (`^1.11.0`), and tilde (`~1.11.0`).

Prereleases are excluded unless the constraint itself names one, or you opt in. Some
drivers publish nothing else, so this is worth knowing about:

```xml
<AdbcDriver Include="clickhouse" Version="*" Prerelease="allow" Rids="linux-x64" />
```

`Prerelease` on the item beats the project-wide `AdbcDriverAllowPrerelease`, in either
direction, so one driver can track prereleases without opting everything else in.

### Properties

| Property | Default | Meaning |
|---|---|---|
| `AdbcDriverLockFile` | `adbc.drivers.lock.json` beside the project | The committed lock file. |
| `AdbcDriverNetworkMode` | `CacheOnly` | See [Network modes](#network-modes). |
| `AdbcDriverCachePath` | `$(UserProfile)/.adbc/driver-cache` | Cache root. Also settable with `ADBC_DRIVER_CACHE`. |
| `AdbcDriverOutputSubdirectory` | `adbc` | Output subdirectory receiving drivers and manifests. |
| `AdbcDriverDeployOnBuild` | `true` | Copy into the build output. |
| `AdbcDriverDeployOnPublish` | `true` | Include in publish output. |
| `AdbcDriverGenerateRuntimeManifests` | `true` | Write `<name>.toml` driver manifests. |
| `AdbcDriverRelativeManifestPaths` | `false` | Emit relative `Driver.shared` paths. |
| `AdbcDriverRegistries` | the public registry | Registry base URLs, highest precedence first. Resolve step only. |
| `AdbcDriverAllowPrerelease` | `false` | Let prereleases satisfy a constraint. Resolve step only. |
| `AdbcDriverVerifyFileHashes` | `false` | Re-hash every cached file on every build. Correct but slow. |
| `AdbcDriverMaxExpandedBytes` | 1 GiB | Reject an archive that expands beyond this. |
| `AdbcDriverMaxArchiveEntries` | 1024 | Reject an archive with more entries than this. |
| `AdbcDriverNetworkTimeoutSeconds` | 300 | Per-request timeout. |
| `AdbcDriverCacheLockTimeoutSeconds` | 600 | How long to wait for another build using the same cache entry. |

### Runtime identifiers

Projects speak NuGet RIDs; ADBC registries speak their own platform tuples. The mapping
is explicit:

| NuGet RID | ADBC platform |
|---|---|
| `win-x64` | `windows_amd64` |
| `win-arm64` | `windows_arm64` |
| `linux-x64` | `linux_amd64` |
| `linux-arm64` | `linux_arm64` |
| `linux-musl-x64` | `linux_amd64_musl` |
| `linux-musl-arm64` | `linux_arm64_musl` |
| `osx-x64` | `macos_amd64` |
| `osx-arm64` | `macos_arm64` |

Version- and distribution-qualified RIDs collapse onto their portable equivalent, so
`win10-x64`, `ubuntu.22.04-x64`, and `alpine.3.19-x64` all work.

When an item has no `Rids`, the value is taken from `$(AdbcDriverRuntimeIdentifiers)`,
then `$(RuntimeIdentifier)`, then `$(RuntimeIdentifiers)`, then the SDK's RID for the
machine doing the build. Requesting several RIDs is normal and gives each one its own
directory, with a single manifest mapping every platform.

### Network modes

| Mode | Behaviour |
|---|---|
| `CacheOnly` | **Default.** Never touches the network. A locked artifact missing from the cache is an error. |
| `Online` | Uses the cache, otherwise downloads exactly the URL in the lock and checks it against the locked hash. |
| `ReadOnly` | May download and verify, but never writes to the cache. For shared or immutable cache directories. |

There is no mode that lets an ordinary build re-resolve. Setting `RefreshLock` fails with
a pointer to the resolve target.

---

## Continuous integration

`CacheOnly` is the default precisely because it is the right CI behaviour: a build that
cannot reach the network is a build that provably used reviewed bytes.

Cache the content-addressed directory between runs, keyed by the lock file:

```yaml
- uses: actions/cache@v4
  with:
    path: ~/.adbc/driver-cache
    key: adbc-${{ hashFiles('**/adbc.drivers.lock.json') }}
```

The cache is immutable and content-addressed, so entries never need invalidating — a
different driver is simply a different key. If you prefer, the acquire step also exposes
`$(AdbcDriverLockFileDigest)` for use as a cache key.

On a cold cache, allow one download with `-p:AdbcDriverNetworkMode=Online`.

Nothing here participates in NuGet restore, so `dotnet restore --locked-mode` does not
cover drivers. The driver lock file is the equivalent guarantee.

---

## What you get, and what you do not

**Verified before use.** An archive is checked against the lock's SHA-256 *before* it is
extracted. A mismatch fails the build and the bytes are discarded.

**Hardened extraction.** Absolute paths, `..` components, symbolic and hard links,
duplicate entries, device and FIFO entries, reserved device names, and archives that
exceed the configured entry-count and size limits are all rejected.

**A cache that cannot be half-written.** Entries are content-addressed and immutable.
Each is staged in a private directory, promoted with an atomic move, and marked complete
by a receipt written last — so a build interrupted mid-download leaves nothing that a
later build mistakes for valid. A per-entry lock makes this safe across parallel projects
and concurrent `dotnet build` processes.

**Licences travel with the drivers.** Each driver's `LICENSE` and `NOTICE` are deployed
alongside it, and the resolve step prints the declared licence of everything it selects.

**No identifiers are sent.** Requests carry an ordinary product `User-Agent` and nothing
else — no machine id, no user id, no telemetry.

### What is not guaranteed

A hash recorded by the resolve step gives you **reproducibility**: every later build gets
the same bytes the lock was reviewed against. It does **not** independently authenticate
that first download, because the hash was learned from that download. HTTPS protects the
transport; it does not prove the archive is what the publisher intended.

OpenPGP signature verification against a pinned key is designed for but **not yet
implemented**. Cache receipts record `NotAttempted` rather than implying a check that did
not happen.

If that gap matters for your threat model, review the lock file diff when versions change
— that is what it is for — and treat the first resolve of a new driver as the trust
decision it is.

### Licensing

The drivers this package downloads are **not** covered by its licence. They are
third-party software with their own terms — Apache-2.0, MIT, and proprietary licences all
appear in the public registry.

Deploying a driver alongside your application is not the same as redistributing it inside
your own NuGet package, and this package will not do the latter for you: driver
deployment is suppressed during `dotnet pack`. If you have established that you have
redistribution rights, `AdbcDriverIncludeInPackage` opts in.

### Why it does not flow to your consumers

The package is marked as a development dependency and has no `buildTransitive/`
directory. A class library that references it will **not** cause applications that
reference the library to download or deploy native drivers.

That is deliberate. Silently pulling a 60 MB native database driver into someone else's
application — with its own licence terms — is not a decision a transitive dependency
should be making. Reference the package from the executable that needs the drivers.

---

## If you already use the `dbc` CLI

`dbc` has its own driver list (`dbc.toml`) and lock file (`dbc.lock`). They are **not
interchangeable with the files here**, and the two can coexist without interfering:
`dbc` installs drivers into machine or user locations, while this package deploys them
next to one application. Different scope, different files.

If you keep both, expect to declare your drivers twice. That is the current cost of
using both tools, and the reason is structural rather than stylistic:

- `dbc.lock` records **one platform per driver** — its loader keys entries by driver name
  alone — because it describes what was installed on *this* machine. An application that
  ships for `win-x64` and `linux-x64` cannot be expressed in it.
- `dbc.lock` records a version but **no URL**, so it pins a version rather than specific
  bytes.
- Its `checksum` is of the **installed shared library**, computed after extraction. The
  `archiveSha256` here exists so that verification can happen *before* anything is
  unpacked.

Every field in `dbc.lock` has an equivalent in `adbc.drivers.lock.json` — `name`→`id`,
`version`→`version`, `platform`→`adbcPlatform`, `checksum`→`driverSha256` — so nothing is
lost by using this one; the extra fields are what make offline, pre-extraction
verification possible.

## Troubleshooting

**`... is not in the driver cache at ...`**
A `CacheOnly` build found nothing cached. Restore your cache directory, or build once
with `-p:AdbcDriverNetworkMode=Online`. The message includes the expected hash, the
source URL, and the exact cache path it looked in.

**`Driver '...' is referenced by the project but is not in the driver lock file`**
You added or renamed an `AdbcDriver` item without re-resolving. Run
`dotnet build -t:ResolveAdbcDriverLock` and commit the result.

**`The driver lock file '...' does not exist`**
The first resolve has not happened yet. Builds never create it implicitly.

**`The driver lock file has no '<rid>' artifact for driver '...'`**
You added a RID to `Rids` after resolving. Re-resolve.

**`The runtime identifier '...' does not map to an ADBC platform`**
The RID is outside the table above — `win-x86`, for instance. If the registry publishes a
tuple for it, map it with `PlatformOverride`.

**`... has SHA-256 ..., but the lock file requires ... Refusing to use it`**
The bytes at that URL are not the bytes your lock file was reviewed against. This is the
check doing its job. Do not "fix" it by re-resolving until you know why the archive
changed.

**Nothing is deployed and there is no error.**
Deployment is skipped during design-time builds (so the IDE never blocks on a download),
during the outer build of a multi-targeted project (which has no single output directory),
and during `pack`. Inner builds deploy normally, once per target framework.

---

## Contributing

```text
src/Adbc.Drivers.Tasks     MSBuild tasks and all acquisition logic (netstandard2.0 + net472)
src/Adbc.Drivers.Build     The shipped package: build/ targets + tools/ assemblies
test/Adbc.Drivers.Tasks.Tests             Unit tests
test/Adbc.Drivers.Build.IntegrationTests  Drives real `dotnet build` runs against the packed package
samples/SnowflakeSample                   Consumes the real Snowflake driver
```

```sh
dotnet build dbc-net.slnx
dotnet test dbc-net.slnx
dotnet pack src/Adbc.Drivers.Build/Adbc.Drivers.Build.csproj
```

The integration tests pack the product themselves, so `dotnet test` is self-sufficient.
They build throwaway projects against a local `file://` fixture registry using an
isolated NuGet folder and driver cache, so they touch neither the network nor your global
caches. Tests that use the real public registry are opt-in:

```sh
ADBC_DRIVERS_TESTS_NETWORK=1 dotnet test
```

Those are the only tests that confirm the live registry index and a real driver archive
still parse, which is worth a scheduled run even though they cannot gate every commit.

The sample is not in the solution, because it consumes the package from
`artifacts/package`:

```sh
dotnet pack src/Adbc.Drivers.Build/Adbc.Drivers.Build.csproj
dotnet build samples/SnowflakeSample -p:AdbcDriverNetworkMode=Online
dotnet run --project samples/SnowflakeSample --no-build
```

### Two decisions worth knowing before you change things

**The task assembly has no external dependencies at all.** It loads into a long-lived
MSBuild process shared with every other task, where a third-party assembly is a
well-known source of version conflicts. That is why the YAML, TOML, JSON, tar, and semver
readers under `src/Adbc.Drivers.Tasks/{Text,Archives,Model}` are hand-written. They
implement narrow subsets and report anything outside them as an error rather than
guessing, because mis-parsing a registry index would change which bytes get downloaded.
`Fixtures/public-index-snapshot.yaml` is a committed copy of the live index, parsed in
full by a test, and is the guard that matters most.

**RIDs map to ADBC platforms through an explicit table.** A custom `runtime.json` RID
graph would be fragile on .NET 8 and later, which deliberately use a smaller portable
graph, and would still not describe ADBC's separate tuple scheme.

---

## Status

Working and tested, at an early version. Resolution against public registries, the lock
file, the content-addressed cache, and MSBuild build/publish integration are all
implemented and covered by tests, including against the real public registry.

Not yet implemented:

- OpenPGP signature verification and licence allow/deny policy
- Private registries and their credential contract
- Verification against the Apache ADBC .NET driver manager, which may yet change the
  runtime manifest strategy

## Licence

Apache-2.0. This licence covers this repository only, not the drivers it downloads.
