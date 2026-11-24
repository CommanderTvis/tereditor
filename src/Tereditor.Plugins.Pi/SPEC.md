---
id: module-plugin-pi
type: module-design
status: active
title: Pi builtin
parent: group-plugins-agents
---

# Pi builtin

Early support for the Pi coding agent (https://github.com/badlogic/pi-mono), shown as a chat tab in the shared chat view
(`src/Tereditor.Plugins.Chat/SPEC.md`). Disabled by default; the manifest declares `Support = early`. Pi runs as a child
process of the host, so a browser tab reaches it only through a host. By default tereditor installs nothing:
it runs the `pi` the host finds on PATH (or the configured command). Settings › Pi can choose the pinned Pi instead (below).

Facts below come from Pi's RPC documentation (`docs/rpc.md`, `docs/providers.md`, `docs/session-format.md` in `@earendil-works/pi-coding-agent` 1.6.0, read from the managed copy at `~/.tereditor/pi/managed/1.6.0`: `docs/rpc-commands.md` for the commands, `docs/configuration.md`, `docs/skills.md`, `docs/packages.md`, `docs/security.md` and `docs/mcp.md` for what a session loads). They are verified by the checks against a fixture `pi` that speaks the protocol, not against a real Pi run.

## Acceptance contract

- Process: a chat's child is `pi --mode rpc [--session <file>] [--model <pattern>] [--tools +codemode]` in the workspace directory, with the host's
  environment. Commands go to stdin and events and responses come back on stdout as LF-delimited JSON; stdout is read by line (never split on
  Unicode separators). Closing stdin, then killing the process tree, ends it. Stderr is kept (the last lines) to explain an exit.
- Start: `get_state` (model, thinking level, session file and id, name), then `set_thinking_level` when one was chosen, `get_available_models`
  and `get_commands`; a resumed session then `get_messages`, replayed into the same items a live turn folds to. A missing `pi` fails the chat
  with an install hint; each response has a 30 s bound.
- Folding (`PiFolder`): `agent_start`/`agent_end` are Running/Idle; a user `message_start` is the prompt (it releases a message the host showed
  as queued); assistant text and thinking blocks are items keyed by message and content index and grow with their deltas; a tool call is an item
  keyed by Pi's tool call id, Pending at `toolcall_end`, Running through `tool_execution_*` (the update's accumulated output replaces the last) and
  Done or Failed at the end; output over `ChatContract.OutputLimit` is cut and served whole as item detail; `edit` and `write` carry the file path.
  Usage and cost accumulate in the header; compaction shows as the header's activity; retries and extension errors become notices.
- Sending while a turn runs sends `prompt` with `streamingBehavior: steer` and shows the message queued until Pi echoes it. Stop is `abort`. The
  model (`provider/id`) and thinking level changes are confirmed by Pi before the header shows them.
- Not offered: permissions and questions (Pi has none; the one permission mode says its tools run freely), rewind, fork, queue editing and
  background tasks (their methods answer an error). An extension dialog (`select`, `confirm`, `input`, `editor`) is answered as cancelled with a
  notice so Pi never waits; `notify` becomes a notice.
- Sessions: Pi stores sessions under `~/.pi/agent/sessions/--<path with / as ->--/` (or `PI_CODING_AGENT_DIR`). The sessions list shows that
  directory's files newest first, titled by the session's name or first prompt; opening one resumes it with `--session`. The host remembers each
  chat's session file so reopening its tab continues it.
- Launcher: "Pi" in the agent launchers always opens a chat tab (there is no terminal mode yet); Settings › Pi has the Pi source, the command, a default model, a thinking level, a Codemode switch and the version read from `pi --version`. Model providers and keys are managed only in Settings › Plugins › Model providers; Pi has no provider or key page of its own.

Settings › Pi shows the Model source control from Model providers as its first setting, above everything else (Plugin API `UiSlots.ModelSourceView`): the agent's own provider (default) or a provider the user added that speaks an API Pi takes. With a provider chosen, Model providers writes Pi's `models.json` in tereditor's own agent directory (`<state>/providers/pi/agent`) with the key as a `!command` for the `provider-key` helper, and the chat's `pi --mode rpc` child runs with `PI_CODING_AGENT_DIR` set to that directory (an agent launch contribution; sessions are listed from it). The key is never in an argument or the environment, and `auth.json` is never written. With none chosen or the plugin off, the child starts as before. With that plugin off the page shows one hint line and nothing else changes. With Air Gateway on, JetBrains (Air Gateway) is one more choice of that control (Central declares wiring for Pi): the chat then starts `central run pi -- --mode rpc …` (a launch contribution that replaces the executable, applied only to the plain `pi` command; a custom command is never rewritten), and a contribution blocked with a reason stops the chat from starting. Pi does not depend on Model providers; its manifest declares `agentRouting` (Anthropic, OpenAI Responses and OpenAI Chat through its models file).

## Pi source

`PiSettings.Source` is `system` (default; nothing changes for a setup that never touches it) or `managed`. Managed runs the pinned Pi that tereditor downloads, and never falls back to the system one.

