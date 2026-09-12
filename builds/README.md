# Build and release

Run from PowerShell 7 with .NET 10 and Git installed. Scripts resolve repository paths
relative to themselves, so they also work when invoked from another directory.

`Projects.psd1` is the shared package and test-project inventory used by local packaging,
builds, publish previews and GitHub Actions. SQL Server, Memory, GraphRAG and service
contracts are now integrated into Host/Core/SDK; their former standalone packages are
not published. `FabrCore.Host.Testing` is included in all nine-package releases.

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
`dotnet run`. Integration, Evaluation and SqlMode categories are excluded from this
deterministic build. SQL mode tests are explicit and use the existing Docker container;
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
the generated version is newer than the feed. Push selects the highest semantic version and requires all nine packages; it never
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

GitHub Actions accepts stable `vX.Y.Z` tags, uses the same build script, and publishes only
after tests and packing succeed. Publishing is not part of local build validation.

The optional local prerelease publisher reads `NUGET_API_KEY` from the environment, falling
back on Windows to an encrypted SecureString saved with `Export-Clixml` in the ignored
`scripts/Push-NuGet.credential.clixml`, then the Windows local application data folder at
`FabrCore/NuGetApiKey.clixml`. Encryption is tied to the current Windows user and machine.
Never commit credentials. It previews a complete local package set and prompts before
pushing and unlisting it. ACL migration uses `FABRCORE_ADMIN_API_KEY`; see
[database modes](../docs/database-modes.md) for supported export/import sources.
