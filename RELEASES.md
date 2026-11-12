# Plugin releases

This is the maintainer procedure for releasing one plugin or coordinating a
LANCommander compatibility-baseline change. It describes the current GitHub
Actions implementation; it does not create tags or publish anything by itself.
**Pushing a matching tag starts a release workflow that publishes a GitHub
Release and eligible package feeds. Get explicit maintainer authorization
before creating or pushing a release tag.**

## Release units and version sources

Each installable component has its own version and changelog.
`eng/plugins.json` is the canonical registry for component keys, tag prefixes,
project and test paths, package identities, artifact formats, feed eligibility,
and host compatibility metadata. Keep it consistent with project/package
metadata when changing a component.

| Component key | Version file | Changelog | Tag format | GitHub Release asset | NuGet feeds |
| --- | --- | --- | --- | --- | --- |
| `Server.Mcp` | `plugins/Server.Mcp/version.txt` | `plugins/Server.Mcp/CHANGELOG.md` | `server-mcp-v<version>` | `LANCommander.Community.Server.Mcp.<version>.nupkg` | GitHub Packages; NuGet.org if `NUGET_API_KEY` is set |
| `Server.RecompCatalog` | `plugins/Server.RecompCatalog/version.txt` | `plugins/Server.RecompCatalog/CHANGELOG.md` | `server-recompcatalog-v<version>` | `LANCommander.Community.Server.RecompCatalog.Plugin.<version>.nupkg` | GitHub Packages; NuGet.org if `NUGET_API_KEY` is set |
| `Server.Quake3` | `plugins/Server.Quake3/version.txt` | `plugins/Server.Quake3/CHANGELOG.md` | `server-quake3-v<version>` | `LANCommander.Community.Server.Quake3.<version>.nupkg` | GitHub Packages; NuGet.org if `NUGET_API_KEY` is set |
| `Launcher.MQTT` | `plugins/Launcher.MQTT/version.txt` | `plugins/Launcher.MQTT/CHANGELOG.md` | `launcher-mqtt-v<version>` | `LANCommander.Community.Launcher.MQTT.Plugin.<version>.nupkg` | GitHub Packages; NuGet.org if `NUGET_API_KEY` is set |

Use complete SemVer in `version.txt`, including prerelease identifiers when
applicable. The tag must contain that exact version, and the changelog must
have a non-empty `## <version>` section. Never reuse a released version or
replace a published release asset; choose a new version and matching changelog
entry for subsequent changes. Values in the files alone do not mean a version
has been published.

All current components target LANCommander `2.2.0-beta.4`. Server components
use the matching published NuGet packages. Launcher.MQTT is source-coupled to
the host and uses the pinned source snapshot in `eng/lancommander-source.json`
(commit `675befe0673c03aded2b45995694661ce454ba95`). Do not substitute a
mutable checkout or invent package adapters for unavailable host contracts.

## Prerequisites and validation

Local validation requires PowerShell 7 and the .NET 10 SDK. MCP validation
additionally uses Node.js; Launcher.MQTT validation uses Python 3.12 and the
packages in `plugins/Launcher.MQTT/tests/ha-template-requirements.txt`.
Launcher validation also needs network access to materialize the pinned
LANCommander source.

Run the repository checks and the selected component's validation before
merging:

```powershell
pwsh ./eng/Test-ReleaseSelection.ps1
pwsh ./eng/Test-Repository.ps1
pwsh ./eng/Test-ReleaseDestinations.ps1
pwsh ./eng/Test-NuGetPublication.ps1
pwsh ./eng/Test-GitHubPackages.ps1
pwsh ./eng/Validate-Plugin.ps1 -Plugin Server.Mcp -OutputDirectory ./artifacts/validation-mcp
```

Run `Validate-Plugin.ps1` for each component being released, with a different
new output directory for each. The validation script runs the component's
Release tests, packaging and inventory checks, and component-specific checks.
MCP validation includes Node tests; Launcher.MQTT includes loader and Home
Assistant template checks. These automated checks do not replace a live GUI
installation or Home Assistant trial. For local launcher validation, install
its test-only Python dependencies first:

```powershell
python -m pip install -r plugins/Launcher.MQTT/tests/ha-template-requirements.txt
pwsh ./eng/Validate-Plugin.ps1 -Plugin Launcher.MQTT -OutputDirectory ./artifacts/validation-launcher
```

`eng/Prepare-LANCommanderSource.ps1` materializes the launcher host source at
the immutable commit in `eng/lancommander-source.json`. Build and validation
verify that pin. Keep host-provided assemblies excluded from the launcher package
and bundle only private dependencies. Server packages retain their host
runtime exclusions and loader-identity checks.

## Releasing one component

