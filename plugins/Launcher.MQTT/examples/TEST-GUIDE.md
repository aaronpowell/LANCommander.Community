# Launcher test package 0.2.1

Actual Windows GUI Launcher plugin targeting **LANCommander v2.2.0-beta.3**,
source commit `9eefa639a904059c0a03f39e7908332f468828af`, .NET 10.
The earlier PoC's real HA-to-LC launch was confirmed by the user. This iteration
adds native configuration, scalable selection and the normal LC install queue.
Its new GUI and real install workflow still require your smoke test.

## Install / upgrade

1. For an existing ZIP install, exit the Launcher and follow the
   [README migration steps](../README.md#install--upgrade) **before** installing
   the NuGet package. Back up settings and journal securely outside `Plugins`,
   then move them to `Data` within the normalized plugin folder. The host
   updater cannot preserve root files; uninstall deletes `Data` as well.
2. Locate the existing Launcher **data/config root**, not just the executable
   directory. `LANCOMMANDER_DATA_DIR`, if set, is the exact root; otherwise it is
   normally `Data` beneath the Launcher's working directory. Protected locations
   can use local application data instead. Use the existing configuration/DB root.
3. Use the Launcher's plugin manager to install
   `LANCommander.Community.Launcher.MQTT.Plugin.0.2.1.nupkg` from a local file
   or configured feed, and restart to apply the staged install. The resulting
   layout is:

   ```text
   <Launcher data root>\
     Plugins\
       LANCommander.Community.Launcher.MQTT.Plugin\
         LANCommander.Community.Launcher.MQTT.Plugin.dll
         LANCommander.Community.Launcher.MQTT.Plugin.deps.json
         LANCommander.Community.Launcher.MQTT.Core.dll
         LANCommander.Community.Launcher.MQTT.Transport.dll
         MQTTnet.dll
         homeassistant.example.json
         Data\
           homeassistant.json
           discovery-topics.json
   ```

For manual installation, extract only `lib/net10.0` contents into the normalized
plugin folder, not the entire nupkg wrapper. Preserve `Data`; a manual update
can also move legacy root settings/journal there on first start, but that is
too late for a NuGet updater that has already deleted root files.
Do not overwrite populated settings with the blank template. Existing
`Favorites` migrate to selected exposure automatically. Keep the same client
ID/topic prefix to update existing entities. No host SDK, Launcher DLLs,
build-output `runtimes` directory, ZIP or nupkg wrapper belongs in the plugin folder.

## Configure in the Launcher

Start the GUI Launcher, sign in and open **Settings > Home Assistant MQTT**.
The settings page remains available if broker configuration is missing/invalid.

Set broker hostname/IP, explicit port, username, password (masked), optional TLS,
a unique stable client ID and topic prefix. Defaults: plaintext, port 1883.
TLS normally uses 8883, but toggling TLS does not silently change your port.
Both plaintext and TLS still require username/password authentication.
Use **Refresh library**, search by title/UUID, then check games to expose or choose
**Expose all games**. The catalog is the current authenticated user's visible LC
library, including uninstalled games, not every game on the server. UUID-suffixed
HA labels disambiguate duplicate titles.

**Save and apply** atomically writes config and reconnects in background, without a
Launcher restart. It rejects changes during a remote launch/install. Connection
problems retry without blocking normal local use. Changing broker/client/topic
identity requires the old broker online so retained discovery can be removed first.
The first eight checked games preserve legacy direct launch buttons; all mode
exposes the full catalog through one select rather than a button per game.

Manual `Data/homeassistant.json` remains supported (restart after editing):

- `BrokerHost`, `BrokerPort`, `BrokerUsername`, `BrokerPassword`, `UseTls`.
- `ClientId`: unique stable 8-64 ASCII letters/digits/underscore/hyphen identifier.
- `TopicPrefix`: unique prefix, e.g. `lancommander/livingroom`.
- `ExposureMode`: `"selected"` (default) or `"all"`.
- `Favorites`: UUID/title array used for selected exposure and first-eight legacy
  buttons. Empty means no exposed games in selected mode. Existing credentials and
  favorites are preserved when editing unrelated settings in the UI.

No Windows Credential Manager entry is read. MQTT 5 is required. HA's MQTT
integration must use the same broker.

**Security:** the JSON password is unencrypted. Plaintext MQTT exposes credentials
and commands; TLS intentionally ignores all certificate errors (including
self-signed/untrusted certs and hostname mismatches), permitting impersonation.
Use trusted LAN access controls, restricted Windows file permissions and broker
authentication/topic ACLs. Never share populated config or credential backups.
The plugin does not log credentials.

Plugin broker ACLs need read access to `<TopicPrefix>/command/+` and
`homeassistant/status`; write access to `<TopicPrefix>/availability`,
`<TopicPrefix>/state`, `<TopicPrefix>/selection`, `<TopicPrefix>/result`, and
`homeassistant/{button,select,sensor,binary_sensor}/<ClientId>/+/config`
(expand the braces into four ACL rules). Permit only trusted HA clients to publish
commands. No game paths/command lines appear in discovery or commands.

## One-game run trial

1. Keep Windows awake/unlocked; let the signed-in Launcher library load. Verify
   an exposed installed game launches locally, then exit it.
2. Find the **LANCommander Launcher** MQTT device in HA. It has **Game** select,
   **Run selected**, **Install selected**, readiness/status/outcome sensors and
   selected-game installation status. Legacy favorite buttons remain.
3. Wait for readiness. MQTT online only means connected, not authenticated/ready.
   Choose a game: selection must not launch or install anything. Wait for the
   nonoptimistic select to reflect the plugin's response (about two seconds).
4. Turn on TV/select PC HDMI before launch. Press **Run selected** once.
   Expect `starting`, observed `running`, then `completed` after normal exit.
   `accepted` is not proof of process start; `completed` is not an exit-code verdict.
5. Pressing Run on an uninstalled game must report `game is not installed`, never
   an implicit install. Selection of another game alone must do nothing.

The included `home-assistant-launch-game.yaml` retains the direct-UUID launch
example with TV/input preparation and correlated outcomes. Replace sample TV,
game, MQTT and HA entity IDs. WOL does not unlock Windows or prove Launcher ready.
All emitted action templates use HA-supported `range`/`random`/`format`, not
unsupported `uuid()`. Each render creates a fresh UUIDv4-shaped ID and exactly
30 seconds from one issue timestamp.

## One-game install trial

1. In native Launcher game settings configure **exactly one absolute install
   directory**. Zero or multiple distinct destinations produce
   `install_destination_not_configured`; remote requests cannot provide a path.
2. Expose one small **uninstalled** game already in your signed-in library.
   Ensure local install queue is idle and the Launcher is connected to its server.
3. Select it in HA, then press **Install selected** once. Inspect the normal
   Launcher install queue: it must use the configured destination and normal
   required-dependency/planner workflow. Optional addons/tools are not selected.
4. HA operation status is `installing`; the installation sensor reports the
   selected queue phase and a `progress` attribute when download byte totals are
   available. It is not a fabricated whole-install percentage. Final remote result
   `installed` requires queue completion plus persisted installed record/manifest.
   Failure/cancellation or blocked dependencies must produce an explicit failure.
5. Completion must **not launch the game**. Only a separate **Run selected** may
   start it. Installing an already installed game is rejected: no update,
   modification, uninstall or implicit launch is offered.
   A plan containing an already-installed game dependency is also rejected with
   `install_plan_would_modify_installed_game`; perform that addon/dependency case
   locally, because the host's installed-game branch can modify addons/tools.

Avoid overlapping manual and remote actions in this iteration. Local active
queue/running-game guards reject remote operations, but the upstream host has no
single global lock preventing a later manual action from racing a remote preflight.

## Reconciliation / failure checks

- Switch selected/all exposure, save, and verify options refresh. Remove the
  selected game from exposure: selection becomes unknown, direct commands are
  rejected, and stale favorite discovery is tombstoned.
- Reconnect broker/restart HA: catalog/config/state replay, without replaying
  launch/install requests. `discovery-topics.json` tracks owned discovery topics
  for removal after restart. Do not retain any command.
- Request IDs deduplicate for the bounded validity window within one Launcher
  process, including config reconnects. A full window rejects new IDs. There is
  no durable replay ledger across a full process restart; do not resend commands
  after restarting the Launcher.
- Stop the broker and verify local LC stays usable. Exposure/credential edits
  can still save; identity changes wait for old-broker cleanup. External config
  identity changes or uninstalling the plugin can require manual deletion of old
  retained discovery on that broker.

## Troubleshooting / rollback

Look in Launcher logs for plugin loading, configuration errors and MQTT connection
messages. The matching GUI host is required; headless CLI initialization is inert.
Connection retries report DNS/port/authentication/ACL/TLS listener failures, not
certificate errors (intentionally ignored). A startup warning describes plaintext
or unverified TLS. In HA, `<TopicPrefix>/state` is retained; `/result` is nonretained.
Results include request/game IDs and reasons such as not ready, busy, not exposed,
not installed, missing destination, disconnected server or install/dependency failure.
Malformed/retained/expired commands and bounded-ingress overflow never execute.

To roll back: exit Launcher, move this plugin folder out of `Plugins`, restart.
No games are uninstalled. Remove old retained discovery through normal HA/broker
administration if removing the plugin entirely.

## Verification boundary

Automated checks build from isolated pinned beta.3 source, exercise the actual
plugin loader/default host type identity/headless initialization and inspect the
four-DLL inventory. The actual NuGet artifact is searched on a local feed,
installed/staged by the pinned host package manager, upgraded with Data
preservation, and rejected on the wrong host or outside the verified host range.
Core tests cover selection, dynamic eligibility, deduplication,
separate install/run, failure outcomes and destination guards. Real local MQTT 5
plaintext and self-signed/wrong-host TLS tests cover auth, retained rejection,
simulated launch/install, no auto-launch, live catalog cleanup, journal cleanup,
HA birth replay and reconnect. Exported packaged templates pass a representative
HA Jinja sandbox (15,000 unique IDs, exact expiry and select/action rendering).

These are not a live GUI, real game installation, server dependency/download test,
or a running HA test. The prior launch PoC was user-verified; this iteration's new
settings page/select/install behavior needs the trials above. Current-game describes
the tracked remote launch, not all manual launches. The pinned host dependency
graph reports upstream NuGet vulnerability/version warnings; use controlled testing.
