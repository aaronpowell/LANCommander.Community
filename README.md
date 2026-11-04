# LANCommander.Community

Community plugins for LANCommander Server and Launcher. The repository shares
build and validation infrastructure while maintaining independent plugin
projects and releases.

## Plugins

- [Server.Mcp](plugins/Server.Mcp/README.md)
- [Server.RecompCatalog](plugins/Server.RecompCatalog/README.md)
- [Server.Quake3](plugins/Server.Quake3/README.md)
- [Launcher.MQTT](plugins/Launcher.MQTT/README.md)

Each child README is the canonical guide for its plugin's requirements,
compatibility, setup, installation, behavior, and limitations.

## Build and validate

Install the .NET 10 SDK and PowerShell 7. Additional prerequisites for an
individual plugin, including any required source materialization, are
documented in its README. Consult those instructions before building the full
solution.

```powershell
dotnet build .\LANCommander.Community.slnx -c Release
```

Run the repository checks:

```powershell
pwsh .\eng\Test-ReleaseSelection.ps1
pwsh .\eng\Test-Repository.ps1
pwsh .\eng\Test-ReleaseDestinations.ps1
pwsh .\eng\Test-NuGetPublication.ps1
pwsh .\eng\Test-GitHubPackages.ps1
```

Validate a single plugin and create its local install artifact with
`eng/Validate-Plugin.ps1`, selecting a component from the registry:

```powershell
pwsh .\eng\Validate-Plugin.ps1 -Plugin <Server.Mcp|Server.RecompCatalog|Launcher.MQTT|Server.Quake3> -OutputDirectory <new-output-directory>
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
