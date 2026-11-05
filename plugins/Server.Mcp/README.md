# Server.Mcp

A server plugin that exposes LANCommander catalog management over the Model Context
Protocol (MCP). It runs **inside LANCommander**, uses its provider contracts and
authentication, and opens no additional listener.

Its source project and namespace use
`LANCommander.Community.Server.Mcp`. The package ID and runtime assembly now
match that project name. The persistent plugin ID remains
`io.github.aaronpowell.lancommander.mcp`.

## Requirements

- LANCommander server **2.2.0-beta.3 or later** with the server plugin framework.
- .NET 10 SDK to build.
- An MCP client supporting Streamable HTTP and an administrator's LANCommander bearer token.

The project references published `LANCommander.SDK` and
`LANCommander.Server.Plugins` **NuGet packages**, not the LANCommander source tree.
The initial implementation was checked against upstream `LANCommander/LANCommander`
`main` at `a77242f02891d86cab29d4d2621f95b2dfedf80d`.

## Build and install

```powershell
dotnet test plugins\Server.Mcp\tests\LANCommander.Community.Server.Mcp.Tests\LANCommander.Community.Server.Mcp.Tests.csproj -c Release
node --test plugins\Server.Mcp\tests\setup.test.mjs
pwsh .\eng\Validate-Plugin.ps1 -Plugin Server.Mcp -OutputDirectory "$env:TEMP\server-mcp-validation"
```

The selected package is written to the output directory as
`LANCommander.Community.Server.Mcp.0.2.1.nupkg`. Upload it through the server's plugin
package installer, then restart LANCommander. The package declares
`LANCommander.Plugin` and `LANCommander.Plugin.Server` and bundles its private
dependencies and dependency manifest under `lib/net10.0`. Host-owned LANCommander
assemblies are deliberately excluded.
Both managed runtime and native assets of the host contracts are excluded:
PowerShell and its native dependencies belong to LANCommander, not this plugin.
Package verification checks that every runtime asset declared in the plugin's
dependency manifest is actually bundled, including platform-specific assets.

If upgrading from 0.1.0 after a `Microsoft.PowerShell.Native` / `build.manifest`
load failure, install 0.1.1 and restart the server. For manual installations,
stop the server, remove the old `Plugins\LANCommander.Mcp` output directory, and
install the new complete `Plugins\LANCommander.Community.Server.Mcp` output.
The package ID and assembly name changed together; the semantic plugin ID did
not. The old package ID was not published to NuGet.org.

For a manual installation, build in Release and copy the **whole output folder**
to `Plugins\LANCommander.Community.Server.Mcp` under the server's application directory, then restart.
The source namespace is now `LANCommander.Community.Server.Mcp`; its package ID,
runtime assembly name, and project name now match; the persistent plugin ID is
unchanged. Do not copy just the main DLL. Use either manual installation or the package
installer, not both.

The published `0.2.0` package is historical and is not replaced by this
repository's development version `0.2.1`. See the repository's
[plugin release procedure](../../RELEASES.md) for the current tag, workflow,
and publication behavior.

## Connect

### Get a token from the plugin page

Open **Settings > Integrations > MCP** (or `/Plugins/Mcp`) while signed in as an
administrator. The page and its navigation entry are contributed by this plugin;
no LANCommander changes or new authentication API are required.

Enter an LC administrator username and password, acknowledge the refresh-token
warning, and select **Generate access token**. The browser posts directly to the
existing same-origin `/api/Auth/Login` endpoint, just like the PowerShell login
example. Password authentication must be enabled. HTTPS is required except on
localhost; credentials are never forwarded to another host or across redirects.

The password is cleared after the request. The access token is masked and held
only in page memory, never local/session storage. The refresh token is discarded.
Use **Copy access token** or **Copy VS Code configuration with token**, then **Clear token**.
Clearing or leaving the page does not revoke the token. The copied configuration
uses the VS Code MCP format and contains the secret: do not commit it.

This is a temporary workaround until LC offers proper API tokens. Tokens expire,
have all permissions of the account that logged in (not just MCP), and **generating
one replaces that account's refresh token**, potentially preventing other clients
from refreshing. Use administrator credentials even if the page itself is already
open as an administrator; LC's login endpoint authenticates the entered account.

### Client setup tabs

The same page shows the MCP endpoint and copyable setup instructions in tabs for
the **GitHub Copilot app**, **Claude Code**, **Codex** and a **General** tab
(transport and auth requirements, plus a VS Code `mcp.json` example). Examples
never contain a real token: they use the `YOUR_LANCOMMANDER_TOKEN` placeholder or
the `LANCOMMANDER_TOKEN` environment variable. The page makes no network requests
other than the existing explicit token generation and clipboard actions.

The endpoint is derived from the address LANCommander serves the page at,
including its scheme, host, port and path base (subpath), so it matches what the
host itself considers its public address. The page does not read forwarded
headers itself. If you opened it via `localhost` or another name clients cannot
use, enter the public address (for example `https://example.com/lancommander/`)
in **LANCommander address used by MCP clients**; it only changes the examples
and is not saved. Local-only and plain HTTP addresses show warnings.

Verified client notes (see official documentation linked from each tab):

