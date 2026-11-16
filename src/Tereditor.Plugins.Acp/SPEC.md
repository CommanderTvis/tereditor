---
id: module-plugin-acp
type: module-design
status: active
title: ACP builtin (Early support)
parent: group-plugins-agents
---

# ACP builtin (Early support)

Early support for the Agent Client Protocol (https://agentclientprotocol.com): tereditor is the client, an ACP agent is a child process
speaking JSON-RPC over stdio, and the conversation is shown in the shared chat view (`src/Tereditor.Plugins.Chat/SPEC.md`, "Serving another
agent protocol"). Disabled by default; the manifest declares `Support = early`, its label is "ACP" and
`UnavailableOn = [pureWeb]`, because an agent is a child process of the host and a browser tab has none. The Android and browser clients reach
a host's ACP chats through the chat contract like any other client.

The agent is untrusted code. It is never sandboxed by tereditor; the rules below limit what the client methods give it.

## Protocol (stable version 1)

- Framing: one JSON-RPC 2.0 message per line (`AcpConnection`); either side sends requests and notifications; requests from the agent run
  concurrently so a waiting permission prompt never blocks reading; a request pending when the agent's output ends fails with
  `AcpClosedException`.
- `initialize` sends `protocolVersion: 1`, the client capabilities (`fs.readTextFile`, `fs.writeTextFile`, `terminal`) and `clientInfo`. An
  agent answering another version is refused by name (`AcpVersionException`). Agent capabilities read: `loadSession`, prompt capabilities
  (`image`, `audio`, `embeddedContext`), `authMethods`, `agentInfo`.
- Messages to the agent are built as JSON nodes; the registry and icon cache records use the plugin's own
  source-generated metadata, never reflection, with the bytes the reflection serializer wrote.
- Session: `session/new` (modes, models and config options of the answer fill the header), `session/load` when the agent advertises
  `loadSession` and the chat has a stored session id (the replayed updates rebuild the transcript; otherwise a new session starts),
  `session/prompt` for a turn, `session/cancel`, `session/set_mode`, `session/set_model`, `session/set_config_option`.
- Pickers: the chat shows what the agent offers and nothing else. An option with category `mode` or `model` (or id `mode` or `model` when it has
  no category) fills the mode or model picker, and is set with `session/set_config_option`; `session/set_mode` and `session/set_model` are used only
  when the agent offered `modes` or `models` instead. Config options replace `modes` and `models` for the same purpose. Grouped select values are
  flattened; a group becomes the model's provider. Every other select or boolean option gets a chip of its own whatever its category
  (thought level, model parameters, permissions, custom ones), set by `chatSetOption`; a select of exactly on and off reads like a switch
  ("Allow All off"). An agent may offer no model list at all: GitHub Copilot CLI sends only a mode option, an Allow All select and `modes`, so it
  has Mode and Allow All chips and no model chip. The answer of `session/set_config_option` and every `config_option_update` carry the full set of options and
  replace what is shown. The client advertises boolean options. An agent that offers no model and no mode gets neither chip.
- Updates folded by `AcpFolder`: `user_message_chunk`, `agent_message_chunk` and `agent_thought_chunk` (grow one item until another kind of update
  arrives), `tool_call` and `tool_call_update` (keyed by `toolCallId`; text content, diffs as `-`/`+` lines with the file path for the card,
  terminal references), `plan` (one assistant item replaced in place, entries as a checklist), `available_commands_update` (slash menu),
  `current_mode_update`, `config_option_update`, `session_info_update` (title), `usage_update` (context size and cost).
- Stop reasons: `end_turn` is Idle; `cancelled` adds an interrupt marker; `refusal` and errors add a failure notice; `max_tokens` and
  `max_turn_requests` add a warning.
- Permissions: `session/request_permission` becomes a permission card with the agent's own title; the call stays open until the user answers.
  The card shows the tool kind, the files with their lines, the command, path or pattern of the raw input, the agent's text, and a short diff for
  each file change (a new file shows all its lines), with the whole raw input one fold away. Its buttons are the agent's options, in its order and
  with its names, and the answer carries the chosen option id, so `reject_always` works. ACP cannot send a reason, so the card has no reason box.
  Nothing is auto-approved. Allow answers with the agent's `allow_once` option, or `allow_always` when the user asks to remember it and the
  agent offered one; deny answers `reject_once`. An agent that offers no option of the wanted kind is answered as cancelled. Cancelling a turn
  answers every waiting request as `cancelled`, as the protocol requires.
- Client methods (`AcpClientMethods`): `fs/read_text_file` (with `line` and `limit`), `fs/write_text_file`, `terminal/create`, `terminal/output`,
  `terminal/wait_for_exit`, `terminal/kill`, `terminal/release`. Every path and every terminal working directory is resolved, symbolic links
  included, and refused when it leaves the workspace root. A terminal runs its command directly (no shell), merges stdout and stderr, cuts the
  front of its output at the agent's byte limit and is killed when released, when the chat closes or when the agent exits.
- Process: `ProcessAcpTransport` starts the agent in the workspace directory (or the entry's directory) with the entry's environment added; closing
  stdin and then killing the process tree ends it. An exit shows the chat as Exited with the last stderr lines and a retryable failure notice;
  opening the chat again starts a new agent and resumes the stored session through `session/load` when the agent can.

## Registry

- Source: `https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json`, schema version `1.0.0`
  (`{version, agents[], extensions[]}`; each agent has `id`, `name`, `version`, `description`, optional `repository`, `website`, `authors`,
  `license`, `license_url`, `icon` and `preview`, and a `distribution` with any of `npx` and `uvx` (`{package, args?, env?}`) and `binary` (per
  `darwin|linux|windows`-`aarch64|x86_64` target: `{archive, cmd, args?, env?, sha256?}`). The per-agent schema is `agent.schema.json` of the
  registry repository. `tests/Tereditor.Checks/Fixtures/acp-registry.json` is a trimmed real copy.
- Cache (`AcpRegistryCache`) under `~/.tereditor/acp/registry`: a copy younger than the registry's 5 minute `max-age` is not refetched; a stale
  one is revalidated with `If-None-Match` and `If-Modified-Since` (a 304 keeps it); offline, or when the answer is not a registry, the saved copy
  is shown with a note; only with no copy at all a built-in two-agent seed stands in, and says so. Files are written beside and moved into place.
- Install (`AcpInstaller`): the settings page asks for a plan first (source, exact command line, SHA-256, license, repository) and shows it;
  `Install` needs `Confirmed` and the agent, distribution kind and version of that plan, or it refuses (the registry may have moved on), so every
  agent and every version needs its own confirmation. npx entries run with the host's pinned Bun (`bunx <package>`, started as `bun x`, downloaded and checked on first use, so no Node or npx is needed; `npx` only when Bun cannot be provided), uvx entries `uvx <package>`, both pinned by the
  registry. A binary is downloaded over https only, checked against the registry's SHA-256 (a mismatch discards it; a missing checksum is
  said so in the plan), unpacked with the platform's `tar` into `~/.tereditor/acp/agents/<id>/<version>/` and its command must stay inside
  that directory. The registry's `preview` channel is ignored.
- Entries for agents tereditor supports directly (a small id list in `AcpRegistry`: Claude and Pi adapters) get a Not recommended tag with the
  reason, sort after the others, and say so in the install confirmation. They stay installable. Manually added agents are never flagged.
- Icons: the registry gives each agent an `icon`, an https url of a 16x16 monochrome SVG on the registry's own host that uses `currentColor`.
  `AcpIconCache` (the host half, under `~/.tereditor/acp/registry/icons`, one `.svg` and one `.json` of validators per agent id) fetches only
  urls that are https, on the registry's host and default port, with no credentials, with no redirect followed, a 10 second timeout and a 64 KB
  cap, and revalidates with the ETag like the registry (a copy younger than 12 hours is not refetched). `AcpIconRules` refuses a document that is
  not well-formed SVG XML, declares a doctype, contains `script`, `foreignObject`, `image`, animation or embedding elements, an event handler, an
  `href` that is not a `#fragment`, or a CSS `url()` or `@import` that is not a fragment; an icon that turns unsafe is dropped with its old copy.
  Ids that are not plain file names are skipped. The UI asks the host (`icons`, never with urls) after the list is shown and draws the SVG with
  the theme's text colour for `currentColor` (hard-coded colours stay); until then, with no icon, for a rejected one and for custom agents it
  draws the generic ACP glyph. The New ACP chat menus read icons from disk only. The chat header keeps the plugin's own icon. A chat tab wears the icon of the agent it talks to: the UI asks the host (`chatAgents`, a chat id to agent id map per workspace) and draws the cached SVG through a tab decoration (`TabDecoration.IconSvg`) in the tab's text colour. The tab strip, the tab list and the rail use the same decoration. A chat with no known agent, a custom agent, or an agent with no icon on disk keeps the generic ACP icon. Reading the tab icons fetches nothing. Starting a chat for a registry agent does: when that agent's icon is not on disk, the host fetches that one icon in the background through the same `AcpIconCache` (same url rules, size cap, checks, timeout and atomic write), once per agent per run. The chat start never waits for it and a failure is only logged at debug level, without its message. A custom agent does nothing. The UI then asks `agentIcon` (an agent id, answered with that one icon or nothing) and redraws the tabs when the icon arrives.
- Settings layout: Settings > ACP is a dense list on the left and a large pane on the right (`MasterDetail`, the list is resizable), under the registry trust warning, a search, Refresh with the
  `Fetched` time, and Add custom agent. The list (`AcpRows.Combined`) has Installed agents (registry installs and custom ones, by name) pinned
  under an Installed heading, followed by the registry agents that are not installed under a Registry heading. An installed registry agent
  appears once, in the pinned group. The search filters both groups by name, description or id, and installed matches stay on top. A row shows the
  agent's icon, name, version (Custom for a custom agent), one line of description and, at the right, Install (or Not on this machine) for a registry row, or
  a mark for an installed one (a red alert when the host knows something is wrong: the program path does not exist, or the last start of a chat failed,
  `AcpAgentList.Problems`; a refresh mark for Update available; a check otherwise). Selecting a row shows its pane; nothing expands under the row. A registry row's pane shows the
  description, version, kinds, links, license, the trust notice and Install; a finished install selects the installed row. If the registry cannot be read, installed agents still show with a plain message. An installed agent's pane holds:
  name, tags (Registry or Custom, Not recommended, Update available, Off, Default), description, version, source, website and repository links (https only), license,
  install status and location, what was verified, the trust notice for registry agents, a switch to offer it in the launcher, a switch to make
  it the launcher's default, the executable override with a Use default arguments switch and the arguments one per line (a registry agent), or
  its command and arguments (a custom agent), environment, working directory, the command line that will run, Update to or Reinstall, and
  Remove. Fields save when they lose focus. Update and Reinstall keep the user's overrides, working directory and environment. Remove asks in
  the overlay and deletes a registry install's files under `~/.tereditor/acp/agents/<id>` (nothing outside it). There is no "use the program on
  PATH" choice: a registry binary has no name to look up, and uvx agents already run through the PATH.
- Add custom agent: a popup in the overlay with name, command, arguments, environment and working directory. It refuses an empty name or command,
  a name already in the list, an environment line that is not NAME=value, and a relative working directory; Add selects the new agent.
- Checks on a download: the registry's schema has an optional `sha256` per binary target and nothing else to verify (no signature, minisign,
  cosign or digest fields).
  A checksum the registry lists is always checked, after the download and before anything is written or unpacked, with a fixed-time compare;
  a mismatch, a value that is not 64 hex digits, or a download that is not https refuses the install. The plan, the confirmation, the detail
  pane and the entry say what applied: SHA-256 verified, No checksum from the registry, Version pinned or Not pinned. Packages get no check
  beyond the pinned version. No signature scheme exists to implement. `AcpInstallPlan.TrustNote` (third-party programs, run with the user's access, install only trusted ones) is in the confirmation, at the top of the registry browser and in each registry agent's pane.
- Install confirmation: the plan is shown in the in-window dialog (`DialogWindow`), over the window that has the Install button, with the name
  and version, source, command, checksum, license, repository and the explanation; Escape and Cancel install nothing.
- Default agent: a switch in the agent's pane writes `AcpSettings.DefaultAgent` (the plugin's settings, written through `UpdateSettingsAsync`) names the agent a click on the New ACP chat
  button starts. Without a usable default the click is as before: the only agent, or the picker. A default whose agent was removed is kept and
  ignored (the settings say so), so adding it back restores it. Right-click lists every agent in order with the default marked, and ACP settings;
  on a touch screen a tap opens that menu. This is the split of the Claude Code launcher.
- Custom agents: command, arguments, environment and working directory typed in the settings page; adding one is the confirmation.
- Credentials: tereditor never shows a field for them and never reads a secret.
- Default mode: an agent's pane has a "Default mode for new chats" choice (`AcpAgentEntry.DefaultMode`; blank keeps the agent's own). The choices are the modes the agent listed in its last chat (`KnownModes`, kept by the host when a session opens, so an edit from an older copy cannot wipe them); before the first chat the pane says to start one. After `session/new` a chat switches to the default through the normal mode request when the agent lists that mode and is not already in it; a refusal is logged and the agent's own mode stays. Cause of issue #63: GitHub Copilot sends its modes (URL-valued ids) in `session/new`, so the chat's Mode chip worked, but no settings page had a place to choose one for new chats.
- Signs in with: each agent's pane has a "Signs in with" list, and the failed-start card repeats it. The host (`signIn` method, `AcpSignIn`)
  lists, in the agent's own order and from names and file existence only: variables set in the launch environment (the host's merged with the
  entry's own; names, never values), a stored credential file that exists, the GitHub CLI account, and what the agent said. The first source
  known to be there is marked "checked first", and only for an agent whose order is documented. A tooltip gives the reason: what is not set, what was
  skipped and why, and the order. The list says what is available and in which order the agent checks it (one source shows as plain text, several as bullets). It never claims which source the agent used.
  - Table (`AcpSignIn.Known`, documented facts only). `github-copilot-cli`: `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN`, then the OAuth
    token that `copilot login` keeps in the OS keychain (service `copilot-cli`), then the GitHub CLI login as the last fallback. A variable
    silently overrides a stored login. With `COPILOT_PROVIDER_BASE_URL`, `COPILOT_PROVIDER_TYPE` or `COPILOT_PROVIDER_API_KEY` no GitHub login is
    needed; the line names the variable. An agent not in the table shows only variables of its own entry whose names look like credentials
    (TOKEN, KEY, SECRET, AUTH, PASSWORD, CREDENTIAL), unmarked, and no GitHub CLI. A row can also list credential files under the home folder.
  - GitHub CLI: `gh auth status --hostname github.com`, run on the host with a 5 second limit, with the token variables removed from its
    environment. Only the account name is kept from the output; `gh auth token` is never run. The answer is cached for 30 seconds.
  - Keychain: never queried. Asking for an item that another program stored can raise a permission prompt, and the prompt would be a surprise
    while the user is only looking at settings. Once the agent has answered `initialize` in this app run, or a start has failed, the list says
    "A stored login, if you ran `copilot login`", as something that may be there. Nothing is marked "checked first" after such a line.
  - The agent's own answer: after `initialize` the host keeps `authMethods` in memory (name, description, and for a terminal login its label).
    They show as "A sign-in the agent will ask for". If the agent advertises `agentCapabilities.auth.status` (the unstable `auth/status`), it is
    asked once at start and its accounts show as "The agent says it uses". The settings pane never starts an agent to ask.
  - Terminal login: a method of `type: "terminal"` runs the entry's program and arguments plus the method's `args`; a method with
    `_meta["terminal-auth"]` (GitHub Copilot CLI 1.0.112 sends this with no `type`) runs its own `command`. A bare command that is the entry's program
    is the entry's launch command. The line has the method's own variables first, every part quoted for the shell, and never the entry's
    environment. "Run in terminal" opens a terminal tab with that line (the tooltip shows it all); the user signs in there. Nothing is run
    without a click and nothing is typed for the user. The settings pane asks again when the window gets focus back, and "Check again" asks at once.
  - The failed-start card (`ChatNoticeItem.Action`, `ChatHitKind.NoticeAction`) lists the same lines, the reason and the button.
  - Checks: `--acp` (`AcpChecks.SignIn`): a fake `gh` that records its arguments, a sentinel token that must appear nowhere, credential files in a temp home, and the fake agent in its terminal, `auth/status` and not-signed-in modes.

- Prompt content (`AcpPromptBuilder`): the text comes first, then one block per chip. Paths are host paths in `file://` URIs; a folder link ends with a slash. An editor or preview selection is an embedded `resource` (label `path:start-end` or `path (selected text)`, then the text; the range also rides in `_meta.tereditor`) when `promptCapabilities.embeddedContext` is on, else a `resource_link` plus the text in a fenced block. A selection over 100000 characters is linked with its range and the chat says so. A picked file or folder is a `resource_link` (a file with its size), never embedded. A finished `@path` or `@path#L1-5` in the text inside the workspace becomes a link or an excerpt; one outside stays text. Images go inline only with `promptCapabilities.image`; without it a picked image is linked and a pasted one is dropped, and the chat tells the user. The editor's own context (chip kinds `selection` and `file`, sent as chips because `ChatAgent.StructuredContext` is on) is left out, with a note, when the file is outside the workspace or Git ignores it; a file the user picks is sent anyway. The chat view shares the chips and `ChatMentions` with the Claude Code chat.

## Not yet supported

- Unsaved editor buffers: `fs/read_text_file` and `fs/write_text_file` read and write the files on disk (confined to the workspace); the host
  half has no access to the editor's document model, so unsaved buffers are not seen and writes do not reach open editors as diffs. Tool
  diffs are shown on the tool card as text.
- The terminal service: agent terminals are plain child processes owned by the chat, not terminal tabs, and are not in the owned-process
  registry (that registry is keyed by a terminal tab).
- `authenticate` for a method of type `agent`: the method is implemented in the client but nothing calls it; it is only listed. Terminal logins have a button.
- The keychain and any credential file of an agent other than Copilot: not looked up, because the facts are not verified.
- The `preview` channel, the registry's `extensions` and per-agent `website` links are not used.
- A plan record (plans are text), audio prompts from the composer, images in
  agent output, `session/list` and `session/fork`, the agent's `mcpServers` (none are passed), steering or queuing a message while a turn
  runs (the send is refused until the turn ends), and a headless UI check of the chat view with streamed ACP output (the chips and the
  approval card are covered by `--claude-chat-ui`; the ACP folding is covered by `--acp`). A thought level is an option chip, not the model menu's effort.
- Unstable ACP methods and extensions.

## Checks

`--acp`: the manifest and Early support label; framing, version negotiation, session new, prompt streaming, cancel, load and crash against the
in-process fake agent (`tests/Tereditor.Checks/AcpFakeAgent.cs`); the folded transcript; permission answers by option id and cancellation; the pickers for an agent that offers only config options, only modes or only models and the request each one sets; fs and terminal
methods and their confinement (including a symbolic link and `..`); registry parsing against the fixture, the cache, offline fallback and
seed; registry icons (url rules, unsafe SVGs, size cap, cache and offline, the generic glyph); the row model and search; the install confirmation facts; the agent list model, detail model and tags, the add popup's validation, checksum and trust checks, the launcher's default, removed default, turned-off agents and menu; install confirmation, version mismatch, checksum failure and unpacking; the settings page's text forms. Checks never launch a real agent
or download from the registry.
