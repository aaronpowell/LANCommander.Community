# Launcher.MQTT

A Windows LANCommander GUI Launcher plugin that publishes bounded command/state events over MQTT. Home Assistant is one supported integration; its discovery payloads and sample automation are HA-specific, while the command and state topics can be used by any MQTT client.

## Current prototype (0.2.0)

Open **Launcher settings > Home Assistant MQTT** to configure broker address/port,
username, masked password, optional TLS, stable client ID and topic prefix.
Refresh the signed-in user's visible library, search by title/UUID and check the
games to expose, or enable **Expose all games**. Installed and uninstalled games
are included. **Save and apply** atomically saves `homeassistant.json` and reconnects
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
changes through alpha.4's installed-game queue branch.

The host-independent coordinator enforces bounded JSON commands (`requestId`,
`action`, `gameId`, `issuedAt`, `expiresAt`), a 30-second maximum lifetime,
readiness, live exposed-library membership, installation/running checks,
one remote operation at a time and a bounded deduplication window. A full window
rejects new requests rather than evicting valid IDs. Accepted is not completed;
install success requires terminal queue status and an installed record/manifest.
Launch completion means the normal LC run returned, not an exit-code verdict.

The source-pinned adapter and MQTT transport are now implemented as well. The host adapter builds against the exact `v2.2.0-alpha.4` source tree, starts only in the GUI launcher, and keeps a fresh service scope alive for the full `GameService.Run` task. It launches only one unambiguous primary action for an allowlisted, already-installed game. Matching published host packages were not available, so the build intentionally uses source project references rather than guessing or silently downgrading package versions.

The MQTT client uses authenticated MQTT 5, clean sessions, non-retained commands/results,
retained discovery/state and last will, bounded ingress and reconnect/HA-birth replay.
The library, selection and installation state reconcile about every two seconds.
Removed/inaccessible games invalidate selection and remove stale retained favorite
discovery. `discovery-topics.json` journals owned topics for cleanup across restarts.
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

## Drop-in test ZIP

After building and validating Release, run:

```powershell
pwsh .\eng\Validate-Plugin.ps1 -Plugin Launcher.MQTT -OutputDirectory "C:\path\to\new-artifact-folder"
```

The ZIP contains `Plugins\LanCommander.HomeAssistant.Plugin`, a configuration
template, a [test guide](examples/TEST-GUIDE.md), sample HA script, dependency
license and SHA-256 inventory. Copy its plugin folder to the Launcher's existing
**data/config root** under `Plugins`, not necessarily beside the executable.
It targets alpha.4 source commit `4c8c73e47da6d4198c6153cc93ca7bb480d30abe`.

## Configure and build the source-pinned plugin

1. Copy the package to the Launcher data root (see the [test guide](examples/TEST-GUIDE.md)), start the GUI and configure it in Launcher settings. Alternatively copy `src\LanCommander.HomeAssistant.Plugin\homeassistant.example.json` to the plugin's `homeassistant.json` and fill it in locally. `ExposureMode` is `"selected"` (default; `Favorites` is the UUID/title allowlist, now unlimited) or `"all"` (the authenticated visible library). An empty selection safely exposes no games.
2. Set `UseTls` to `false` for plaintext MQTT (usually port `1883`), or `true` for TLS (usually port `8883`). Use the port configured on your broker; it is not changed automatically. Certificate errors are ignored when TLS is enabled. Existing configurations must add `BrokerUsername` and `BrokerPassword`; no Credential Manager entry is read.
3. Use the repository validation command to materialize the pinned LANCommander
   `v2.2.0-alpha.4` source commit and run the core/loader/template tests:

   ```powershell
   pwsh .\eng\Validate-Plugin.ps1 -Plugin Launcher.MQTT -OutputDirectory "C:\path\to\new-artifact-folder"
   ```

4. Configure the installed plugin through **Launcher settings > Home Assistant
   MQTT**, or copy `homeassistant.example.json` as `homeassistant.json` into the
   existing plugin directory and fill it in locally. Preserve populated
   `homeassistant.json` files when updating; the ZIP contains only the blank
   template. The logical component is `Launcher.MQTT`, while the package folder,
   assembly, plugin ID, and config filename intentionally retain their deployed
   historical identities.

Home Assistant's MQTT integration must be configured for the same broker. The launch buttons generate a fresh UUID and 30-second issue/expiry window on each press. `examples\home-assistant-launch-game.yaml` illustrates TV power/input setup, waiting for the authenticated Launcher-ready sensor, publishing a fresh command, and waiting for the request-correlated launch outcome. Replace the sample TV entity, game UUID, MQTT topic, and entity IDs with values from discovery. WOL does not unlock Windows or prove Launcher readiness.

Home Assistant does not expose a `uuid()` template function. Discovery and the
script use the supported `range`, `random` and `format` primitives to generate
UUIDv4-shaped IDs with 122 random bits per render, not timestamp-derived IDs.
Expiry is exactly 30 seconds from a single captured issue time. The protocol
still requires request IDs; MQTT retries with the same payload remain deduplicated.
See HA's [random filter reference](https://www.home-assistant.io/template-functions/random/).

## Build and test the core

```powershell
dotnet test tests\LanCommander.HomeAssistant.Core.Tests\LanCommander.HomeAssistant.Core.Tests.csproj
```

No remote paths, shell commands, arguments, uninstall, force-stop or implicit
installation/launch are accepted. Broker failures remain isolated from local LC.
Legacy `command/<game UUID>` accepts `launch` and explicit `install`. New
`command/select` requires `action: select` plus a game UUID; `command/launch_selected`
and `command/install_selected` require matching actions and an empty UUID
`00000000-0000-0000-0000-000000000000`. All use the same request ID/time envelope.
Selected-action results contain the resolved UUID. Do not retain any command.

To repeat the alpha.4 host-loader check after building the plugin:

```powershell
$env:LANCOMMANDER_SOURCE_ROOT = "C:\path\to\LANCommander"
dotnet build src\LanCommander.HomeAssistant.Plugin\LanCommander.HomeAssistant.Plugin.csproj -c Release -p:LanCommanderSourceRoot="$env:LANCOMMANDER_SOURCE_ROOT"
$env:LANCOMMANDER_HOMEASSISTANT_PLUGIN_PATH = (Resolve-Path "src\LanCommander.HomeAssistant.Plugin\bin\Release\net10.0\plugin-package\LanCommander.HomeAssistant.Plugin.dll").Path
dotnet test tests\LanCommander.HomeAssistant.HostLoader.Tests\LanCommander.HomeAssistant.HostLoader.Tests.csproj -c Release
```

To render the discovery template exported by the **actual packaged assembly** and
the sample script in a representative HA Jinja sandbox (Python with the pinned
packages in `tests\ha-template-requirements.txt`):

```powershell
$env:LANCOMMANDER_HA_TEMPLATE_EXPORT = Join-Path $env:TEMP "lancommander-ha-discovery.json"
dotnet test tests\LanCommander.HomeAssistant.HostLoader.Tests\LanCommander.HomeAssistant.HostLoader.Tests.csproj -c Release
python tools\Test-HaTemplates.py "$env:LANCOMMANDER_HA_TEMPLATE_EXPORT"
```

This checks 15,000 renders including the favorite/select/install/run templates,
constant-time rapid presses, UUID format/uniqueness, exact expiry and script correlation. It reproduces HA's
context-dependent random filter and supported date/JSON helpers; it does not
replace a manual button press on a running Home Assistant installation.
