# Changelog

## 0.2.1

Package the launcher as `LANCommander.Community.Launcher.MQTT.Plugin`
NuGet with `LANCommander.Plugin` and `LANCommander.Plugin.Launcher` types.
Build and validate the private payload once, then use the same release artifact
for GitHub Packages and optional NuGet.org publication. Retain the verified
LANCommander beta.4 source pin and exact host compatibility range. Launch only
the single `ActionType.Primary` action, retaining the exactly-one-primary safety
check.

Store writable settings and the discovery journal in the host-preserved `Data`
directory. Existing ZIP users must back up and move both files into `Data`
**before** installing through the NuGet updater; see the README. A manual
drop-in update can migrate legacy root files at startup, rejecting conflicts.
The published 0.2.0 ZIP and its tag remain immutable.

## 0.2.0

Prototype imported from the existing launcher worktree. This was
distributed as a drop-in ZIP, not a NuGet package. Aligns assembly names and
the staged install folder with project names while preserving plugin/config/
MQTT/HA entity identities. Targets LANCommander 2.2.0-beta.3 using its pinned
released source, with matching runtime compatibility metadata and
host-loader/package validation. The ZIP is not a NuGet artifact.