- **GitHub Copilot app** (desktop, not VS Code or Copilot CLI): GitHub documents
  adding a custom server under **Customize > MCP** but not that form's fields, so
  HTTP header support there is unverified. As an alternative, GitHub states that
  servers configured for Copilot CLI are automatically available in the app;
  the tab shows the documented CLI header setup (`~/.copilot/mcp-config.json`).
  GitHub does not separately document how the app applies those headers, so
  confirm the tools work in the app.
  [App docs](https://docs.github.com/en/copilot/how-tos/github-copilot-app/customize-github-copilot-app#configuring-mcp-servers),
  [CLI docs](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers).
- **Claude Code**: `claude mcp add --transport http` with an `Authorization`
  header, or a project `.mcp.json` using `${LANCOMMANDER_TOKEN}`.
  [Docs](https://code.claude.com/docs/en/mcp).
- **Codex** (CLI, IDE extension and ChatGPT desktop app share
  `~/.codex/config.toml`): `bearer_token_env_var = "LANCOMMANDER_TOKEN"`, read
  from the Codex process environment. The docs do not say which environment the
  desktop app reads or describe a token field in its add-server form.
  `codex mcp login` (OAuth) is not supported by this server.
  [Docs](https://developers.openai.com/codex/mcp).

The endpoint is:
```text
https://your-lancommander-server/api/Plugins/Mcp
```

For clients using the VS Code MCP configuration format:

```json
{
  "inputs": [
    {
      "id": "lancommander-token",
      "type": "promptString",
      "description": "LANCommander administrator bearer token",
      "password": true
    }
  ],
  "servers": {
    "lancommander": {
      "type": "http",
      "url": "https://your-lancommander-server/api/Plugins/Mcp",
      "headers": {
        "Authorization": "Bearer ${input:lancommander-token}"
      }
    }
  }
}
```

Obtain a bearer token through LANCommander's existing authentication flow with
an administrator account. This plugin does not issue tokens, implement OAuth
discovery, or change the server's authentication settings. Browser cookies work
only where the host accepts them; the supported remote-client setup is the bearer
header above. Use HTTPS beyond a trusted local development connection, and never
commit tokens to configuration files.

The server enforces the plugin's **administrator-only** policy on every MCP
request, including initialization and discovery. Ordinary authenticated users
cannot use the endpoint. The transport is stateless: each request gets its own
scope, so database-backed provider instances are not retained across sessions.
There is no stdio transport, legacy SSE endpoint, anonymous access or broad CORS
policy.
Browser requests with an `Origin` header must match the request's scheme, host
and port; malformed, opaque and cross-origin values receive HTTP 403. Non-browser
clients may omit `Origin`. Behind a reverse proxy, configure LANCommander's trusted
forwarded headers and allowed hosts correctly so that it sees the public origin.

## Tools

| Area | Tools |
| --- | --- |
| Games and add-ons | `list_games`, `get_game`, `create_game`, `update_game`, `delete_game` |
| Game relationships | `get_game_links`, `set_game_links` |
| Game versions | `list_game_versions`, `get_game_version`, `create_game_version`, `update_game_version`, `delete_game_version` |
| Taxonomy | `list_taxonomy`, `get_taxonomy`, `create_taxonomy`, `rename_taxonomy`, `delete_taxonomy` |
| Installable tools | `list_tools`, `get_tool`, `create_tool`, `update_tool`, `delete_tool` |
| Redistributables | `list_redistributables`, `get_redistributable`, `create_redistributable`, `update_redistributable`, `delete_redistributable` |
| Game server configuration | `list_servers`, `get_server`, `create_server`, `update_server`, `delete_server` |
| External metadata | `list_metadata_providers`, `search_metadata`, `get_metadata` |

List tools accept `offset` and `limit` (1-100, default 25; metadata search defaults
to 10). Catalog lists return `items`, `total`, `offset` and `limit`; optional
`search` is a case-insensitive substring match on title/name. Providers currently
load their full list before the plugin filters/pages it.

Create calls ignore supplied record ids and let LANCommander assign them.
**Update calls replace all writable fields**, matching the provider contracts:
read the record immediately before updating, preserve unrelated fields, and send
the entire record. There is no optimistic concurrency token; simultaneous edits
can overwrite one another. Setting links replaces the entire chosen relationship
kind, and an empty array removes all links of that kind.

### Safety and limitations

This is a write-capable administrative interface. Keep MCP client approval enabled
for mutations, especially deletions and game server configuration. Read-only and
destructive tool annotations are hints for clients, **not an authorization or
confirmation mechanism**. The plugin does not independently collect user approval.

- Deleting games, versions, tools or redistributables can permanently delete
  their archive/media files as specified in the tool descriptions.
- Creating a game version carries forward the latest version's child records
  and **publishes an existing latest draft**.
- Game server configuration includes executable and script paths and autostart:
  writes can affect subsequent process execution. Process start/stop, consoles,
  RCON passwords and HTTP file paths are not exposed.
- No script editing/execution, product keys, media upload, package import,
  archive download, custom field/external-id management or version-specific
  redistributable option management is exposed.
- Catalog descriptions and external metadata are untrusted content, not
  instructions for an agent.
- An MCP SDK failure or provider failure is reported as an error, not a
  successful empty result. Missing single records are explicit tool errors.

## Development

The integration tests use the published contracts, ASP.NET Core TestServer and
mock providers. They load the built plugin through LANCommander's isolated
`PluginLoadContext`, then exercise the real HTTP MCP pipeline, browser origin
guard and the same route-group authorization arrangement as LANCommander.
No live catalog is modified.
Run `node --test tests/setup.test.mjs` for the browser login helper's credential,
same-origin, HTTPS, error-handling and clipboard checks.

The shared repository centralizes the LANCommander NuGet dependency baseline.
Do not replace these NuGet references with project references: the published
contracts remain this plugin's compatibility boundary.

## License

MIT.
