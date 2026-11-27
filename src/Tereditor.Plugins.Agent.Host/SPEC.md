---
id: module-plugin-agent-host
type: module-design
status: active
title: Agent host — shared Pi and omp host code
parent: group-plugins-agents
tags: [plugins, host]
---

# Agent host

Host code that the Pi and omp plugins share lives in `Tereditor.Plugins.Agent.Host`. Both plugin host projects reference this
module; neither depends on the other plugin. It is a library, not a plugin: it has no contract, settings or state of its own.

## Boundary

Depends on the base and host entries of the plugin API only. No Avalonia, no app or `Tereditor.Host.*` project, no agent contract.
Every type is internal and opened to the two host halves and the checks.

- `ManagedBunPackage` is a pinned agent that tereditor downloads: one npm package at one exact version with its
  `dist.integrity`, installed with `bun install --ignore-scripts --no-progress --registry=https://registry.npmjs.org/` in a
  temporary folder next to `<root>/<version>`. The copy counts as installed only when Bun's lock file named the pinned integrity.
  One download runs at a time; later requests share it. Cancel kills Bun and leaves no partial folder; a folder of an earlier pin is
  deleted after an update; Remove deletes what the download made. Bun comes from the resolver the plugin passes. The plugin adds
  its pin, its folder and how it runs the copy (Pi: the chat's command; omp: the `omp` program it writes after a download).
- Nothing is downloaded without a user click. Only `EnsureAsync` downloads, and only `ManagedDownloads` calls it, for the
  plugin's install request, which the UI sends from the Download button alone.
- `ManagedDownloads` serves a plugin's install, cancel and remove requests, each answering with the plugin's status, and deletes the
  download when the user turns the plugin off.
- `AgentVersion` runs `<command> --version` with a 10 s bound and words the failures.
- `OwnedFile` writes a file tereditor owns whole and atomically, only when its text changes.

Not shared, on purpose: Pi's chat over RPC (omp has no chat), the configuration and context readers (Pi and omp discover files by
different rules), the process poll (omp's follows the Codex plugin's) and the launch lines.

## Checks

`--pi` and `--omp` run `ManagedAgentChecks.Download` against a fixture `bun` for each pin.
