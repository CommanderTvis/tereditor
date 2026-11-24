---
id: module-plugin-runtimes
type: module-design
status: active
title: plugin-runtimes — Bun, uv and a Temurin JDK for the plugins that run packages or Java servers, found first, downloaded only when needed
parent: group-plugins-workbench
depends-on: [module-plugin-api]
tags: [v1, plugins]
---

## Responsibility

Owns "Runtimes for tooling": the one place that decides where Bun (`bunx`), uv (`uvx`) and a Temurin JDK 21 come from for the plugins that start packages or Java-based language servers, and the
switch that lets tereditor download them. Builtin, on by default (a fresh install works), never mandatory, no dependencies, wire version 1, category
Environment, icon `settings-3-line`, runs on every host with processes (not the Playground), also headless. Node is not provided.

The pins, the download and the resolution are the host's (`ManagedRuntimes`, `ArtifactInstaller`, `LanguageServers`; [Language SPEC](../Tereditor.Host.Core/Language/SPEC.md)).
The plugin is the policy: its on/off switch allows or forbids the download, its settings hold the user's pinned paths, its page shows the state, and turning it off
cleans up.

## Resolution

A client plugin calls `IPluginHostContext.ResolveRuntimeAsync(kind, workspace)`. The order is fixed:

1. The path the user pinned (this plugin's `BunPath` or `UvPath`, or the host tool path of Settings › Code Intelligence). A pin that cannot run is reported, not replaced.
2. The copy this plugin downloaded, whenever one exists and passes its check (plugin on). Once downloaded it stays preferred until the user presses Remove download; an install on the computer never outranks it.
3. An install on the hardened PATH (absolute entries, never inside the workspace). Skipped when `toolchains.source = managed`, the container's choice of host-owned copies only.
4. Nothing found and the plugin on: it is downloaded now (pinned version, allow-listed hosts, atomic). Never at startup; the first request does it. Offline, or on a platform with no pin, the answer says so.

Safety. Every downloaded archive has a SHA-256 pinned in the source next to its version and URL (`ManagedRuntimes`: Bun 1.4.4 on six platforms, uv 0.13.9 on six, Temurin 21.0.13 on five; Temurin has no Windows arm64 build), checked before anything is extracted. After extraction the installer records the program's own SHA-256, size and modified time in `.tereditor-verified.json`. Each time the copy is resolved the record must name the pinned hash; if the size and time still match, the copy is trusted without reading it, otherwise the program is hashed again and must equal the record (the record is rewritten with the new time when it does). A mismatch, a missing record, a missing or linked program: the copy is not used, the row shows a red line saying so with a Remove download hint, and resolution falls back to the computer's copy (if any).

With the plugin off, step 2 and step 4 are closed: the answer is unavailable, `OfferEnable` is true and the message names the tool and where to turn the plugin on. A pinned or PATH install
still works with the plugin off. The result carries the command, the arguments that go before the package (`x` for Bun, `tool run` for uv), the environment and the source.

The environment is the guard. Bun gets `BUN_INSTALL` in the cache, `BUN_OPTIONS` with an empty config and `--no-env-file`. uv gets `UV_NO_CONFIG=1` and
`UV_NO_ENV_FILE=1`, so `uv.toml`, `pyproject.toml` and `.env` in the workspace cannot choose an index or run anything. A downloaded uv keeps its cache, tools and Pythons in `uv-home`.
`BunPathAsync` stays as the path of the same answer.

The JDK (`RuntimeKind.Jdk`, pinned path `JdkPath`, the host tool `java`) follows the same rules with two differences. It only runs the Java-based servers, jdtls (21 or newer) and Metals (11, 17 or 21); it is never the project's JDK, which stays what the project and the server's `java.home` say, and it never replaces a JDK the user set. A `JAVA_HOME` or PATH JDK counts only when its `release` file shows a version the server accepts; an older or unreadable PATH one is passed over when the JDK can be downloaded, and kept when it cannot (plugin off, offline). The answer has no arguments and no environment; a server launched on it gets `JAVA_HOME` and PATH from the host. One Temurin 21 per platform (macOS arm64 and x64, Linux x64 and arm64, Windows x64) is pinned from the Adoptium API's published SHA-256.

Clients: the Java and Scala language servers (`Needs a JDK 21 or newer.` and the offer) and the language servers that run on Bun (TypeScript, Python, PHP, CSS, Shell: they say `Needs Bun.` and the offer) and the ACP plugin (npx entries on Bun, uvx entries on uv) and the Pi plugin (its pinned Pi, installed and run on Bun; [Pi SPEC](../Tereditor.Plugins.Pi/SPEC.md)).

## Turning it off

When the user turns the plugin off (not when tereditor quits), the host stops every language server that runs on Bun or needs a JDK and every process a plugin reported with
`TrackRuntimeProcess` that started from the downloaded copy, then deletes `runtimes/bun`, `runtimes/uv`, `runtimes/jdk`, `bun-home` and `uv-home`. The pinned paths stay in the settings. Programs the
user pinned or that are on PATH are not stopped or deleted. The same convention applies to the plugins that download: ACP removes `~/.tereditor/acp/{agents,registry,bun}`,
each language server plugin removes `servers/<id>`, `cache/<id>` and `workspaces/*/<id>` of the host's language directory. Not covered yet: the shared NuGet,
Go, gem and Coursier caches of the C#, Go, Ruby and Scala servers are the language's own caches, not per-plugin downloads.

## Settings page

Settings › Runtimes for tooling shows each runtime (Bun, uv, Temurin JDK 21): the source in use ("In use: the downloaded copy, version X (verified).", the program you chose, or the one found on your computer) with its version, an "Also found on your computer: path, version X. It is not used." line, an "Also downloaded" line when the copy is not in use, a red line when the downloaded copy failed its check, a field for the path you want to use, Download now, Remove download, and the disk space of the downloaded files. Download now turns into a disabled "Downloading…" and the row shows a line, a bar and Cancel: percent and both sizes with a determinate bar when the server sent the size, the bytes so far with an indeterminate bar when it did not. The progress area is always laid out, so the row does not change height; when the download ends the line says "Bun is downloaded.", "The download was canceled." or the error. The numbers are the host's own count of the bytes it received (`RuntimeStatus.Downloading`, `ReceivedBytes`, `TotalBytes`, from the archive download in `ArtifactInstaller`); the page reads them with the `status` request once a second, and the host serves them off the UI thread; Stop using my path clears the path you set (so the downloaded copy, then the computer's, apply). Where Runtimes is off and a runtime blocks something, a Turn it on button (chat banner of a failed ACP start; language server dependency row) turns the plugin on; the chat then downloads with Cancel and restarts the agent (`IPluginUIContext.TurnOnRuntimeAsync`), the server row starts the server again. Text is short and plain.
The page asks the host (`status`, `download`, `remove`); starting a download is the only network use.

