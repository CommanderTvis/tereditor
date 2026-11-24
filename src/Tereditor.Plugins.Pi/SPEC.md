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
- Extension UI (docs/rpc-extension-ui.md): a dialog request (`select`, `confirm`, `input`, `editor`) is a card in the chat. `select` and `confirm`
  are permission-style cards with Pi's options (Yes/No for a confirm), answered with `value` or `confirmed`; `input` and `editor` are text cards
  (hint, prefill, several lines for `editor`), answered with `value` (an empty text is still a value). Esc, or closing the chat, answers
  `cancelled`; Pi's own timeout settles the card without a reply. `notify` is a notice of its level; `setStatus` a chip in the footer, and
  `setWidget` a text block above or below the composer, both by key and cleared when the text or lines are omitted; `setTitle` sets the chat's
  title; `set_editor_text` sets the composer draft (the host keeps one draft per chat). A method the docs do not list is logged and gets no reply.
- Permissions: Pi has no permission modes of its own. The one mode ("Runs tools freely") is information, not a choice, so the chat shows no
  permission picker for it. An approval extension (Pi's examples `permission-gate.ts`, `protected-paths.ts`) gates tool calls through the dialogs
  above; tereditor bundles none.
- Not offered: questions beyond extension dialogs, rewind, fork, queue editing and background tasks (their methods answer an error).
- Sessions: Pi stores sessions under `~/.pi/agent/sessions/--<path with / as ->--/` (or `PI_CODING_AGENT_DIR`). The sessions list shows that
  directory's files newest first, titled by the session's name or first prompt; opening one resumes it with `--session`. The host remembers each
  chat's session file so reopening its tab continues it.
- tereditor tools: each chat's `pi --mode rpc` gets `-e ~/.tereditor/pi/tereditor-mcp.mjs` (a fixed extension the host writes atomically; never `~/.pi` or the project) and `THINKRAIL_MCP_URL` set to the host's MCP address keyed to the chat, the same address Claude Code chats get. The extension calls Pi's `pi.registerMcpServer("tereditor", { url, exposure: "codemode" })`, so the project tools (lsp_*, context_*, workspace_*, processes_status, resources) reach Pi through its codemode tool (`tools.mcp__tereditor__<tool>`), which Pi's MCP support turns on by itself. It works with the system and the pinned Pi (both read `-e`), a user's own `tereditor` entry in `mcp.json` wins by Pi's rule, and the browser-tab launch gets none. The pane's MCP SERVERS section lists it as a read-only row with the TEREDITOR scope while a chat runs.
- Launcher: "Pi" in the agent launchers always opens a chat tab (there is no terminal mode yet); Settings › Pi has the Pi source, the command, a default model, a thinking level, a Codemode switch and the version read from `pi --version`. Model providers and keys are managed only in Settings › Plugins › Model providers; Pi has no provider or key page of its own.

Settings › Pi shows the Model source control from Model providers as its first setting, above everything else (Plugin API `UiSlots.ModelSourceView`): the agent's own provider (default) or a provider the user added that speaks an API Pi takes. With a provider chosen, Model providers writes Pi's `models.json` in tereditor's own agent directory (`<state>/providers/pi/agent`) with the key as a `!command` for the `provider-key` helper, and the chat's `pi --mode rpc` child runs with `PI_CODING_AGENT_DIR` set to that directory (an agent launch contribution; sessions are listed from it). The key is never in an argument or the environment, and `auth.json` is never written. With none chosen or the plugin off, the child starts as before. With that plugin off the page shows one hint line and nothing else changes. With Air Gateway on, JetBrains (Air Gateway) is one more choice of that control (Central declares wiring for Pi): the chat then starts `central run pi -- --mode rpc …` (a launch contribution that replaces the executable, applied only to the plain `pi` command; a custom command is never rewritten), and a contribution blocked with a reason stops the chat from starting. Pi does not depend on Model providers; its manifest declares `agentRouting` (Anthropic, OpenAI Responses and OpenAI Chat through its models file).

## Pi source

`PiSettings.Source` is `system` (default; nothing changes for a setup that never touches it) or `managed`. Managed runs the pinned Pi that tereditor downloads, and never falls back to the system one.

- Pin: `PiManaged` holds the exact version of `@earendil-works/pi-coding-agent` (1.6.0) next to the npm registry's `dist.integrity` of that release. There is no update-pins task for it: change both constants together, from `https://registry.npmjs.org/@earendil-works/pi-coding-agent/<version>`.
- Download: the host runs `bun install --ignore-scripts --no-progress --registry=https://registry.npmjs.org/` in a temporary folder next to the final one, with a `package.json` that names the exact version. Bun comes from Runtimes for tooling (`ResolveRuntimeAsync(Bun)`: the user's pin, the downloaded copy, then PATH, downloaded with the usual consent); its answer's environment (Bun's cache in the host's cache, no config or `.env` from the working directory) goes to the install and the launch. After it Bun's `bun.lock` must contain the pinned integrity, else the copy is deleted and the download fails with a sentence. The folder is then renamed to `~/.tereditor/pi/managed/<version>` with a record of the pin; a folder of an earlier pin is deleted after the update. One download runs at a time: the settings page and a chat that needs the copy share it. Cancel kills Bun and leaves no partial folder. Nothing is downloaded until the user presses Download or opens a chat with the managed source.
- Launch: `bun --bun <package>/dist/bundle/cli.js --mode rpc …`, the equivalent of `bunx --bun @earendil-works/pi-coding-agent@<version>` from the installed copy, in the workspace directory. The process is reported to Runtimes (`TrackRuntimeProcess`), so turning Runtimes for tooling off stops it before Bun is deleted. A Model source that starts Pi through another program and runs `pi` from PATH (`central run pi --`, a wrapper ending in `pi --`) works with the managed Pi: the wrapper is kept, no Bun words are added, and `~/.tereditor/pi/shim` (a `pi` script that runs `bun --bun cli.js "$@"`, plus `pi.cmd` on Windows; written atomically, rewritten when the version or Bun path changes) is put first on the child's PATH. That Pi 1.6.0 loads Central's `jetbrains-central.run.ts` extension is not verified. A wrapper of any other shape is not known to run `pi` from PATH: the start is blocked with a sentence that names the Model source (`PiChatHost.ManagedBlock`, shared by the chat and the package runner). A model source's environment (the agent directory) still applies. A failed download or a missing Bun fails the chat with the reason; the chat does not run any other Pi.
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
- Model list: the chat's model picker lists what Pi's `get_available_models` returns (Pi's own catalog, in its order, oldest first). The picker leads with one model per family, so only the newest model of each provider and family keeps its family name (`PiChatSession.LeadingByFamily`); the rest are in "More models".
- Settings › Pi › Model (--model) explains the accepted forms (`provider/id`, a pattern, `pattern:thinking`) with examples; it offers no list, as the host has no cheap read-only list outside a running chat.
- Pane header: the title "Pi" with the Early support badge beside it, above the tab switcher, so every tab shows it (the Account tab does not repeat it).
- Account: a Provider row first, the chosen Model source's name or "Pi's own provider (built in)" when none is chosen (the name is the launch contribution's `Provider`, `AgentLaunchComposition.ProviderName`), then the Pi version and the project's trust state. The chat header shows the model and thinking level.
- Scope chips read USER and PROJECT (`-l` maps to PROJECT). Click a row to open its file.
- Not offered: turning single resources on or off, changing trust, and `/reload`.

## Not yet

The browser-tab launch (`PiLinks.InTab`) does not pass the codemode flag yet. A terminal launch of Pi, permission-style gating, rewind and fork through Pi's `fork` and session tree, MCP and tereditor's
tools for Pi, and running Pi inside a browser tab.

## Checks

`--pi` (gate case `pi`): the manifest, the folding of recorded events and stored messages, a session against a fixture `pi` (start, state, models,
commands, a streamed turn, model and thinking changes, close, a missing binary, the version probe) and the sessions list, a launch contribution that replaces the executable or blocks the start, and codemode: a fresh setting is on, and the launch carries `--tools +codemode` (the managed launch too). Codemode needs Pi 1.1.0 or later (the `+name` form of `--tools`); the system command is not probed for it. The pinned Pi runs against a fixture `bun`, with no network: a download whose lock file lacks the pinned integrity is refused and leaves nothing, cancel leaves nothing, two requests share one download, the install arguments, folder and environment, an earlier copy replaced, no second download, the launch as `bun --bun cli.js --mode rpc …` with the process tracked, a blocked launch passed on, a missing Bun failing the chat, Remove, and the system source unchanged. The pane's reads (`PiChecks.Config`: a fixture home and project, the trust gate, the nearest trust decision, the scope labels and the agent directory choice) and the package runner (`PiChecks.Packages`: the arguments, a fake `pi` that records its arguments and working directory, a failed install and a blocked launch).
