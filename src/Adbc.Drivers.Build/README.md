# Adbc.Drivers.Build

Build-time MSBuild integration that deploys [ADBC](https://arrow.apache.org/adbc/)
drivers into a .NET project's build and publish output.

This is the reference summary. Full documentation — including runtime loading, CI setup,
and troubleshooting — is at
[github.com/CurtHagenlocher/dbc-net](https://github.com/CurtHagenlocher/dbc-net).

The workflow is split in two, deliberately:

1. An explicit **resolve** step reads Columnar-compatible driver registries, selects
   exact versions and platforms, downloads and inspects the archives, and writes a
   **committed lock file** of immutable URLs and SHA-256 hashes.
2. Ordinary **build** and **publish** read only that lock file, obtain the exact
   archives from a verified content-addressed cache, and copy the drivers, licences,
   notices, and a generated runtime manifest into the output.

A build never resolves a floating version and never rewrites the lock, so a mutable
upstream index cannot change what your build produces without a reviewable diff.

## Install

```xml
<PackageReference Include="Adbc.Drivers.Build" Version="0.1.0" PrivateAssets="all" />
```

The package is marked as a development dependency and has no `buildTransitive/`
directory: a class library that references it will not cause downstream applications
to download or redistribute native drivers. Reference it from the executable project
that needs the drivers.

## Configure

```xml
<ItemGroup>
  <AdbcDriver Include="snowflake" Version="1.11.0" Rids="win-x64;linux-x64" />
</ItemGroup>
```

Then resolve once and commit the result:

```sh
dotnet build -t:ResolveAdbcDriverLock
git add adbc.drivers.lock.json
```

### `AdbcDriver` metadata

| Metadata | Meaning |
|---|---|
| `Version` | Exact version or constraint (`1.11.0`, `^1.11.0`, `>=1.10 <2.0`). Only the resolve step interprets it. |
| `Rids` | Portable RIDs to acquire. Defaults to `$(RuntimeIdentifier)`, then `$(RuntimeIdentifiers)`, then the host RID. |
| `ManifestName` | Base name of the generated `.toml` manifest. Defaults to the driver id. |
| `Entrypoint` | Driver init symbol. Defaults to whatever the archive declares; omitted when unknown. |
| `AdbcVersion` | ADBC API version recorded in the manifest. Defaults to the archive's value. |
| `PlatformOverride` | `rid=platform` pairs, for a registry publishing a tuple the built-in table does not map. |
| `CopyToBuildOutput` | Deploy to `$(TargetDir)`. Default `true`. |
| `CopyToPublishDirectory` | Deploy on publish. Default `true`. |

### Properties

| Property | Default | Meaning |
|---|---|---|
| `AdbcDriverLockFile` | `$(MSBuildProjectDirectory)\adbc.drivers.lock.json` | The committed lock file. |
| `AdbcDriverNetworkMode` | `CacheOnly` | `CacheOnly`, `Online`, or `ReadOnly`. |
| `AdbcDriverCachePath` | `$(UserProfile)/.adbc/driver-cache` | Content-addressed cache root. Also `ADBC_DRIVER_CACHE`. |
| `AdbcDriverOutputSubdirectory` | `adbc` | Output subdirectory receiving drivers and manifests. |
| `AdbcDriverDeployOnBuild` | `true` | Copy into `$(TargetDir)`. |
| `AdbcDriverDeployOnPublish` | `true` | Include in publish output. |
| `AdbcDriverGenerateRuntimeManifests` | `true` | Write `<name>.toml` driver manifests. |
| `AdbcDriverRelativeManifestPaths` | `false` | Emit relative `Driver.shared` paths (see below). |
| `AdbcDriverRegistries` | `https://dbc-cdn.columnar.tech/` | Registries, highest precedence first. Resolve step only. |
| `AdbcDriverVerifyFileHashes` | `false` | Re-hash every cached file on every build. |

### Network modes

| Mode | Behaviour |
|---|---|
| `CacheOnly` | Never reaches the network. A locked artifact missing from the cache is an error. Default, and the right choice for CI. |
| `Online` | Uses the cache, otherwise downloads exactly the locked URL and verifies it against the locked hash. |
| `ReadOnly` | May download and verify, but never writes to the cache. |

`RefreshLock` is rejected during `Build` and `Publish`; use the `ResolveAdbcDriverLock`
target.

## Output layout

```text
$(TargetDir)adbc/
  snowflake.toml
  snowflake/1.11.0/win-x64/
    libadbc_driver_snowflake.dll
    libadbc_driver_snowflake.dll.sig
    LICENSE
    NOTICE
    MANIFEST
```

Point the ADBC driver manager at the `adbc` directory, for example by setting
`ADBC_DRIVER_PATH` to `Path.Combine(AppContext.BaseDirectory, "adbc")`.

## Runtime manifests and absolute paths

The ADBC manifest specification requires `Driver.shared`, and driver managers reject
relative shared-library paths by default. Manifests are therefore generated **per
destination**, with absolute paths, at the point the destination is known — once for
`$(TargetDir)` and again for `$(PublishDir)`.

The consequence is that a generated manifest is valid for the directory it was written
into. Moving a published folder to a different path invalidates it; regenerate by
publishing again, or set `AdbcDriverRelativeManifestPaths` to `true` if your driver
manager is configured to accept relative paths.

## Integrity

- Archives are verified against the SHA-256 in the lock file **before** extraction.
- Extraction rejects absolute paths, `..`, links, duplicate entries, unexpected entry
  types, reserved names, and archives that exceed the configured entry and size limits.
- The cache is content-addressed and immutable; entries are promoted atomically and a
  receipt is written last, so a partial entry is never observed as valid.
- Every driver's `LICENSE` and `NOTICE` are preserved and deployed alongside it. This
  package's own licence grants no rights to any driver it downloads.

A hash recorded by the resolve step gives **reproducibility** — later builds get the
bytes the lock was reviewed against. It does not independently authenticate that first
download, because the hash was learned from it. OpenPGP signature verification against a
pinned key is not yet implemented; cache receipts record `NotAttempted` rather than
implying a check that did not happen.

No persistent machine or user identifiers are sent with any request.

## Licence

Apache-2.0.
