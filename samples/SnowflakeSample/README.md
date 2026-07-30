# SnowflakeSample

Consumes the real Apache Snowflake ADBC driver from the public Columnar registry, driven
by the committed [`adbc.drivers.lock.json`](adbc.drivers.lock.json).

The lock file pins driver `snowflake` 1.11.0 for `win-x64` and `linux-x64`, with the
exact archive URLs and SHA-256 hashes for both the archives and the shared libraries. It
was produced by running the resolve target and committed after review, which is the
intended workflow.

## Running it

The sample references `Adbc.Drivers.Build` as a NuGet package, so pack it first:

```sh
dotnet pack src/Adbc.Drivers.Build/Adbc.Drivers.Build.csproj
```

Then, from the repository root:

```sh
# Online, because the driver cache starts empty. Downloads about 33 MB.
dotnet build samples/SnowflakeSample -p:AdbcDriverNetworkMode=Online

# Every later build is offline: CacheOnly is the default.
dotnet build samples/SnowflakeSample

dotnet run --project samples/SnowflakeSample --no-build
```

The program prints the generated manifest and the deployed files. It does not open a
Snowflake connection — that would need credentials and a live account, and the point here
is the build integration. What it does show is the one thing a real application has to
get right: pointing the ADBC driver manager at the deployed manifests via
`ADBC_DRIVER_PATH`.

## Updating the pinned version

```sh
dotnet build samples/SnowflakeSample -t:ResolveAdbcDriverLock
git diff samples/SnowflakeSample/adbc.drivers.lock.json
```

Review the hashes and the declared licence before committing. A hash learned from a
download proves later builds get the same bytes; it does not independently authenticate
that first download.

## Note on the deployed layout

Both RIDs are requested, so the generated `snowflake.toml` carries a `Driver.shared`
platform map rather than a single path, and each RID gets its own directory — otherwise
the two packages' `MANIFEST`, `LICENSE`, and `NOTICE` files would collide.

The Apache `LICENSE` and `NOTICE` shipped in each driver archive are deployed alongside
the driver. This repository's licence grants no rights to the driver itself.
