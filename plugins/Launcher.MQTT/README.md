# Launcher.MQTT

A Windows LANCommander GUI Launcher plugin that publishes bounded command/state events over MQTT. Home Assistant is one supported integration; its discovery payloads and sample automation are HA-specific, while the command and state topics can be used by any MQTT client.

The source projects and namespaces use
`LANCommander.Community.Launcher.MQTT.*`; the deployed assemblies and plugin
folder now use their project names, including
`Plugins/LANCommander.Community.Launcher.MQTT.Plugin`. The persistent plugin ID
and `homeassistant.json` configuration identity remain unchanged. Version 0.2.1
is an unreleased packaging/data-safety update to the published 0.2.0 ZIP
prototype; this change does not publish a release or replace that ZIP.

## Current prototype (0.2.1)

Open **Launcher settings > Home Assistant MQTT** to configure broker address/port,
username, masked password, optional TLS, stable client ID and topic prefix.
Refresh the signed-in user's visible library, search by title/UUID and check the
games to expose, or enable **Expose all games**. Installed and uninstalled games
are included. **Save and apply** atomically saves `Data/homeassistant.json` and reconnects
without restarting the Launcher; it is blocked during a tracked remote operation.
Missing/invalid configuration leaves the settings page available, not a dead plugin.
Older files migrate to selected exposure using their existing `Favorites` and
credentials. Keep populated configuration when upgrading.

HA discovers one UUID-backed **Game select**, **Run selected**, **Install selected**,
installation status/progress attributes and the original readiness/outcome sensors.
Selection alone does nothing. Run requires installation; install uses the Launcher's
normal planner/queue and required dependencies, never automatically launches, and
rejects an already-installed game rather than treating it as a modification request.
Configure **exactly one absolute game install directory in native Launcher settings**;
zero/multiple destinations are explicitly rejected. Optional addons/tools are not
selected remotely. The first eight chosen games retain their old launch-button IDs;
all-game exposure does not create thousands of buttons.
Plans that would reinstall/modify an already-installed dependency are rejected too;
complete those addon/dependency cases locally rather than allowing unintended
changes through the host's installed-game queue branch.

The host-independent coordinator enforces bounded JSON commands (`requestId`,
`action`, `gameId`, `issuedAt`, `expiresAt`), a 30-second maximum lifetime,
readiness, live exposed-library membership, installation/running checks,
one remote operation at a time and a bounded deduplication window. A full window
rejects new requests rather than evicting valid IDs. Accepted is not completed;
install success requires terminal queue status and an installed record/manifest.
Launch completion means the normal LC run returned, not an exit-code verdict.

The source-pinned adapter and MQTT transport are now implemented as well. The host adapter builds against the exact `v2.2.0-beta.3` source tree, starts only in the GUI launcher, and keeps a fresh service scope alive for the full `GameService.Run` task. It launches only one unambiguous primary action for an allowlisted, already-installed game. The adapter retains verified source project references for its launcher service contracts and host-loader tests.

The MQTT client uses authenticated MQTT 5, clean sessions, non-retained commands/results,
retained discovery/state and last will, bounded ingress and reconnect/HA-birth replay.
The library, selection and installation state reconcile about every two seconds.
Removed/inaccessible games invalidate selection and remove stale retained favorite
discovery. `Data/discovery-topics.json` journals owned topics for cleanup across restarts.
Changing broker/client identity through the UI requires the old broker to be reachable
so old discovery can be removed. External JSON edits require a Launcher restart;
external identity changes may require manual cleanup on the old broker.

The earlier PoC's real HA-to-LC launch was confirmed by the user. Source-pinned
loader checks and plaintext/self-signed TLS broker tests use a simulated game host;
the new settings UI and real installation still need user-machine validation.

**Network security:** plaintext MQTT exposes credentials and commands on the network.
TLS without certificate verification permits broker impersonation. Use only a
trusted LAN with network access controls and broker authentication/topic ACLs.
The JSON stores the password as plaintext: restrict access to the installed file,
do not commit it, and remove credentials before sharing configuration or backups.
Startup logs warn which insecure transport mode is in use, but never log the credentials.

## MQTT command and state protocol

Set `TopicPrefix` to the topic namespace clients should use. The launcher
subscribes to `<prefix>/command/+`. Publish non-retained JSON commands with:

```json
{
  "requestId": "a fresh non-empty UUID",
  "action": "launch",
  "gameId": "an exposed game UUID",
  "issuedAt": "2026-11-02T02:00:00Z",
  "expiresAt": "2026-11-02T02:00:30Z"
}
```

The payload is limited to 1024 bytes and must expire within 30 seconds. Supported
actions are `launch` and `install` on `command/<game-uuid>`, `select` on
`command/select`, and `launch_selected` / `install_selected` on matching
`command/launch_selected` and `command/install_selected` topics. Selected
actions require the empty UUID; `select` requires a game UUID. Requests are
deduplicated, bounded, and restricted to games exposed in the signed-in user's
library. Selecting a game does not run or install it.