1. Make the intended component changes. Update only its `version.txt` and add
   a substantive, non-empty changelog section headed with the exact same
   version. Keep its project, package ID, assembly name, namespace, registry
   entry, and tag prefix aligned. Preserve semantic plugin IDs and persisted
   configuration identifiers unless a separately reviewed migration is
   authorized.
2. Run the repository checks and that component's validation command above.
   Review the generated package, its checksum, and inventory in a fresh
   output directory.
3. Commit the reviewed source and version/changelog changes and merge them to
   `main` through the repository's normal review process. The release plan
   rejects a tag whose commit is not already reachable from `main`.
4. Confirm the exact tag and commit, then—with explicit maintainer
   authorization—create and push the matching tag. For example, after
   substituting the reviewed main commit and the version actually in
   `version.txt`:

   ```powershell
   git tag -a server-mcp-v0.2.1 -m "Server.Mcp 0.2.1" <reviewed-main-commit>
   git push origin server-mcp-v0.2.1
   ```

   The example is illustrative; check the current version file and release
   history before using any version. The remote tag push is the publication
   trigger.
5. Follow the run in **Actions → Component release**. It verifies tag syntax,
   SemVer/version match, exact source commit, and ancestry from `main`, then
   selects only the component named by the tag. A component tag cannot select
   or publish another plugin.

If a tag-triggered run fails before any jobs start, inspect its run details and
confirm no release or feed publication occurred. The same workflow has a
main-only recovery dispatch for that already-pushed tag; it validates the
existing tag, component version, and `main` ancestry and then uses the normal
build, release, and feed jobs. Do not move or recreate the tag. For example:

```powershell
gh workflow run release.yml --repo aaronpowell/LANCommander.Community --ref main -f tag=server-mcp-v0.2.1
```

For a run that successfully built the package but failed later, pass that run's
ID and attempt to reuse the original checked artifact instead of rebuilding.
This matters when the GitHub Release already exists: changed rebuild bytes are
rejected by immutable release checks. Example:

```powershell
gh workflow run release.yml --repo aaronpowell/LANCommander.Community --ref main `
  -f tag=server-mcp-v0.2.1 -f artifact_run_id=<original-run-id> -f artifact_run_attempt=1
```

For a startup failure before any artifact was built, omit `artifact_run_id`;
the recovery dispatch will build from the exact selected tag. Follow the new
run through every destination. Recovery validates the source artifact's
component, package metadata, manifest, and checksum, then compares it to any
existing GitHub Release asset before continuing. Feed jobs use the exact
workflow commit on `main` for publisher tooling, while the package artifact
remains bound to the immutable selected tag. If the original artifact has
expired, do not assume a rebuild is byte-identical; stop rather than replacing
published bytes.

Do not invoke `eng/Publish-Release.ps1` manually as a shortcut around review
or workflow checks. The workflow builds and validates the selected component
once, uploads the resulting bytes as an Actions artifact, and promotes those
same checked bytes to the GitHub Release and eligible NuGet feeds. The
artifact is named for the component, workflow run, and attempt; its name is
passed as a build-job output so a retry of failed downstream jobs downloads
the original build artifact. It is retained for seven days. The main-only
recovery dispatch can retrieve that exact artifact by source run ID and
attempt, validate it, and re-upload those same files under the recovery run;
it does not rebuild in that mode.

The GitHub Release publish job creates a draft, attaches the component asset,
its `.sha256` sidecar, and `release-manifest.json`, then checks their hashes
against the manifest and downloaded assets. After all three are verified,
the workflow publishes the release. Package-feed jobs start only after that
job succeeds.

## Automatic NuGet feed publication

There is one release process: the component tag workflow creates the GitHub
Release and publishes eligible `.nupkg` components to package feeds. There are
no separate manual feed-dispatch workflows.

`eng/plugins.json` explicitly identifies feed-eligible components. All four
`.nupkg` components publish to GitHub Packages on every successful
component release. NuGet.org publication is automatically attempted for those
same components only when the repository Actions secret `NUGET_API_KEY` is
non-empty. If it is absent, the workflow reports the intentional skip; it
does not fail the GitHub Release or the GitHub Packages job. An explicitly
configured key that is invalid or a failed push causes the NuGet.org job to
fail visibly. Launcher.MQTT 0.2.1 and later use the same flow as server packages;
the existing 0.2.0 ZIP release remains unchanged and is not retroactively
converted or sent to feeds.

To enable NuGet.org for future tag releases, a repository maintainer should
create a NuGet.org API key scoped to push new versions of only these package
IDs, give it an expiry, and store it as the repository Actions secret named
`NUGET_API_KEY`:

- `LANCommander.Community.Server.Mcp`
- `LANCommander.Community.Server.RecompCatalog.Plugin`
- `LANCommander.Community.Server.Quake3`
- `LANCommander.Community.Launcher.MQTT.Plugin`

Do not use a personal account-wide key or commit the secret. No API key,
NuGet.org account, trusted publisher, or external service is configured by
this change. The workflow checks secret presence in a minimal step that emits
only a boolean; the key value is not logged or included in job outputs. It is
otherwise supplied only to the NuGet.org publisher step. No separate
environment or reviewer gate is required by this automatic release design.

GitHub Packages uses the repository-linked `GITHUB_TOKEN` and requires no PAT
or extra secret. The reusable release workflow declares `packages: write` as a
maximum; callers pass that ceiling, while each job narrows its own token. Only
the eligible GitHub Packages publisher job has `packages: write` (and
`contents: read`); build/release jobs do not receive package-write access.
The NuGet.org publisher has
`contents: read` and receives the optional API key only for its push step. The
GitHub Release job has `contents: write`.
`RepositoryUrl` in the plugin packages points to
`https://github.com/aaronpowell/LANCommander.Community`, linking packages to
the repository. After a first authorized GitHub Packages publication,
maintainers should review package visibility and repository access settings;
new packages may default to private, and public distribution must be
intentionally enabled in GitHub's package settings.

