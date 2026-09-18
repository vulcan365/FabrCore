# Build and release

Run from PowerShell 7 with Git and the SDK specified by `global.json` (10.0.302 with
patch roll-forward) installed. Scripts resolve repository paths
relative to themselves, so they also work when invoked from another directory.

`Projects.psd1` is the shared package and test-project inventory used by local packaging,
builds, publish previews and GitHub Actions. SQL Server, Memory, GraphRAG and service
contracts are now integrated into Host/Core/SDK; their former standalone packages are
not published. `FabrCore.Host.Testing` and the optional `FabrCore.Scripting` extension are included in the configured package inventory.

```powershell
./scripts/Build.ps1                          # Build, test, and pack to C:/repos/nuget on Windows
./scripts/Push-NuGet.ps1                     # Push the highest version in that feed
./scripts/Build.ps1 -Version 2.0.0-blah       # Build, test, and pack this exact version
./scripts/Push-NuGet.ps1 -Version 2.0.0-blah  # Push this exact version
./scripts/Build.ps1 -DryRun -Pack            # Validate paths and show commands only
./scripts/Test-BuildScripts.ps1             # Offline inventory and release-script checks
./scripts/Pack-Local.ps1 -LocalFeed C:/repos/nuget
./scripts/Release-Patch.ps1 -DryRun          # Also Minor, Major, Develop
./scripts/Push-NuGet.ps1 -LocalFeed C:/repos/nuget -DryRun
./scripts/Test-SqlMode.ps1 -Container sql2025
```

`Build.ps1` runs VSTest projects with `dotnet test` and the two MSTest SDK projects with
`dotnet run`. Integration, Evaluation, SqlMode and SqlIntegration categories are excluded from this
offline build. TRX reports are written under `artifacts/test-results/offline`. SQL mode tests are explicit and use the existing Docker container;
they create and clean up isolated test databases. Memory evaluation scripts are also
explicit, require configured model/database credentials and can incur provider charges.

Both scripts default to `C:/repos/nuget` on Windows, or `artifacts/packages` on other systems.
`Build.ps1` packs by default; `-Pack:$false` explicitly disables packaging. Use `-OutputDirectory`
(alias `-LocalFeed`) on build and `-LocalFeed` on push to select a different folder.
`-Version` applies the exact version to restore, build, and pack, and selects that exact set on push.
Arbitrary prerelease suffixes such as `2.0.0-blah` are supported.

Without `-Version`, build uses the next patch after the highest stable local Git tag, or a higher
release line already in the local feed, and appends a fresh `-local.<UTC timestamp>` suffix.
A stable package already in the feed advances to its next patch. Fetch tags first if they may
be stale. Build also advances past future local timestamps or a higher-sorting prerelease so
the generated version is newer than the feed. Push selects the highest semantic version and requires every configured package; it never
silently publishes an older set when the newest is incomplete. Both flat and version-subfolder
layouts are supported. Conflicting duplicate package contents are rejected.

`Pack-Local.ps1` delegates to the same build/pack implementation and skips tests. It accepts
`-Version`, `-LocalFeed`, and `-DryRun`; without a version it uses the same automatic selection
as `Build.ps1`. The legacy `-AllowVersionDowngrade` switch remains accepted, but is unnecessary:
explicit versions are honored and automatic versions advance beyond the feed.
`Test-PackageWorkflow.ps1` checks this
workflow offline, including version propagation, package selection, and failure handling.

Release dry runs do not fetch, pull, switch branches, tag, merge or push. Real version
releases require a clean main branch, fetch tags, fast-forward main, and stop at the first
Git failure. They prompt before tagging/pushing and leave the checkout on main. The
Develop helper prompts before merging and also stops on Git failures.

## Release validation

```powershell
./scripts/Build.ps1 -Version 2.0.0-local.verify -OutputDirectory ./artifacts/packages
./scripts/Test-ReleaseSql.ps1 -Container sql2025 -PackageDirectory ./artifacts/packages -Version 2.0.0-local.verify
```

Use a running SQL Server 2025 Linux Docker container with port 1433 published and its initial
`MSSQL_SA_PASSWORD` environment variable available to Docker inspection. The SQL runner uses
`/opt/mssql-tools18/bin/sqlcmd`, creates a unique release database, and supplies explicit
connection settings to every suite. Host tests also create their own uniquely named databases.
Only these test databases are removed; caller environment variables are restored in `finally`.
Do not use an application database as a test fixture.

The runner selects Host `SqlMode` and `SqlIntegration` tests and Memory/GraphRAG `Integration`
tests, excluding `Evaluation`. Reports go under `artifacts/test-results/sql/<run-id>`.
A failing or skipped required test, zero executed tests, or missing TRX report fails validation.
These tests use deterministic model doubles and do not invoke paid providers.

`Test-PackageConsumers.ps1 -PackageDirectory ./artifacts/packages -Version <version>` checks
package identities, assemblies, readmes and internal dependencies, then extracts and compiles
the README C# examples. It restores from the supplied feed plus NuGet.org with a new isolated
cache and source mapping that requires FabrCore packages to come from the supplied feed.
Its generated projects live outside the repository. The SDK-only agent project references SDK;
the runtime consumer loads all nine package assemblies, starts a standalone host, checks readiness
and agent discovery, and exercises typed storage. The SQL runner supplies an isolated SQL
connection and requires the additional startup/restart persistence checks. Temporary consumer
projects and caches are removed after the run.

Releases use one tag-triggered `publish-nuget.yml` workflow. Run `Release-Develop.ps1`
to merge and push develop to main, then run `Release-Major.ps1`, `Release-Minor.ps1`
or `Release-Patch.ps1` yourself to create the release tag. GitHub builds, runs the
offline tests, packs all configured packages once, and pushes them to NuGet.org in
the same job. Branch pushes and pull requests do not trigger separate validation
workflows. SQL, package-consumer and scripting integration checks remain available
as local commands; they are not a separate publishing gate. Local validation never
tags or publishes.

## Public API compatibility

Published packages use `PublicAPI.Shipped.txt` as their released baseline and
`PublicAPI.Unshipped.txt` for reviewed future additions. The public API analyzer runs during
normal builds and fails on undeclared additions or missing declared APIs; deleting baseline
files also fails the build. Preserve shipped signatures during 2.x changes, including optional
parameters and nullability. Record additive APIs in the unshipped file using the analyzer's
code fix; do not regenerate the shipped baseline to silence a breaking change. These checks
complement the existing Orleans contract files and storage compatibility tests.

The optional local prerelease publisher reads `NUGET_API_KEY` from the environment, falling
back on Windows to an encrypted SecureString saved with `Export-Clixml` in the ignored
`scripts/Push-NuGet.credential.clixml`, then the Windows local application data folder at
`FabrCore/NuGetApiKey.clixml`. Encryption is tied to the current Windows user and machine.
Never commit credentials. It previews a complete local package set and prompts before
pushing and unlisting it. ACL migration uses `FABRCORE_ADMIN_API_KEY`; see
[database modes](../docs/database-modes.md) for supported export/import sources.
