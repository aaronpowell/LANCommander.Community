# Contributing

Use the component-focused project and namespace hierarchy:

- `LANCommander.Community.Server.Mcp`
- `LANCommander.Community.Server.RecompCatalog.*`
- `LANCommander.Community.Server.Quake3` and its `.Core` / test projects
- `LANCommander.Community.Launcher.MQTT.*`

Keep the historical deployed package/assembly/plugin/configuration identities
stable unless a separately reviewed migration is explicitly authorized. Keep
Home Assistant discovery as the HA-specific integration layer; Launcher.MQTT's
command/state protocol is usable by other MQTT clients too.

Before submitting changes, run the repository checks and the validation
command(s) for affected components from the root [README](README.md#build-and-validate).
See [Plugin releases](RELEASES.md) for versioning, packaging, and release
procedures. Repository-local agent guidance is under
[`.github/skills`](.github/skills/): plugin maintenance,
[release preparation](.github/skills/plugin-release/SKILL.md), and
[authoring more skills](.github/skills/repository-skill-authoring/SKILL.md).

The [Discord webhook groundwork](plugins/Server.DiscordWebhook/README.md) is
non-packable while its subscription-action host integration is blocked. Run its
focused `dotnet test` command from that README; do not add it to release
selection or claim it is an installable plugin before integration is verified.

Do not create or push release tags, publish releases, or change deployed
identities without explicit maintainer authorization.