### Package discovery and eligibility

NuGet discovery uses nuspec **package types**, not just search tags.
LANCommander searches for `LANCommander.Plugin.Server` on the server and
`LANCommander.Plugin.Launcher` on the launcher; each plugin package
also declares the generic `LANCommander.Plugin` type. `PackageTags` are
supplemental search terms. The server projects declare the generic and server
types; Launcher.MQTT declares generic and launcher types. Repository/package
checks verify the produced metadata.

Launcher.MQTT's complete private payload is under `lib/net10.0`, including its
`.deps.json`, private Core/Transport/MQTTnet assemblies and blank
`homeassistant.example.json`. No live configuration, `Data`, or host assemblies
are shipped. `eng/Test-LauncherPackage.ps1` exercises local feed discovery,
the real pinned host package reader/installer, update preservation of `Data`,
and extracted assembly loading. Existing ZIP users must follow the
[launcher migration steps](plugins/Launcher.MQTT/README.md#install--upgrade)
**before** staging a NuGet install, since root configuration/journals are not
preserved by the host updater.

Discovery metadata does not establish installability. LANCommander inspects
the single plugin assembly under `lib/<framework>` for its
`[LANCommanderPlugin]` metadata, including `Hosts` and host-version bounds.
The launcher installer and loader are validated against the released beta.4 source
[`675befe`](https://github.com/LANCommander/LANCommander/commit/675befe0673c03aded2b45995694661ce454ba95).
Its package reader/staging contracts still preserve only `Data`. The launcher
remains bounded to beta.4 until another host version is explicitly validated.
Beta.4 replaces the primary-action flag with `ActionType.Primary`; the MQTT
adapter uses that discriminator while retaining the exactly-one-primary guard.
Earlier upstream discovery research was pinned at commit
[`38f162d`](https://github.com/LANCommander/LANCommander/commit/38f162ddeb2a3db1388cb898c4e6b20c4f4bf95d):
[`PluginPackageTypes.cs`](https://github.com/LANCommander/LANCommander/blob/38f162ddeb2a3db1388cb898c4e6b20c4f4bf95d/LANCommander.SDK/Plugins/PluginPackageTypes.cs),
[`PluginFeedClient.cs`](https://github.com/LANCommander/LANCommander/blob/38f162ddeb2a3db1388cb898c4e6b20c4f4bf95d/LANCommander.PluginManagement/PluginFeedClient.cs),
[`PluginPackageReader.cs`](https://github.com/LANCommander/LANCommander/blob/38f162ddeb2a3db1388cb898c4e6b20c4f4bf95d/LANCommander.PluginManagement/PluginPackageReader.cs),
[`PluginAssemblyInspector.cs`](https://github.com/LANCommander/LANCommander/blob/38f162ddeb2a3db1388cb898c4e6b20c4f4bf95d/LANCommander.PluginManagement/PluginAssemblyInspector.cs),
[`LANCommanderPluginAttribute.cs`](https://github.com/LANCommander/LANCommander/blob/38f162ddeb2a3db1388cb898c4e6b20c4f4bf95d/LANCommander.SDK/Plugins/LANCommanderPluginAttribute.cs),
and the launcher's
[`LauncherPluginCache.cs`](https://github.com/LANCommander/LANCommander/blob/38f162ddeb2a3db1388cb898c4e6b20c4f4bf95d/LANCommander.Server/Plugins/Installation/LauncherPluginCache.cs).

### Ordering, partial failures, and retry safety

The GitHub Release must be successfully published before either feed job starts.
GitHub Packages and NuGet.org are separate downstream jobs: failure in one
does not prevent the other from running and does not roll back the GitHub
Release or a successful publication to the other feed. On a partial failure,
inspect each destination and rerun the failed jobs for the same tag. The
reusable workflow passes the original build artifact name as a dependency
output, so a failed feed retry uses those checked bytes rather than rebuilding.
If the artifact has expired, rerunning the whole workflow rebuilds from the
same tag commit; the immutable GitHub Release checks still reject any changed
asset bytes.

GitHub Packages retries download an existing flat-container package and
compare every archive path and uncompressed entry hash with the GitHub Release
asset. After a push, the workflow polls and applies the same comparison. It
does not use `--skip-duplicate`, does not overwrite a differing version, and
does not assume the feed rewrites package bytes. A mismatch or unavailable
package fails closed; investigate the feed and publish changed content only
under a new version and tag.

NuGet.org repository-signs uploaded packages, so the feed `.nupkg` may have a
different archive SHA-256 from the unsigned GitHub Release asset. On retry or
after an ambiguous push, the publisher downloads the feed package, runs
[`dotnet nuget verify --all`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify),
requires a verified repository signature from NuGet.org and exactly one root
`.signature.p7s`, then compares every other archive path and uncompressed
entry hash. The nuspec, package types, tags, and every payload file must
match; missing, additional, duplicate, or changed entries fail closed. Only
the original verified GitHub package bytes are supplied to `dotnet nuget
push`; the publisher never pushes a rewritten copy and never uses
`--skip-duplicate`.

NuGet's [signed package reference](https://learn.microsoft.com/en-us/nuget/reference/signed-packages-reference)
documents automatic repository signing for packages uploaded to nuget.org.
Committed retry tests use a synthetic `.signature.p7s` fixture to test
comparison and rejection behavior; they do not prove cryptographic signature
validity. Live retry verification uses the supported NuGet CLI. No real feed
upload or credential setup is part of the repository change.

Do not delete or replace a GitHub asset, retag a release, or reuse a package
version to recover from a mismatch. Publish changed content only under a new
component version and tag after maintainer review and authorization.

## Coordinated LANCommander compatibility upgrades

When a shared LANCommander dependency baseline or pinned launcher host source
changes, determine which components are affected. Shared/root changes do not
implicitly release every component. Update and review the shared baseline
and/or source pin, then version and document every intended component
independently. Each component keeps its own SemVer and receives its own
matching tag and workflow run.

The selector can check a coordinated set before building:

```powershell
pwsh ./eng/Resolve-ReleaseSelection.ps1 -Plugins Server.Mcp,Server.RecompCatalog,Server.Quake3,Launcher.MQTT
```

It reports independently configured versions; it does not build, create tags,
or make one atomic multi-plugin release. Validate each selected component
separately, using a distinct new output directory. Review and merge the full
compatibility change to `main` before pushing any release tags, then push the
exact component tags separately. Each component's build and destinations can
succeed or fail independently.

For launcher changes, update the source pin and project references together
only after checking the actual upstream source contracts. Do not replace
unavailable host contracts with guessed NuGet adapters. Registry compatibility
metadata, plugin attributes, and loader/package tests must agree with any
intentional compatibility change.

## Permissions and compatibility boundaries

The release workflow has `contents: write` for publishing the GitHub Release.
The reusable workflow's `packages: write` maximum is narrowed by each job;
only eligible server GitHub Packages publisher jobs receive that permission.
NuGet.org uses the optional repository secret without an environment gate;
the secret is not available to the build or test steps. A maintainer needs
permission to merge reviewed source to `main`, push the release tag, manage
repository secrets, and change package visibility/access settings. Do not
grant broader permissions or perform a remote tag/release action without
explicit authorization.

Project paths, package IDs, owned assembly names, and C# namespaces use the
`LANCommander.Community` hierarchy and exact `LANCommander` casing. Persistent
semantic plugin IDs, server data IDs, launcher configuration, MQTT topics,
and Home Assistant entity identifiers are separate from package identities;
preserve them unless an explicitly reviewed migration is authorized. When
upgrading an earlier manual Launcher installation, replace only the old
plugin folder and preserve its populated `homeassistant.json` and
`discovery-topics.json` as described in the Launcher.MQTT README.

Launcher.MQTT implements a general MQTT command/state protocol. Home Assistant
discovery and templates are its HA-specific integration layer; the plugin is
not HA-only. Its current configuration stores broker credentials as plaintext,
TLS defaults off, and enabled TLS does not verify broker certificates.
Preserve these limitations in user-facing documentation and do not claim those
modes are secure.