## Checks

`--runtimes` (`tests/Tereditor.Checks/RuntimesChecks.cs`, fake downloads, no network, POSIX): the order pinned, PATH, downloaded; the JDK: five shipped pins, a user JDK used when its version suits (jdtls 21, Metals 11) and passed over when older, one download and `JAVA_HOME` for both servers, a JDK the user set kept, plugin off; a broken pin; one download per runtime; the off-state message and no request;
the Bun and uv guards; quitting deletes nothing; a language server plugin turned off removes only its downloads; Runtimes for tooling turned off stops the downloaded copy's processes (not a system one's), deletes everything and keeps the pin; ACP likewise.
`--runtimes-ui` (`RuntimesUiChecks.cs`, headless, fake host) renders the page: not installed, start, midway (bytes only, then percent and sizes), canceled, failed, done, with the row height checked at each step, the downloaded copy in use beside a computer install (both lines), a path of the user's with the downloaded copy beside it (Stop using my path), and a copy that failed its check (red line, Remove download on). `--runtimes` also checks that a corrupted download is refused and falls back, that the verified record is invalidated when the program changes, and that every pin has a 64-hex SHA-256 for each supported platform. Off has no page: a plugin that is off registers no section. `--runtimes` also covers the consent question (below) and `--claude-chat-ui` and `--lsp-dependencies` the Turn it on buttons.

## Consent

When Runtimes is on and a plugin's request (a language server or an ACP agent) needs a runtime that is not pinned, not on PATH and not downloaded, the host does not download at once. This plugin asks first:
every client shows one prompt (the host sends the list of waiting questions on the `consent` channel, and an answer from any client takes the prompt down in all) with the runtime's name and pinned version, Download and Not now.
The size is not shown: the pins hold none. Requests that arrive while the question is open share it. Not now is remembered until tereditor restarts or the user presses Download now; nothing is stored on disk, and a runtime is not asked about twice in a session.
A host without a screen (headless) does not ask and downloads as before. Download now on the page is the user's own request and never asks. A prompt that is dismissed without an answer keeps the request waiting; stopping the server cancels it.
