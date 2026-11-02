# LANCommander.Community

Community plugins for LANCommander Server and Launcher. The repository shares
build and validation infrastructure while maintaining independent plugin
projects and releases.

## Plugins

- [Server.Mcp](plugins/Server.Mcp/README.md) — Model Context Protocol access to
  LANCommander catalog management.
- [Server.RecompCatalog](plugins/Server.RecompCatalog/README.md) — browse a
  compatible catalog and prepare game imports.
- [Launcher.MQTT](plugins/Launcher.MQTT/README.md) — MQTT command and state
  integration for the LANCommander GUI Launcher, including Home Assistant
  discovery.

Each child README is the canonical guide for its plugin's requirements,
compatibility, setup, installation, behavior, and limitations.

## Build and validate

Install the .NET 10 SDK and PowerShell 7. Additional prerequisites for an
individual plugin are documented in its README. From the repository root, build
all projects with. The Launcher.MQTT project also requires its pinned host
source; see its [build instructions](plugins/Launcher.MQTT/README.md#configure-and-build-the-source-pinned-plugin)
before running a full solution build.

```powershell
dotnet build .\LANCommander.Community.slnx -c Release
```

Run the repository checks:

```powershell
pwsh .\eng\Test-ReleaseSelection.ps1
pwsh .\eng\Test-Repository.ps1
```

Validate a single plugin and create its local install artifact with
`eng/Validate-Plugin.ps1`, selecting a component from the registry:

```powershell
pwsh .\eng\Validate-Plugin.ps1 -Plugin <Server.Mcp|Server.RecompCatalog|Launcher.MQTT> -OutputDirectory <new-output-directory>
```

The output directory must not already exist. See each plugin README for its
focused build, test, and installation instructions.

## Repository guidance

- [Contributing](CONTRIBUTING.md) — shared project conventions and contributor
  checks.
- [Plugin release procedure](RELEASES.md) — versioning, validation, and
  component release workflow.
- [Repository agent skills](.github/skills/) — focused `SKILL.md` guidance for
  plugin maintenance, release preparation, and authoring more skills.

The community repository is independent and is not an official LANCommander
release channel.