Observe `<prefix>/state`, `<prefix>/selection`, and retained
`<prefix>/availability`; request-correlated outcomes are published to
`<prefix>/result` without retain. MQTT 5 uses authentication, clean sessions,
and bounded reconnect/replay behavior. Only Home Assistant discovery uses the
`homeassistant/` topic tree. For a live topic/config example, see the
[Home Assistant setup](examples/TEST-GUIDE.md).

## Install / upgrade

The release artifact is now a real
`LANCommander.Community.Launcher.MQTT.Plugin.<version>.nupkg`, not a ZIP wrapped
in NuGet. Its package types are `LANCommander.Plugin` and
`LANCommander.Plugin.Launcher`. The full private runtime payload is under
`lib/net10.0`; dependencies supplied by the host are excluded. Search tags
are supplementary, not the installer contract. Build and validate locally:

```powershell
pwsh .\eng\Validate-Plugin.ps1 -Plugin Launcher.MQTT -OutputDirectory "C:\path\to\new-artifact-folder"
```

Use LANCommander **2.2.0-beta.3**'s Launcher plugin manager to install the local
`.nupkg`, or find this package in a configured feed after a maintainer release.
Installation is staged until restart. Releases use GitHub Packages automatically;
NuGet.org is used only if the release workflow has `NUGET_API_KEY`.
Feed visibility/authentication is configured by the user/maintainer, not this
plugin. The existing 0.2.0 ZIP is immutable and is not retroactively published
as NuGet. Version 0.2.1 is not published by this change.

**Existing ZIP installations require preparation before a NuGet upgrade.**
The host updater replaces the plugin folder and preserves **only `Data`**.
The new plugin cannot rescue root files after the installer has removed them.

1. Exit the Launcher completely. Find its existing data/config root (not
   necessarily the executable directory) and back up the entire installed
   MQTT plugin folder to a secure location outside `Plugins`.
2. In `Plugins\LANCommander.Community.Launcher.MQTT.Plugin`, create `Data`.
   Move your populated `homeassistant.json` and `discovery-topics.json` into
   `Data` before reopening the Launcher or staging installation. If either
   destination already exists, back up both copies and reconcile them;
   never overwrite settings or the owned-topic journal blindly.
3. If the old folder is `Plugins\LanCommander.HomeAssistant.Plugin`, with the
   Launcher stopped, rename **only that folder** to
   `LANCommander.Community.Launcher.MQTT.Plugin` after preparing `Data`. If
   the destination also exists, stop and reconcile the duplicate installations
   from backups rather than loading both. Leave other plugin folders untouched.
4. Open the Launcher, install the `.nupkg` through its plugin manager, then
   restart. Check the same broker, client ID/topic prefix, exposed games and
   retained discovery. Do not use uninstall/reinstall to upgrade: uninstall
   deletes `Data` too.

Normal subsequent NuGet upgrades preserve `Data/homeassistant.json` and
`Data/discovery-topics.json`. The plugin also moves legacy root files into
`Data` when started after a manual drop-in update, but fails visibly if both
copies exist. This fallback does **not** replace preparation before the first
NuGet install. Only a blank `homeassistant.example.json` is bundled; use the
settings UI or copy it to `Data/homeassistant.json` for manual configuration.

For manual installation without the manager, extract **only** the contents
of `lib/net10.0` to
`<Launcher data root>\Plugins\LANCommander.Community.Launcher.MQTT.Plugin`
while the Launcher is stopped. Preserve its `Data` directory; do not copy the
whole archive, `lib` wrapper, or any host DLLs into `Plugins`.

## Configure and build the source-pinned plugin

1. Install through the Launcher plugin manager (see above and the [test guide](examples/TEST-GUIDE.md)), restart the GUI and configure it in Launcher settings. Alternatively copy `src\LANCommander.Community.Launcher.MQTT.Plugin\homeassistant.example.json` to the plugin's `Data/homeassistant.json` and fill it in locally. `ExposureMode` is `"selected"` (default; `Favorites` is the UUID/title allowlist, now unlimited) or `"all"` (the authenticated visible library). An empty selection safely exposes no games.
2. Set `UseTls` to `false` for plaintext MQTT (usually port `1883`), or `true` for TLS (usually port `8883`). Use the port configured on your broker; it is not changed automatically. Certificate errors are ignored when TLS is enabled. Existing configurations must add `BrokerUsername` and `BrokerPassword`; no Credential Manager entry is read.
3. Use the repository validation command to materialize the pinned LANCommander
   `v2.2.0-beta.3` source commit and run the core/loader/template tests:

   ```powershell
   pwsh .\eng\Validate-Plugin.ps1 -Plugin Launcher.MQTT -OutputDirectory "C:\path\to\new-artifact-folder"
   ```

