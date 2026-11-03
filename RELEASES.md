# Plugin releases

This is the maintainer procedure for releasing one plugin or coordinating
releases with a LANCommander compatibility-baseline change. It describes the
current GitHub Actions implementation; it does not create tags or publish
anything by itself. **Pushing a matching tag starts a release workflow and can
publish a public GitHub Release. Get explicit maintainer authorization before
creating or pushing a release tag.**

## Release units and version sources

Each installable component has its own version and changelog. The
`eng/plugins.json` registry is the source of component keys, tag prefixes,
project/test paths, package identities, artifact formats, and host compatibility
metadata. Keep these values consistent when changing the registry.

| Component key | Version file | Changelog | Tag format | Release asset |
| --- | --- | --- | --- | --- |
| `Server.Mcp` | `plugins/Server.Mcp/version.txt` | `plugins/Server.Mcp/CHANGELOG.md` | `server-mcp-v<version>` | `LANCommander.Mcp.<version>.nupkg` |
| `Server.RecompCatalog` | `plugins/Server.RecompCatalog/version.txt` | `plugins/Server.RecompCatalog/CHANGELOG.md` | `server-recompcatalog-v<version>` | `LANCommander.RecompCatalog.Plugin.<version>.nupkg` |
| `Launcher.MQTT` | `plugins/Launcher.MQTT/version.txt` | `plugins/Launcher.MQTT/CHANGELOG.md` | `launcher-mqtt-v<version>` | `launcher-mqtt-<version>.zip` |
| `Server.Quake3` | `plugins/Server.Quake3/version.txt` | `plugins/Server.Quake3/CHANGELOG.md` | `server-quake3-v<version>` | `LANCommander.Community.Server.Quake3.<version>.nupkg` |

`Server.Quake3` is a development preview, not release-ready. Its isolated host
pin is `2.2.0-beta.1-fileaccess.local.1`, an explicitly unreleased upstream
build with host-routed server file operations, **not published beta.1**.
Its core tests run in ordinary CI; full plugin/UI/loader/package validation
also runs in the CI matrix and currently fails restore (`NU1102`) because the
development host version is not on NuGet.org. Local validation requires the
upstream feed described in its README. The registry's
`releaseBlockedReason` makes the publisher refuse it. Do not remove that gate
until a real compatible host version is released, pinned and validated.
Other plugins retain their existing central host baseline.

Use complete SemVer in `version.txt`, including prerelease identifiers when
applicable. The tag must contain that exact version, and the changelog must
have a non-empty `## <version>` section. Never reuse a released version or
replace a published release asset; choose a new version and matching changelog
entry for subsequent changes. The versions currently in the files are
development snapshots; their presence alone does not mean they have been
published.

## Prerequisites and validation

Use a reviewed commit that will be on `main` before releasing. Local validation
requires PowerShell 7 and the .NET 10 SDK. MCP validation additionally uses
Node.js; launcher validation uses Python 3.12 and the packages in
`plugins/Launcher.MQTT/tests/ha-template-requirements.txt`. Launcher validation
also needs network access to materialize the pinned LANCommander source.

Run the repository checks and the selected component's validation before
merging:

```powershell
pwsh ./eng/Test-ReleaseSelection.ps1
pwsh ./eng/Test-Repository.ps1
pwsh ./eng/Validate-Plugin.ps1 -Plugin Server.Mcp -OutputDirectory ./artifacts/validation-mcp
pwsh ./eng/Validate-Plugin.ps1 -Plugin Server.RecompCatalog -OutputDirectory ./artifacts/validation-recomp
pwsh ./eng/Validate-Plugin.ps1 -Plugin Launcher.MQTT -OutputDirectory ./artifacts/validation-launcher
```

Run only the `Validate-Plugin.ps1` command(s) for the component(s) being
released. Each output directory must be new; validation fails instead of
overwriting an existing one. The validation script runs the component's
Release tests, packaging and inventory checks, and component-specific checks.
For MCP this includes Node tests; for Launcher.MQTT it includes loader and
Home Assistant template checks. These automated checks do not replace a live
GUI installation or Home Assistant trial.

For local Launcher.MQTT validation, install its test-only Python dependencies
first:

```powershell
python -m pip install -r plugins/Launcher.MQTT/tests/ha-template-requirements.txt
```

`eng/Prepare-LANCommanderSource.ps1` materializes the launcher host source from
`eng/lancommander-source.json`, pinned to commit
`4c8c73e47da6d4198c6153cc93ca7bb480d30abe` (LANCommander
`v2.2.0-alpha.4`). `eng/Build-Plugin.ps1` and launcher validation verify that
pin; a mutable local checkout is not a substitute. Keep host-provided
assemblies excluded from the launcher ZIP and bundle only the plugin's private
dependencies. Server packages use the central LANCommander `2.2.0-alpha.4`
NuGet baseline and retain the runtime exclusions and loader-identity checks
covered by their package tests.

## Releasing one component

1. Make the intended component changes. Update only its `version.txt` and add a
   substantive, non-empty changelog section headed with the exact same version.
   Keep the package/assembly/plugin/configuration identities unchanged unless
   an explicitly authorized compatibility and migration plan says otherwise.
