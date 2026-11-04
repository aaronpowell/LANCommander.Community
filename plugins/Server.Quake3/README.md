# Server.Quake3

Native Quake 3 administration in LANCommander Server at `/Plugins/Quake3`.
Select an explicitly associated LC server, inspect status, configure FFA,
duel, team deathmatch or CTF, save presets and apply a startup rotation.
Next map, change map and **restart match** issue RCON commands; none restart
the container.

## Compatibility and development status

Requires LANCommander Server **2.2.0-beta.3 or later**. The Quake-specific
central package file pins only this plugin's SDK, Server.Plugins and Server.UI
contracts to published `2.2.0-beta.3`; other plugins' host pins are unchanged.
The host provides `IServerProvider.ListFilesAsync`, `OpenFileAsync`,
`WriteFileAsync` and `ServerFileInfo`. These APIs were added in
[LANCommander/LANCommander#480](https://github.com/LANCommander/LANCommander/pull/480)
and are included in beta.3. A running host must itself be beta.3 or later;
restoring compile-time NuGet packages does not upgrade the server.

The plugin package and assembly are both
`LANCommander.Community.Server.Quake3`. Its private helper assembly is
`LANCommander.Community.Server.Quake3.Core`. Host assemblies are compile-only
and never included in the package.

## Hosting and configuration

Use the official **LANCommander/LANCommander.Plugins.Docker** plugin to host
the game server. Quake administration has no Docker package dependency,
credentials, socket client, shell, Python service or iframe. Provision and
manage image, volumes, startup arguments, game port mapping and consoles in
Docker/LC, not this plugin. No proprietary Quake assets are distributed.

Configure associations in the LC server's configuration:

```json
{
  "Plugins": {
    "Quake3": {
      "Servers": [{
        "ServerId": "<existing LC server GUID>",
        "ContentRoot": "",
        "StartupConfigPath": "Overlay/baseq3/dadlan-admin/server.cfg",
        "ExecPath": "dadlan-admin/server.cfg",
        "RconHost": "<address reachable from LC>",
        "RconPort": 27600,
        "RconPassword": "<24-64 URL-safe characters supplied server-side>"
      }]
    }
  }
}
```

Use the deployment's secret configuration provider for `RconPassword`;
do not commit passwords. Credentials and engine settings are never returned
to the browser. The plugin page, navigation and every service operation
enforce LC's administrator access policy.

All paths are **relative to the selected engine's file root**, not LC's local
filesystem. `ContentRoot` supplies the parent of `Server/baseq3` and
`Overlay/baseq3`. Set `StartupConfigPath` to a writable path that is actually
visible to the game and `ExecPath` to the corresponding game-relative path.
The example is suitable only if the overlay supplies that game-visible file.
The plugin does not invent mounts or modify server provisioning.

The source DadLAN compose mounts game data at `/config`, containing
`Server/baseq3` and `Overlay/baseq3`. Its startup configuration is a separate,
read-only bind at `/quake3-home/baseq3/dadlan-admin`, and the sidecar writes
the bind's source directory. **That mount is not automatically writable or
accessible through the Docker engine's `/config` file API.** Reuse requires
an independently provisioned writable file-root path with matching game
visibility; the plugin cannot manage the existing read-only bind.
Quake listens on container UDP 27600; choose the reachable host-mapped port
for RCON, not necessarily the player port or a container-only hostname.

## Behavior and failure semantics

Limits: time 0-120, frags 0-1000, captures 0-100 (zero disables the limit).
Rotations accept 1-32 installed map names; presets have trimmed 1-48 character
names and a maximum of 32 per server. Applying starts the first map and
renders a wraparound `dadlan_r*`/`vstr` rotation. Preset saves do not affect
startup or the running match. Configuration changes are explicit; no server
is modified on initialization or by opening the page.

RCON is paced at least 0.6 seconds apart per endpoint, with a 3-second first
response timeout, bounded multipart replies, fixed typed actions and explicit
protocol/command errors. After a startup write, live-apply failure is reported
as **startup saved, live apply failed** rather than rolled back or hidden.
File/persistent-state write failures are never reported as successful apply.

The host file API does **not** guarantee atomic startup replacement: failures
and cancellations can leave partial bytes. A durable intent journal is saved
before writing; confirmed startup and last acknowledged live configuration
are tracked separately. Uncertain writes/live application survive plugin
restart as a visible warning with the attempted configuration retained for
reapplication. No live exec is sent after a failed write or failed durable
write confirmation. Reapply or repair startup before restarting the game
server; the plugin cannot safely roll back an unknown partial external write.

PK3 ZIP entries are inspected without extraction, plus loose `maps/*.bsp`,
in both configured roots. A per-server path/size/UTC-mtime catalogue cache
avoids downloading unchanged archives; refresh forces inspection. Remote
nonseekable streams use bounded temporary staging in the host-provided
durable plugin data directory, never the install directory. Missing optional
folders differ from stopped/unavailable engines: a failed scan preserves and
marks the last-known catalogue stale, and applying configurations or changing
maps requires a current catalogue. Status and match restart remain separate.

Inspection limits are 1 GiB per PK3, 2 GiB downloaded per scan, 100,000
listed files/archive entries, 32 MiB central-directory metadata and 8,192 map
names. Missing size/mtime metadata is an unavailable catalogue, not a cache
hit. Bounded JSON state is at most 1 MiB per server.

Persistent state uses `PluginContext.DataDirectory`, with isolated server
identities and atomic replacement. Invalid stored state is an explicit error.
Deleting/replacing state is an operator action, not an automatic recovery.

## Build and verification

The pinned public host packages were built from upstream tag
`v2.2.0-beta.3` (source commit
`9eefa639a904059c0a03f39e7908332f468828af`).

Requires .NET 10 SDK and PowerShell 7. Core tests do not need LC, Docker or
game assets:

```powershell
dotnet test .\plugins\Server.Quake3\LANCommander.Community.Server.Quake3.Tests\LANCommander.Community.Server.Quake3.Tests.csproj -c Release
pwsh .\eng\Test-ReleaseSelection.ps1
pwsh .\eng\Test-Repository.ps1
```

For full validation, use the public NuGet feed, then run:

```powershell
dotnet restore .\plugins\Server.Quake3\LANCommander.Community.Server.Quake3.Host.Tests\LANCommander.Community.Server.Quake3.Host.Tests.csproj --source https://api.nuget.org/v3/index.json --force --no-cache
pwsh .\eng\Validate-Plugin.ps1 -Plugin Server.Quake3 -OutputDirectory <new-output-directory>
```

No source checkout outside the current workspace, Docker host or provisioned
game server is changed by those tests. Automated core/host/UI/package checks
are not a live LC install, Docker deployment or real game trial. No screenshot
is supplied without a running host.

## Behavioral source

Adapted from the behavior and tests of
[`aaronpowell/dadlan-sydney`](https://github.com/aaronpowell/dadlan-sydney/tree/64d19e6bd7ad990eb885eae10112bd1609d05d3e/roles/quake3),
including its Python RCON/admin tests and compose mounts, read remotely.
The native implementation does not redistribute that Python/HTML sidecar.
