# LANCommander.Community

Community plugins for LANCommander Server and Launcher. Source, validation, and dependency baselines are shared; each installable plugin has an independent version, changelog, tag, and release artifact.

## Components

| Component | Current development version | Host | Install artifact | Compatibility / identity |
|---|---:|---|---|---|
| [Server.Mcp](plugins/Server.Mcp/README.md) | `0.2.1` | Server | `LANCommander.Mcp.0.2.1.nupkg` | Server 2.2.0-alpha.4+; package and plugin IDs remain `LANCommander.Mcp` / `io.github.aaronpowell.lancommander.mcp`. |
| [Server.RecompCatalog](plugins/Server.RecompCatalog/README.md) | `0.4.2` | Server | `LANCommander.RecompCatalog.Plugin.0.4.2.nupkg` | Minimum Server 2.2.0-alpha.3; package and plugin IDs are unchanged. |
| [Launcher.MQTT](plugins/Launcher.MQTT/README.md) | `0.2.0` | GUI Launcher | `launcher-mqtt-0.2.0.zip` | Built and tested against Launcher 2.2.0-alpha.4 source commit `4c8c73e47da6d4198c6153cc93ca7bb480d30abe`; existing assembly/plugin/config identities are unchanged. |

These are development versions, not a claim that new releases have been published. The MCP `0.2.0` package is historical; this repository uses `0.2.1` so its artifact is never overwritten. Recomp retains its prior `0.4.1` source version in the changelog and advances to `0.4.2`. The launcher `0.2.0` prototype has not been published from this repository.

The original MCP and Recomp MIT license files, MCP third-party notices, and the launcher MQTTnet license are retained with their components. No new license is inferred for launcher source because none was present in the supplied prototype snapshot.

## Build and validate

Install the .NET 10 SDK, Node.js (for MCP setup tests), PowerShell 7, and Python 3.12 (for launcher Home Assistant template tests). To validate one component and produce only its local install artifact:

```powershell
pwsh ./eng/Validate-Plugin.ps1 -Plugin Server.Mcp -OutputDirectory ./artifacts/validation-mcp
pwsh ./eng/Validate-Plugin.ps1 -Plugin Server.RecompCatalog -OutputDirectory ./artifacts/validation-recomp
pwsh ./eng/Validate-Plugin.ps1 -Plugin Launcher.MQTT -OutputDirectory ./artifacts/validation-launcher
```

Each output directory must be new. Validation runs the component's Release tests, packaging checks, and component-specific tests; it fails rather than overwriting an existing artifact. Launcher validation materializes the pinned LANCommander source into `.cache/lancommander-alpha4`; no mutable checkout or local project tree is used as an implicit dependency.

Run repository checks and release-selection tests with:

```powershell
pwsh ./eng/Test-ReleaseSelection.ps1
pwsh ./eng/Test-Repository.ps1
```

Use the component validation commands above to build and run tests. Launcher host-loader tests require the pinned host source and staged plugin path; the validation script prepares both instead of relying on shell-specific environment setup.

## Independent releases

The authoritative plugin version is `version.txt` in the component directory; `CHANGELOG.md` records that component's release history. Shared build defaults and NuGet dependency versions live in root `Directory.Build.props` and `Directory.Packages.props`. LANCommander compile-time packages use `2.2.0-alpha.4`; the launcher host-source commit is separately pinned in [`eng/lancommander-source.json`](eng/lancommander-source.json).

Release tags explicitly select one plugin:

```text
server-mcp-v0.2.1
server-recompcatalog-v0.4.2
launcher-mqtt-v0.2.0
```

The release workflow verifies the tag's complete SemVer version against that component's version file, validates the selected package, and publishes only the checked artifact bytes. Server components produce NuGet plugin packages; Launcher.MQTT currently produces a drop-in ZIP, not a launcher NuGet package. Releases are GitHub Releases only; no NuGet.org publishing is configured.

For a coordinated LANCommander upgrade, update the central package baseline and (when needed) the pinned launcher source, then update the version/changelog of each affected component in the same reviewed change. Give each component its own next SemVer version and create a separate matching tag for each artifact. Shared changes do not automatically release every component, and one plugin's tag never publishes the others.

The server package feeds contain `LANCommander.SDK`, `LANCommander.Server.Plugins`, and `LANCommander.Server.UI` `2.2.0-alpha.4` packages. Their runtime/native exclusions and plugin load-context checks are preserved; helper projects are not separately packed. Launcher source contracts are materialized from the fixed commit rather than being represented by a guessed NuGet adapter.

## Launcher.MQTT protocol

The launcher transport speaks MQTT independently of Home Assistant. For a configured topic prefix such as `lancommander/livingroom`, it subscribes to `command/+`; command messages must be non-retained JSON containing a fresh non-empty `requestId`, `action`, `gameId`, `issuedAt`, and `expiresAt`. The allowed actions are `launch` and `install` on `command/<game-uuid>`, `select` on `command/select`, and `launch_selected` / `install_selected` on their matching command topics. Commands expire within 30 seconds, are deduplicated, and are limited to exposed games; selection does not itself launch or install anything.

Clients can observe `state`, `selection`, retained `availability`, and non-retained request-correlated `result` topics under the same prefix. Home Assistant discovery payloads under `homeassistant/` are the HA-specific integration layer; other MQTT clients can publish the documented command envelope and consume the same state/result topics directly. The plugin supports authenticated MQTT 5. Its existing `homeassistant.json` stores broker username/password as plaintext; TLS defaults off, and when enabled the current prototype does not verify broker certificates. Use broker ACLs and trusted networks, and treat both plaintext mode and unverified TLS as insecure. These settings and warnings are preserved, not redesigned, by the migration.

The launcher prototype's earlier HA-to-LANCommander launch PoC was reported working. The newly imported settings UI and remote installation flow have automated tests but have not been verified through a real GUI install or live Home Assistant instance.

## Migration and provenance

The source snapshots were imported from the MCP, Recomp Catalog, and Home Assistant launcher prototype worktrees. Historical package, assembly, plugin, persisted data, HA entity, and configuration identities are retained; only repository structure and the launcher's logical component name use `Launcher.MQTT`. Keep existing launcher plugin directories and populated `homeassistant.json` files when upgrading. The package template contains blank broker credentials only.

No original repository is changed or archived by this monorepo. The community repository is independent and is not an official LANCommander release channel.