- Pin: `PiManaged` holds the exact version of `@earendil-works/pi-coding-agent` (1.6.0) next to the npm registry's `dist.integrity` of that release. There is no update-pins task for it: change both constants together, from `https://registry.npmjs.org/@earendil-works/pi-coding-agent/<version>`.
- Download: the host runs `bun install --ignore-scripts --no-progress --registry=https://registry.npmjs.org/` in a temporary folder next to the final one, with a `package.json` that names the exact version. Bun comes from Runtimes for tooling (`ResolveRuntimeAsync(Bun)`: the user's pin, the downloaded copy, then PATH, downloaded with the usual consent); its answer's environment (Bun's cache in the host's cache, no config or `.env` from the working directory) goes to the install and the launch. After it Bun's `bun.lock` must contain the pinned integrity, else the copy is deleted and the download fails with a sentence. The folder is then renamed to `~/.tereditor/pi/managed/<version>` with a record of the pin; a folder of an earlier pin is deleted after the update. One download runs at a time: the settings page and a chat that needs the copy share it. Cancel kills Bun and leaves no partial folder. Nothing is downloaded until the user presses Download or opens a chat with the managed source.
- Launch: `bun --bun <package>/dist/bundle/cli.js --mode rpc …`, the equivalent of `bunx --bun @earendil-works/pi-coding-agent@<version>` from the installed copy, in the workspace directory. The process is reported to Runtimes (`TrackRuntimeProcess`), so turning Runtimes for tooling off stops it before Bun is deleted. A Model source that starts Pi through another program (`central run pi --`) needs the system Pi: with the managed source it blocks the start with that reason. A model source's environment (the agent directory) still applies. A failed download or a missing Bun fails the chat with the reason; the chat does not run any other Pi.
- Page: the Pi source choice (System command, Managed pinned) comes before Command; Managed hides Command and shows the pinned version, "downloaded", "not downloaded yet" or "an earlier version is downloaded", Download or Update, Remove download, and while a download runs a line, a bar with no end (Bun reports no sizes) and Cancel. The page asks the host for the state each half second while a download runs, so it also shows one a chat started. A failure stays on the page as a red line.
- Off: turning the plugin off deletes `~/.tereditor/pi/managed`.
- omp has its own plugin and launch code; it does not share this.

## Pi pane

The Pi side pane (`pane`) follows Claude Code's configuration pane (`src/Tereditor.Plugins.ClaudeCode/UI/ClaudeConfigPanel.cs`): the same kit,
scope chips and hints, with the tabs Context, Settings, Capabilities and Account. `PiConfigPanel` only reads through the host (`PiContract.Config`,
`PiConfigReader`), and it changes one thing: packages, with `pi install` and `pi remove` (`PiPackages`), started the way a chat starts Pi (the chosen
Pi, the managed copy on Bun, or a Model source's wrapper), in the workspace directory, with stdin closed.

- Context: the context file of each directory, the agent directory first, then the working directory's ancestors from the top down. A
  directory has one: the first of AGENTS.override.md, AGENTS.md, AGENTS.MD, CLAUDE.md, CLAUDE.MD that exists. Extensions (`extensions/`), skills
  (`skills/` with a SKILL.md, `~/.agents/skills`, `.pi/skills`, and `.agents/skills` from the working directory up to the repository root),
  prompt templates (`prompts/*.md`) and MCP servers (`mcp.json`) come from the agent directory (USER) and from `.pi` in the working
  directory (PROJECT). Project resources are listed as not loaded until the project is trusted; context files load regardless.
- Trust: the closest decision in `trust.json` of the agent directory for the working directory or a parent. It is shown only; the pane points to
  `/trust` and never writes `trust.json`.
- Settings: the keys of both settings.json files, read-only, and the packages of both, each with Remove. Install takes a source, and Project
  settings adds `-l`. After a change the pane says to restart the chat; it never sends `/reload`.
- Capabilities: the commands the workspace's open Pi chat reports (`get_commands`), with where each comes from. Without an open chat it says so.
- Account: the Pi version, the early-support badge and the project's trust state. The chat header shows the model and thinking level.
- Scope chips read USER and PROJECT (`-l` maps to PROJECT). Click a row to open its file.
- Not offered: turning single resources on or off, changing trust, and `/reload`.

## Not yet

The browser-tab launch (`PiLinks.InTab`) does not pass the codemode flag yet. A terminal launch of Pi, permission-style gating, rewind and fork through Pi's `fork` and session tree, extension dialogs, MCP and tereditor's
tools for Pi, and running Pi inside a browser tab.

## Checks

`--pi` (gate case `pi`): the manifest, the folding of recorded events and stored messages, a session against a fixture `pi` (start, state, models,
commands, a streamed turn, model and thinking changes, close, a missing binary, the version probe) and the sessions list, a launch contribution that replaces the executable or blocks the start, and codemode: a fresh setting is on, and the launch carries `--tools +codemode` (the managed launch too). Codemode needs Pi 1.1.0 or later (the `+name` form of `--tools`); the system command is not probed for it. The pinned Pi runs against a fixture `bun`, with no network: a download whose lock file lacks the pinned integrity is refused and leaves nothing, cancel leaves nothing, two requests share one download, the install arguments, folder and environment, an earlier copy replaced, no second download, the launch as `bun --bun cli.js --mode rpc …` with the process tracked, a blocked launch passed on, a missing Bun failing the chat, Remove, and the system source unchanged. The pane's reads (`PiChecks.Config`: a fixture home and project, the trust gate, the nearest trust decision, the scope labels and the agent directory choice) and the package runner (`PiChecks.Packages`: the arguments, a fake `pi` that records its arguments and working directory, a failed install and a blocked launch).
