---
id: module-plugin-agent-host
type: module-design
status: active
title: Agent host — shared agent plugin host code
parent: group-plugins-agents
tags: [plugins, host]
---

# Agent host

Host code that agent plugins share lives in `Tereditor.Plugins.Agent.Host`: Pi and omp share the download, version probe
and file writes; Codex and omp share the process poll. The host projects of these three plugins reference this module; none
depends on another plugin. It is a library, not a plugin: it has no contract, settings or stored state of its own.

## Boundary

Depends on the base and host entries of the plugin API only. No Avalonia, no app or `Tereditor.Host.*` project, no agent contract.
Every type is internal and opened to the three host halves and the checks.

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
- `ProcessSnapshot` is one reading of the process table through `ps` (nothing on Windows) and the reads of one process the
  plugins need: working directory, command line and the UI launch marker the plugin passes. A zombie is not a running
  process. The descendant search stops at depth 4 and never descends through another tereditor app (`Tereditor.UI` or
  `tereditor`), so an agent in a nested tereditor's terminal does not mark the outer tab.
- `AgentWatch` polls it every 2.5 s while a terminal exists and reports when a tab's shell gains, replaces or loses a
  descendant with the agent's name. Sweeps never overlap and arming never sweeps inline. A plugin that runs a downloaded
  copy on Bun also passes a test that says whether a `bun` process is that copy.

Not shared, on purpose: Pi's chat over RPC (omp has no chat), the configuration and context readers (Pi and omp discover files by
different rules), the Claude Code plugin's process poll (it also reads Windows, runs on a steppable timer and reports
changed workspaces) and the launch lines.

## Checks

`--pi` and `--omp` run `ManagedAgentChecks.Download` against a fixture `bun` for each pin. `--codex` and `--omp` cover
process rows, zombies, nested tereditor apps, agents started by agents and the managed Bun copy; `--agent-launches` reads
the UI launch marker from real processes and follows a replaced agent.
