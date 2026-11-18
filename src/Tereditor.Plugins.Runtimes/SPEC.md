---
id: module-plugin-runtimes
type: module-design
status: active
title: plugin-runtimes — Bun and uv for the plugins that run packages, found first, downloaded only when needed
parent: group-plugins-workbench
depends-on: [module-plugin-api]
tags: [v1, plugins]
---

## Responsibility

Owns "Runtimes for tooling": the one place that decides where Bun (`bunx`) and uv (`uvx`) come from for the plugins that start packages, and the
switch that lets tereditor download them. Builtin, on by default (a fresh install works), never mandatory, no dependencies, wire version 1, category
Environment, icon `settings-3-line`, runs on every host with processes (not the Playground), also headless. Node is not provided.

The pins, the download and the resolution are the host's (`ManagedRuntimes`, `ArtifactInstaller`, `LanguageServers`; [Language SPEC](../Tereditor.Host.Core/Language/SPEC.md)).
The plugin is the policy: its on/off switch allows or forbids the download, its settings hold the user's pinned paths, its page shows the state, and turning it off
cleans up.

## Resolution

A client plugin calls `IPluginHostContext.ResolveRuntimeAsync(kind, workspace)`. The order is fixed:

1. The path the user pinned (this plugin's `BunPath` or `UvPath`, or the host tool path of Settings › Code Intelligence). A pin that cannot run is reported, not replaced.
2. An install on the hardened PATH (absolute entries, never inside the workspace). Skipped when `toolchains.source = managed`, the container's choice of host-owned copies only.
3. The copy this plugin downloaded. When none exists and the plugin is on, it is downloaded now: pinned version, SHA-256 checked before extraction, allow-listed hosts, atomic. Never at startup; the first request does it. Offline, or on a platform with no pin, the answer says so.

With the plugin off, step 3 is closed: the answer is unavailable, `OfferEnable` is true and the message names the tool and where to turn the plugin on. A pinned or PATH install
still works with the plugin off. The result carries the command, the arguments that go before the package (`x` for Bun, `tool run` for uv), the environment and the source.

The environment is the guard. Bun gets `BUN_INSTALL` in the cache, `BUN_OPTIONS` with an empty config and `--no-env-file`. uv gets `UV_NO_CONFIG=1` and
`UV_NO_ENV_FILE=1`, so `uv.toml`, `pyproject.toml` and `.env` in the workspace cannot choose an index or run anything. A downloaded uv keeps its cache, tools and Pythons in `uv-home`.
`BunPathAsync` stays as the path of the same answer.

Clients: the language servers that run on Bun (TypeScript, Python, PHP, CSS, Shell: they say `Needs Bun.` and the offer) and the ACP plugin (npx entries on Bun, uvx entries on uv).

## Turning it off

When the user turns the plugin off (not when tereditor quits), the host stops every language server that runs on Bun and every process a plugin reported with
`TrackRuntimeProcess` that started from the downloaded copy, then deletes `runtimes/bun`, `runtimes/uv`, `bun-home` and `uv-home`. The pinned paths stay in the settings. Programs the
user pinned or that are on PATH are not stopped or deleted. The same convention applies to the plugins that download: ACP removes `~/.tereditor/acp/{agents,registry,bun}`,
each language server plugin removes `servers/<id>`, `cache/<id>` and `workspaces/*/<id>` of the host's language directory. Not covered yet: the shared NuGet,
Go, gem and Coursier caches of the C#, Go, Ruby and Scala servers are the language's own caches, not per-plugin downloads.

## Settings page

Settings › Runtimes for tooling shows each runtime (Bun, uv): the source in use (the program you chose, the one found on your computer, or the copy tereditor
downloaded) with its version, a field for the path you want to use, Download now, Remove download, and the disk space of the downloaded files. A download shows a strip with the bytes so far and Cancel; when another source is in use the row says the download is used only when none is found on your computer, and Use the download pins its path (the same setting as the path field), Use the one on my computer clears it. The order of precedence is unchanged. Where Runtimes is off and a runtime blocks something, a Turn it on button (chat banner of a failed ACP start; language server dependency row) turns the plugin on; the chat then downloads with Cancel and restarts the agent (`IPluginUIContext.TurnOnRuntimeAsync`), the server row starts the server again. Text is short and plain.
The page asks the host (`status`, `download`, `remove`); starting a download is the only network use.

## Checks

`--runtimes` (`tests/Tereditor.Checks/RuntimesChecks.cs`, fake downloads, no network, POSIX): the order pinned, PATH, downloaded; a broken pin; one download per runtime; the off-state message and no request;
the Bun and uv guards; quitting deletes nothing; a language server plugin turned off removes only its downloads; Runtimes for tooling turned off stops the downloaded copy's processes (not a system one's), deletes everything and keeps the pin; ACP likewise.
`--runtimes-ui` (`RuntimesUiChecks.cs`, headless, fake host) renders the page: not installed, downloading (size so far, Cancel, buttons waiting), cancelled, failed, installed, and a download that a system install outranks (it says so and offers Use the download / Use the one on my computer). Off has no page: a plugin that is off registers no section. Not done: real percent (the host reports no total, so the page shows bytes so far). `--runtimes` also covers the consent question (below) and `--claude-chat-ui` and `--lsp-dependencies` the Turn it on buttons.

## Consent

When Runtimes is on and a plugin's request (a language server or an ACP agent) needs a runtime that is not pinned, not on PATH and not downloaded, the host does not download at once. This plugin asks first:
every client shows one prompt (the host sends the list of waiting questions on the `consent` channel, and an answer from any client takes the prompt down in all) with the runtime's name and pinned version, Download and Not now.
The size is not shown: the pins hold none. Requests that arrive while the question is open share it. Not now is remembered until tereditor restarts or the user presses Download now; nothing is stored on disk, and a runtime is not asked about twice in a session.
A host without a screen (headless) does not ask and downloads as before. Download now on the page is the user's own request and never asks. A prompt that is dismissed without an answer keeps the request waiting; stopping the server cancels it.
