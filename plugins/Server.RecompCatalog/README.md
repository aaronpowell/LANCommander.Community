# Server.RecompCatalog

A LANCommander server plugin that browses a Quiver-compatible application catalog and imports a
GitHub release asset into LANCommander as a game, via an LCX package and LANCommander's canonical
importer.

This project is independent of Quiver and is not affiliated with, maintained by, sponsored by,
or endorsed by Quiver or its maintainers. Quiver inspired the workflow, and its public community
catalog feed format and data are compatibility inputs only. The plugin does not bundle,
mirror, or depend on the Quiver application.

The source projects, namespaces, installable package ID, and owned assemblies
use `LANCommander.Community.Server.RecompCatalog.*`. The persistent plugin ID
remains `dev.aaronpowell.lancommander.recompcatalog`.

## Workflow

1. Load a configurable catalog index.
2. Browse GitHub-backed entries in the LANCommander server UI.
3. Resolve the latest GitHub release and select a Windows ZIP or EXE asset.
4. Look up and edit metadata.
5. Download, hash, and normalize the payload without executing it.
6. Select the primary executable.
7. Generate an LCX with deterministic game/archive IDs and source provenance, and import it.
8. Optionally attach a ROM you supply as a LANCommander redistributable (see [ROMs](#roms)).

ZIP normalization rejects rooted and traversal paths, limits download/expanded sizes, limits
entry counts and expansion ratios, and requires at least one executable. This is not a trust
guarantee: administrators must review upstream projects and release artifacts.

## Installation

The repository's development version is not a claim that a package has been
published. The former package ID was never published to NuGet.org; generated
packages now use the project-qualified ID above. See the repository's
[plugin release procedure](../../RELEASES.md)
for the current tag, workflow, and publication behavior. The plugin needs a
LANCommander server build that carries the plugin contracts described under
[Build](#build) and the [host load-context prerequisite](#host-load-context-prerequisite).

1. Download the NuGet package and its `.sha256` file from the
   [community releases page](https://github.com/aaronpowell/LANCommander.Community/releases), and
   verify the hash:

   ```powershell
   $package = 'LANCommander.Community.Server.RecompCatalog.Plugin.0.4.2.nupkg'
   $expected = (Get-Content "$package.sha256").Split(' ')[0]
   (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected
   ```

2. Open **Settings → Plugins** in LANCommander and upload the `.nupkg`.
3. Restart the server to activate the staged installation or update, and check that it logs
   `Loaded plugin 'Recomp Catalog' (dev.aaronpowell.lancommander.recompcatalog)`.

Imported games, their archives, and their Package scripts are stored by LANCommander, so they are
kept when you upgrade or remove the plugin.

### Building the package locally

`eng\Build-Plugin.ps1 -Plugin Server.RecompCatalog -Package` (PowerShell 7) packs the plugin in Release, verifies that only its four
private DLLs are under `lib/net10.0/`, checks the plugin package types and absence of NuGet
dependencies, and writes the `.nupkg` and `.sha256` to `artifacts`:

```powershell
pwsh .\eng\Build-Plugin.ps1 -Plugin Server.RecompCatalog -Package
```

The version comes from `plugins\Server.RecompCatalog\version.txt` and is shared
with its helper projects and runtime plugin version.

For testing, use a unique prerelease version and upload the resulting package in
**Settings → Plugins**. Update `plugins\Server.RecompCatalog\version.txt` and its
changelog before building a different test version; never reuse an artifact
version for different bytes.

Alternatively, add the absolute `artifacts` directory as a local package source in LANCommander's
Plugins page. Enable prereleases under **Sources** if the server is not itself a prerelease.
Increment the version for each test build so package caches and update detection see a new package.

Standard `dotnet pack` also produces the same package layout; the script adds validation and a
checksum. Host assemblies are compile-time references, not bundled dependencies.

```powershell
dotnet pack .\plugins\Server.RecompCatalog\LANCommander.Community.Server.RecompCatalog.Plugin\LANCommander.Community.Server.RecompCatalog.Plugin.csproj -c Release -o artifacts
```

Pull request CI validates all components. A component tag publishes this
package to the repository's GitHub Release and GitHub Packages NuGet feed.
NuGet.org publication is also automatic when the repository `NUGET_API_KEY`
secret is configured. See the repository's [plugin release procedure](../../RELEASES.md)
for eligibility, permissions, and retry behavior.

## Build

The plugin builds against the LANCommander NuGet packages `LANCommander.SDK`,
`LANCommander.Server.Plugins`, and `LANCommander.Server.UI`. Their shared version
is `LANCommanderPackageVersion` in the repository-root `Directory.Packages.props`
(`2.2.0-beta.4`).

The plugin needs the host contracts (`IMetadataLookup`, version redistributables, and bulk custom
field reads) originally added in `2.2.0-alpha.3`. This build targets
`2.2.0-beta.4` or later:

```powershell
dotnet build .\LANCommander.Community.slnx -c Release
dotnet test .\plugins\Server.RecompCatalog\LANCommander.Community.Server.RecompCatalog.Tests\LANCommander.Community.Server.RecompCatalog.Tests.csproj -c Release
```

## Server plugin

`LANCommander.Community.Server.RecompCatalog.Plugin` is the server-side plugin. It registers an
`IServerRouteAssemblyExtension` and an `IServerNavigationExtension`, and serves an
administrator-only catalog browser at `/Plugins/RecompCatalog`.

The browser loads a Quiver-compatible catalog index, maps its lists into a searchable view, and
supports deterministic tag, original-platform, and target-platform filters when those facets are
present in the feed. Selecting an entry shows its catalog provenance, repository, project, tags,
and platform information. "Check release and prepare" resolves the latest GitHub release and lists
selectable Windows ZIP/EXE assets without downloading them.

Catalog entries now have a canonical identity built from the configured feed, source list,
repository provider/path, and stable entry key. LANCommander games are matched by the
`RecompCatalog.Identity` custom field; packages produced by the plugin write that field plus
the exact `RecompCatalog.Release` identity and serialized `RecompCatalog.UpdatePolicy`. Existing
prototype packages that predate those fields
are detected conservatively by their deterministic game ID, but their exact imported release is
reported as unknown rather than guessed from a display name.

The browser distinguishes never imported, imported/current, update available, imported release
unknown, and state-check failure. Initial browsing only reads LANCommander game/custom-field/version
state and does not call release providers per entry. A release check is explicit and compares exact
provider, repository, tag, selected asset name, and asset URL identity. A difference is reported as
an available update without assuming arbitrary tags are SemVer or claiming which tag is newer.

### Re-import safety

The browser only imports **new games**. For each selected release asset, it shows one of these
outcomes before download:

- The exact provider/repository/tag/asset name/asset URL is already imported: preparation is
  disabled. A stale or concurrent request for that same release is a successful **no-op**, not
  another import; it does not build an LCX, call the importer, or change game/archive/metadata rows.
- The existing game has a different release or asset: an **update available** warning is shown,
  and replacement import is disabled. Use the existing game's LANCommander Package-script
  update workflow rather than importing another game or replacing its metadata.
- The imported release is unknown (including legacy packages), multiple games match the catalog
  identity, or host state cannot be verified: import is blocked with an explicit warning.

Existing-game replacement is deliberately unsupported here: a warning explains that re-import
could overwrite locally edited metadata. There is no force/overwrite button or implicit consent.
This conservatively protects all existing metadata, without claiming to detect individual local
edits or relying on importer merge behavior.

Immediately before package building, the plugin rereads LANCommander state inside a shared,
in-process import gate. Concurrent browser sessions on the same server cannot both pass the
new-game check. The canonical catalog identity matches games even when their local title or
catalog display name changes; deterministic legacy IDs remain a fallback. This is not a database
transaction or distributed lock: imports made outside this plugin or on another server process
are not serialized. End-to-end client installation, host merge behavior, and cross-process races
still require separate acceptance testing against a disposable LANCommander instance.

Run the focused, offline re-import acceptance tests (using the built host assemblies described above):

```powershell
dotnet test .\plugins\Server.RecompCatalog\LANCommander.Community.Server.RecompCatalog.Tests\LANCommander.Community.Server.RecompCatalog.Tests.csproj --filter "FullyQualifiedName~CatalogImportSafetyTests|FullyQualifiedName~CatalogBrowserTests|FullyQualifiedName~PluginServiceRegistrationTests" --nologo --verbosity quiet
```

These tests use in-memory host state and importer callbacks; they do not deploy the plugin or
connect to a live LANCommander database.

Selecting a release asset creates a reviewable import preparation containing immutable catalog and
release provenance plus the future Package-script policy: repository provider/path, release asset
filter, selected asset, archive format, and normalization behavior. Metadata lookup is then
explicitly invoked through LANCommander's configured `MetadataService`/`IMetadataProvider`
implementations. The operator can edit every imported metadata field, either from catalog-seeded
defaults or after explicitly applying a lookup result. Lookup never overwrites the draft
automatically, and a zero-result search displays an explicit empty state with guidance to shorten
the query or continue with manual entry. Catalog and release provenance remain read-only and
separate from the editable metadata draft.

After review, the plugin downloads the selected release asset into controlled temporary storage,
enforces the plugin's compressed-size, expanded-size, entry-count, expansion-ratio, path, and
symbolic-link safety checks, and normalizes ZIP or single-EXE releases without executing them. The
administrator explicitly chooses the primary executable when several candidates exist. The plugin
then builds an LCX carrying the edited metadata and immutable provenance, and streams it through
LANCommander's canonical import pipeline (`IPackageImporter`). Temporary downloads and LCX files are removed on
discard, successful import, component disposal, and failed preparation.

The generated LCX also includes a deterministic game `Package` script. LANCommander's existing
package schedule can run it to compare the newest archive version of the imported game with the
repository's latest GitHub release tag, select an asset using the saved filter/name/format policy,
and return `New-Package` only when a different release is available. The script validates that the
saved executable path still exists in the new payload before returning an update.

The plugin is independent of Quiver and does not bundle a catalog snapshot. Quiver inspired the
workflow, and compatible public feeds are treated as untrusted remote input: invalid URLs, HTTP
errors, malformed documents, empty feeds, and release lookup failures are displayed to the
administrator rather than silently replaced with fallback data.

### ROMs

Most recomps need a dump of the original game at runtime. The plugin never downloads, bundles, or
locates ROMs; the administrator supplies one they own. During import, the **ROM** card offers
**No ROM**, **Upload ROM**, or **Use stored ROM**. For a game that is already imported, select it
and use **Manage ROM** to attach, change, or remove its ROM without re-importing.

- **Upload:** the file is streamed from the browser (up to 512 MiB) into temporary storage.
  N64 dumps in v64 or n64 byte order are converted to z64 (big-endian); other formats are kept as
  is. The SHA-1 and SHA-256 are shown so you can compare them with the hashes the project expects.
  The plugin packages the ROM as a redistributable LCX and imports it through `IPackageImporter`. Its
  ID is derived from the SHA-256, so uploading the same dump again reuses the existing
  redistributable. Its name, `ROM: <label> [<sha256 prefix>]`, must be unique.
- **Stored:** pick an existing ROM redistributable that this plugin created (recognised by its
  notes), so one dump can serve several games.
- **Placement:** set the file name the recomp expects (for example `mm.n64.us.1.0.z64`) and the
  folder relative to the install directory. The folder defaults to the primary executable's
  folder, which is where portable N64 recomps keep their data. Rooted paths, `..` segments, and
  invalid or reserved Windows names are rejected.

The placement is stored as the redistributable's per-game options on the game's latest version,
and LANCommander carries the options forward to new versions created by package updates. A game
has at most one plugin ROM; redistributables you added by hand are left alone. Removing a ROM
from a game keeps the stored redistributable.

On each client, the redistributable's DetectInstall script reports it installed only when the
target file exists with the expected SHA-256. Otherwise the client downloads the ROM and the
Install script verifies its hash, checks that the target resolves inside the install directory,
and copies it there. Both scripts use `Get-RedistributableOptions` to read the game's placement.

If the game imports but the ROM step fails, the game is kept and the page explains that the ROM
was not attached; use **Manage ROM** to retry.

### Catalog configuration

The community catalog feed is used by default:

```text
https://raw.githubusercontent.com/tgeorgiadis/quiver-community-app-catalog/main/index.json
```

Override it in LANCommander configuration with:

```json
{
  "Plugins": {
    "RecompCatalog": {
      "FeedUrl": "https://catalog.example.test/index.json"
    }
  }
}
```

The URL must be absolute HTTPS. Set `Plugins:RecompCatalog:FeedUrl` to an empty value to show the
explicit unconfigured state.

GitHub release lookups use `GH_TOKEN` or `GITHUB_TOKEN` from the server's environment when
present. The token is attached only to `api.github.com` requests and is never written to disk or
included in LCX metadata. Public repositories require no token permissions; a fine-grained token
with public-repository access is sufficient. Unauthenticated public-repository lookup remains
supported at GitHub's lower rate limit. When the API rejects a token or exhausts a rate limit,
the plugin reports an actionable error and includes the reset time when GitHub provides one.

The import boundary is the host-provided `IPackageImporter.ImportStreamAsync`, which reuses the
same import context, queue preparation, archive storage, and persistence path as the server API.
The plugin uses only `LANCommander.Server.Plugins` contracts (`IPackageImporter`,
`IMetadataLookup`, `IGameProvider`, `IGameVersionProvider`, and `IRedistributableProvider`) and
never writes game/archive rows directly. The imported package carries the
catalog identity, exact release identity, and serialized update policy so later catalog refreshes
can classify existing games without title matching.

When replacing an earlier manual install, stop the server, remove the old
`Plugins\LANCommander.RecompCatalog.Plugin` folder, and install the new package
or complete output under `Plugins\LANCommander.Community.Server.RecompCatalog.Plugin`.
Imported games, archives, and package scripts live in LANCommander's data store,
not the plugin folder; keep the same plugin ID when replacing the plugin.

The host packages (`LANCommander.SDK`, `LANCommander.Server.Plugins`, and
`LANCommander.Server.UI`) are referenced with `ExcludeAssets="runtime"` and `PrivateAssets="all"`,
so they are compile-time only. Plugin output
contains the plugin plus its private
`LANCommander.Community.Server.RecompCatalog.Core`,
`LANCommander.Community.Server.RecompCatalog.Infrastructure`, and
`LANCommander.Community.Server.RecompCatalog.Providers`
assemblies, but no host or Radzen binaries. This is required: the server
must provide the already-loaded copies so contract types and Razor controls keep a single identity
across the plugin's isolated load context.

For development, build a versioned test package and install it through **Settings → Plugins** as
described under [Building the package locally](#building-the-package-locally). Restart the server
after installing so the plugin's assemblies are loaded.

Start the server and sign in as an administrator. The sidebar gains a "Recomp Catalog" entry, and
startup logs `Loaded plugin 'Recomp Catalog' (dev.aaronpowell.lancommander.recompcatalog)`. The
page at `/Plugins/RecompCatalog` renders its loading, failure, empty, filters, imported/update
states, details, release check, and lookup-assisted preparation with
`LANCommander.Server.UI.Controls`. Import then downloads and normalizes the selected asset, requires
an executable selection, builds an LCX, and submits it to LANCommander's canonical importer.

The navigation entry only controls visibility. Access is enforced by the server from the page's
`[PluginAccess(PluginAccessLevel.Administrator)]` declaration, and a plugin page that declares
nothing is administrator-only by default.

### Host load-context prerequisite

LANCommander's plugin load context treats `LANCommander.SDK`, `LANCommander.Server.Plugins`, and
`LANCommander.Server.UI` as shared host assemblies, so the plugin resolves the server's
already-loaded copies instead of loading second ones. The server must be LANCommander
`2.2.0-beta.4` or later; older 2.2.0 prereleases fail to activate this build.

## Known gaps

- Only GitHub releases and Windows `.zip`/`.exe` assets are supported.
- GitHub's latest release endpoint is the only release provider/strategy.
- Catalog icons and extra file acquisition are not embedded in the LCX.
- ROMs must be supplied by the administrator. The plugin cannot know each recomp's expected ROM
  file name or hash, so the administrator enters the file name and checks the hashes. ROM
  installs use Windows client scripts only.
- Initial launcher validation reached game startup after import/install, but LANCommander
  returned a 404 from `GameClient.GetAllocatedKeyAsync` while retrieving allocated keys. This
  is tracked as a separate LANCommander launcher/server issue rather than a plugin
  import/package failure.
- The operator must assess upstream licensing and supply-chain trust.
- LCX timestamps are assigned by LANCommander packaging APIs, so IDs are deterministic but the
  package is not byte-for-byte reproducible.

## License

[MIT](LICENSE)
