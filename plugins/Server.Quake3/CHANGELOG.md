# Changelog

## 0.1.0-alpha.1

- Add native administrator-only Quake 3 server selection, status, bounded
  match configuration, rotations, presets and match/map actions.
- Use server-side RCON credentials and host-routed files; Docker remains
  responsible for hosting, lifecycle, mounts and consoles.
- Add durable per-server plugin state and cached PK3/loose-map discovery with
  explicit refresh and stale/unavailable results.
- Add independent registry, core/host tests and package validation.
- Development-only: requires unreleased host file-access contracts.
- Validate against actual upstream host packages and emitted package loading;
  journal startup write intent and live acknowledgement separately so partial
  external writes remain visible after plugin restart.