4. Configure the installed plugin through **Launcher settings > Home Assistant
   MQTT**, or copy `homeassistant.example.json` as `homeassistant.json` into the
   existing plugin's `Data` directory and fill it in locally. Preserve populated
   settings and the discovery journal when updating. The logical component is
   `Launcher.MQTT`; its package and assembly identity match its normalized project
   name, while semantic plugin/config/MQTT/HA identities remain unchanged.

Home Assistant's MQTT integration must be configured for the same broker. The launch buttons generate a fresh UUID and 30-second issue/expiry window on each press. `examples\home-assistant-launch-game.yaml` illustrates TV power/input setup, waiting for the authenticated Launcher-ready sensor, publishing a fresh command, and waiting for the request-correlated launch outcome. Replace the sample TV entity, game UUID, MQTT topic, and entity IDs with values from discovery. WOL does not unlock Windows or prove Launcher readiness.

Home Assistant does not expose a `uuid()` template function. Discovery and the
script use the supported `range`, `random` and `format` primitives to generate
UUIDv4-shaped IDs with 122 random bits per render, not timestamp-derived IDs.
Expiry is exactly 30 seconds from a single captured issue time. The protocol
still requires request IDs; MQTT retries with the same payload remain deduplicated.
See HA's [random filter reference](https://www.home-assistant.io/template-functions/random/).

## Build and test the core

Run commands in this section from the repository root.

```powershell
dotnet test plugins\Launcher.MQTT\tests\LANCommander.Community.Launcher.MQTT.Core.Tests\LANCommander.Community.Launcher.MQTT.Core.Tests.csproj
```

No remote paths, shell commands, arguments, uninstall, force-stop or implicit
installation/launch are accepted. Broker failures remain isolated from local LC.
Legacy `command/<game UUID>` accepts `launch` and explicit `install`. New
`command/select` requires `action: select` plus a game UUID; `command/launch_selected`
and `command/install_selected` require matching actions and an empty UUID
`00000000-0000-0000-0000-000000000000`. All use the same request ID/time envelope.
Selected-action results contain the resolved UUID. Do not retain any command.

To repeat package discovery, installation, Data-preserving update and beta.3
host-loader checks against the actual artifact:

```powershell
$env:LANCOMMANDER_SOURCE_ROOT = "C:\path\to\LANCommander"
pwsh .\eng\Test-LauncherPackage.ps1 -PackagePath "C:\path\to\LANCommander.Community.Launcher.MQTT.Plugin.0.2.1.nupkg" -ExpectedVersion 0.2.1
```

To render the discovery template exported by the **actual packaged assembly** and
the sample script in a representative HA Jinja sandbox (Python with the pinned
packages in `tests\ha-template-requirements.txt`):

```powershell
$env:LANCOMMANDER_HA_TEMPLATE_EXPORT = Join-Path $env:TEMP "lancommander-ha-discovery.json"
pwsh .\eng\Test-LauncherPackage.ps1 -PackagePath "C:\path\to\LANCommander.Community.Launcher.MQTT.Plugin.0.2.1.nupkg" -ExpectedVersion 0.2.1
python plugins\Launcher.MQTT\tools\Test-HaTemplates.py "$env:LANCOMMANDER_HA_TEMPLATE_EXPORT" --script plugins\Launcher.MQTT\examples\home-assistant-launch-game.yaml
```

This checks 15,000 renders including the favorite/select/install/run templates,
constant-time rapid presses, UUID format/uniqueness, exact expiry and script correlation. It reproduces HA's
context-dependent random filter and supported date/JSON helpers; it does not
replace a manual button press on a running Home Assistant installation.

### Verified package contracts

The released beta.3 source at
[`9eefa63`](https://github.com/LANCommander/LANCommander/commit/9eefa639a904059c0a03f39e7908332f468828af)
already supports launcher NuGet installation. This was rechecked against
current main
[`123ec53`](https://github.com/LANCommander/LANCommander/commit/123ec53a5dd177c2124001ad9f0ce9cb1a6e8acc).
The package reader selects `lib/<framework>`, inspects one assembly attribute
for launcher host/version compatibility and installs the assembly-named folder.
The staging updater retains only `Data`:
[`PluginPackageReader`](https://github.com/LANCommander/LANCommander/blob/9eefa639a904059c0a03f39e7908332f468828af/LANCommander.PluginManagement/PluginPackageReader.cs),
[`PluginStaging`](https://github.com/LANCommander/LANCommander/blob/9eefa639a904059c0a03f39e7908332f468828af/LANCommander.PluginManagement/PluginStaging.cs),
[`LauncherPluginService`](https://github.com/LANCommander/LANCommander/blob/9eefa639a904059c0a03f39e7908332f468828af/LANCommander.Launcher.Services/Plugins/LauncherPluginService.cs).
The service/package-management host projects are not published as usable
NuGet dependencies, so source pinning remains necessary. The exact beta.3
minimum/maximum range is unchanged; newer hosts are not claimed compatible.