2. Run repository checks and that component's validation command above. Review
   the generated package/ZIP and checksums; use a fresh output directory.
3. Commit the reviewed source and version/changelog changes, and merge them to
   `main` using the repository's normal review process. The release workflow
   rejects a tag whose commit is not already reachable from `main`.
4. Confirm the exact tag and commit, then—with explicit maintainer
   authorization—create and push the matching tag. For example, after
   substituting the reviewed main commit and the version actually in
   `version.txt`:

   ```powershell
   git tag -a server-mcp-v0.2.1 -m "Server.Mcp 0.2.1" <reviewed-main-commit>
   git push origin server-mcp-v0.2.1
   ```

   The example tag is illustrative; check the current file and release history
   before using any version. The remote tag push is the publication trigger.
5. Follow the run in **Actions → Component release**. The workflow verifies
   the exact tag syntax, SemVer/version match, source commit, and ancestry from
   `main`; it then selects only the component named by the tag. A component tag
   cannot select or publish another plugin.

Do not invoke `eng/Publish-Release.ps1` manually as a shortcut around review or
workflow checks. The workflow builds and validates the selected component
once, uploads the resulting bytes as an Actions artifact, and passes those
same checked bytes to the publish job. The Actions artifact is named
`plugin-release-<component>-<run-id>-<attempt>` and retained for seven days.

The publish job creates a **draft GitHub Release**, attaches the component
asset, its `.sha256` sidecar, and `release-manifest.json`, then checks their
SHA-256 hashes against the manifest and downloaded release assets. Once all
three files are verified, the workflow changes the release from draft to
published. This occurs automatically within the tag-triggered run; the
workflow does not stop at a draft for a separate approval click. Releases are
distributed through this repository's GitHub Releases only. Server `.nupkg`
files are LANCommander plugin packages, not NuGet.org publications.
Launcher.MQTT is a drop-in ZIP and is not claimed to support NuGet installation.

## Coordinated LANCommander compatibility upgrades

When a shared LANCommander dependency baseline or pinned launcher host source
changes, determine which components are actually affected. Shared/root changes
do not implicitly release every component. Update and review the shared
baseline and/or source pin, then update the version and changelog independently
for every component intended to receive a release. Each component keeps its
own SemVer value and gets its own matching tag and workflow run.

The selector can check a coordinated set before building:

```powershell
pwsh ./eng/Resolve-ReleaseSelection.ps1 -Plugins Server.Mcp,Server.RecompCatalog,Launcher.MQTT
```

It reports the independently configured versions; it does not build, create
tags, or make one atomic multi-plugin release. Run validation separately for
each selected component, with a different new output directory for each.
Review and merge the complete compatibility change to `main` before pushing
any of its release tags. Push the exact component tags separately; each run
can succeed or fail independently.

For launcher changes, update the source pin and project references together
only after checking the actual upstream source contracts. Do not replace
unavailable host contracts with guessed NuGet adapters. The launcher currently
targets exactly LANCommander `2.2.0-alpha.4`; registry compatibility metadata
and package/loader tests must agree with any intentional compatibility change.

## Failures, retries, and immutability

If a run fails, inspect the failing job and release state before retrying. While
the Actions artifact is retained, re-run the failed publish job or failed jobs
from the same run to promote the already-built, checked bytes. If that artifact
has expired, re-run the workflow for the same unchanged tag; it will rebuild
and revalidate from the tag's source commit.

If publication created a draft but stopped partway through, retrying checks
assets by name and hash, uploads only missing files to the draft, and verifies
the result. It refuses to overwrite an asset whose bytes differ. A published
release is not repaired by replacing or silently changing assets; a missing
published asset or byte mismatch fails for maintainer investigation. Do not
delete a release, force-move/reuse a tag, or change package identity to get
around these guards. Resolve the cause and use a new version/tag for new
content. A failure in one component's run does not roll back other components'
successful releases.

## Permissions and compatibility boundaries

The tag-triggered workflow is granted `contents: write` to create the GitHub
Release and upload its assets. A maintainer needs the repository permissions
to merge the reviewed source to `main` and push the release tag. No PAT or
external publishing service is configured. Do not grant broader permissions
or perform a remote tag/release action without explicit authorization.

Project paths and C# namespaces use the `LANCommander.Community` hierarchy.
Historical deployed package IDs, assembly names, plugin IDs, launcher install
folder, persisted data, and launcher `homeassistant.json` identity remain
unchanged. Keep existing launcher configuration when upgrading; the checked-in
example has blank broker credentials. Changing any installed identity requires
an explicit migration plan and authorization, not just a rename for consistency.

Launcher.MQTT implements a general MQTT command/state protocol. Home Assistant
discovery and templates are its HA-specific integration layer; do not describe
the plugin itself as HA-only or remove the HA integration when documenting
generic MQTT use. The current launcher configuration stores broker credentials
as plaintext, TLS defaults off, and enabled TLS does not verify broker
certificates. Preserve the existing warnings and do not claim those modes are
secure.
