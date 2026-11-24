---
id: module-plugin-omp
type: module-design
status: active
title: omp builtin
parent: group-plugins-agents
---

# omp builtin

Basic ("Early support") integration of the omp coding agent (https://omp.sh). Disabled by default. The manifest
declares `Support = early`, which Settings › Plugins shows as an "Early support" badge on the card; the Agent pane content and
Settings › omp wear the same badge. omp fills Context (context files and offers), Account (version, install link, sign-in import) and Settings (the code-intelligence and profile notes); Capabilities says omp has nothing there. Host and UI talk only through the plugin contract (wire version 1). tereditor never
installs omp: it runs the `omp` the host finds on PATH (or the configured command).

Facts below come from omp's docs: CLI https://omp.sh/docs/cli, context files https://omp.sh/docs/context-files,
MCP https://omp.sh/docs/mcp, settings and profiles https://omp.sh/docs/settings and https://omp.sh/docs/env, sessions
https://omp.sh/docs/sessions, roles https://omp.sh/docs/roles, code intelligence https://omp.sh/docs/code-intelligence.
No real omp run backs them; the checks use a fixture `omp`.

## Acceptance contract

- Launch line (`OmpLaunch.Line`, shared by the launcher, the start action and the revive prefill):
  `[TEREDITOR_UI_LAUNCH=omp] omp [--profile tereditor] [--session-dir <dir>] [-r <file> | -c] [--model <m>]
  [--approval-mode <mode>] [--advisor] [--no-lsp] [--append-system-prompt <text>] [@image…] [prompt]`.
  Other plugins' launch contributions wrap the executable word as for the other agents.
- Settings: command (default `omp`), model, approval mode, advisor, "Give sessions tereditor's tools" (default on) and
  "Use omp's own code intelligence" (default off). Model and approval mode are free text with suggestions: the
  `--approval-mode` values `omp --help` lists (omp 18.12.6: `always-ask`, `always-ask-cached`, `write`, `yolo`), and
  for `--model` the built-in roles as `@role`
  plus the example selectors from the roles page. Blank values pass no flag, leaving omp's configuration in charge.
- MCP: omp has no per-launch MCP flag or config-file variable. Launches with MCP on use the omp profile `tereditor`,
  whose `~/.omp/profiles/tereditor/agent/mcp.json` tereditor owns and rewrites whole (atomically, only when it
  differs) on activation, on settings changes and for each terminal: one `tereditor` HTTP server whose URL is
  `${THINKRAIL_MCP_URL}`. omp expands `${VAR}` when it loads the file, so one static file serves every terminal's own
  MCP endpoint. The profile replaces omp's global scope: it has its own sign-in, settings, sessions, caches and
  user-level context files. The pane says so; users run `/login` once in a tereditor omp tab, use provider API key
  variables, turn MCP off to launch with their default profile and no tereditor tools, or press "Use my omp sign-in in
  tereditor" (Settings › omp points to it).
- Setup wizard: a fresh profile would show omp's first-run setup wizard even with provider keys exported. When MCP is
  on, the host (once per command per host run, in the background, never blocking a launch; one 10 s bound; honoring
  `TEREDITOR_OMP_HOME` and the plugin's command) runs `omp --profile tereditor config get startup.setupWizard` and, only
  when it is `true` and `config get setupVersion` is `0` (never completed), `config set startup.setupWizard false`.
  A completed wizard, a user who re-enabled it, a missing omp or any failure leaves the profile alone (a log line only).
  Launches need `/login` or the sign-in import button.
- Sign-in import: the button shows only while omp is installed and MCP is on, and runs only when pressed, never
  automatically. The host (off the UI thread, one run at a time, 60 s bound) pipes `omp auth export` (default profile;
  an inherited `OMP_PROFILE` is removed) into `omp --profile tereditor auth import -` (syntax from `omp auth --help`,
  omp 18.12.6: export writes JSON to stdout, `-` reads stdin). The credentials stream process to process: tereditor
  never writes them to disk or logs and never sends them to clients; the result carries only success or the first
  error line, at most 160 characters with token-like runs masked. The button is disabled while running and the pane
  shows progress, then the result. Any authenticated client may trigger it; it changes the host's own omp profile.
  `TEREDITOR_OMP_HOME` also becomes omp's `HOME` for these runs, so checks never touch the real `~/.omp`.
- Code intelligence: by default launches pass `--no-lsp`, so sessions use tereditor's shared language servers through
  the `lsp_*` MCP tools instead of starting omp's own; the setting keeps omp's servers. The pane says which applies.
  A possible follow-up is bridging omp's `lsp.json` to tereditor's servers with a proxy command, so omp's built-in
  `lsp` tool reaches the shared servers.
- Sessions: omp cannot take a caller-chosen session id. Each UI launch gets its own session folder,
  `--session-dir "$TEREDITOR_OMP_SESSIONS/<32-hex id>"`, where the host sets `TEREDITOR_OMP_SESSIONS` in every
  terminal to `<plugin state>/sessions` under `~/.tereditor`. The agent record's session id is that folder name, read
  from the process arguments. Revival resumes the folder's newest `*.jsonl` with `-r <file>`, or starts again in the
  folder when none was saved; the generic launcher's resume continues with `-c` in the folder. The host offers the
  revival and runs it only on Resume (Host Plugins.SPEC, Terminals); the hook's `Fresh` command starts in the same
  folder without `-r`. Sessions in these
  folders do not appear in omp's own session picker. Windows launches keep omp's default store.
- Context files (host, off the UI thread): per scope and directory depth the highest-priority provider with a file
  wins (native `.omp/AGENTS.md`/`AGENTS.md`, claude `.claude/CLAUDE.md`, agents `.agent(s)/AGENTS.md`, gemini
  `.gemini/GEMINI.md`, github `.github/copilot-instructions.md`; ancestors walk agents then `AGENTS.md`; user scope
  native, then `~/.codex/AGENTS.md`, then `~/.config/opencode/AGENTS.md`). Shadowed files are listed and marked.
  `RULES.md` applies at user and project level; `SYSTEM.md`/`APPEND_SYSTEM.md` come from `.omp/` before the user
  directory. User-level files come from the profile's agent directory while MCP is on. Rows are ordered most general
  first with a token estimate (bytes / 4). `@path` imports (at a line start or after whitespace, outside inline code and
  fences, relative to the importing file, `~/` and absolute accepted) are nested rows under their importer, at most five
  hops; a file already on the import chain is shown as a cycle and not followed; an unreadable import is not a row.
  The pane offers "Add AGENTS.md", "Add .omp/RULES.md" and "Add .omp/APPEND_SYSTEM.md" when missing; adding never
  overwrites an existing file. This model follows the docs, not omp's code.
- Version and missing binary: the host runs `<command> --version` on a pool thread with a 10 s bound. The pane shows
  `v<version>`, or "omp is not installed" with the reason and an "Install from omp.sh" button; the launcher reports
  itself unavailable with the same hint.
- Agent detection: a 2.5 s process poll (a copy of the Codex plugin's) records an `omp` descendant of a tab's shell
  as an `omp` agent record and clears it when the process leaves; zombie (`Z`) rows are skipped. A record of another
  agent kind is left alone. The search does not descend through another tereditor app (process name `Tereditor.UI`/`tereditor`), so an agent in a nested tereditor's terminals never marks the outer tab; a tereditor run as `dotnet Tereditor.UI.dll` is not recognized by name. If an omp build runs under another process name (for example its runtime's), it is not
  detected; this is not verified against a real install.
- Air Gateway does not govern omp: Central serves Claude Code, Codex and Pi only.
- Mark: the manifest icon is `asset:omp.svg`, omp's π mark from the omp.sh site header redrawn as one `currentColor`
  path, so Settings › omp, the plugin card, its Agent pane chip and omp terminal tabs (workbench and Projects)
  wear it. The launcher's icons and the start action draw it in a theme brush and follow theme changes; the kit's
  terminal glyph stands in until the asset is read, and a failed read is never remembered: it is retried (six
  attempts, a growing delay) and on the next glyph built, so no launcher icon keeps the stand-in permanently.

Settings › omp shows the Model source control from Model providers as its first setting, above everything else (Plugin API `UiSlots.ModelSourceView`): the agent's own provider (default) or a provider the user added that speaks an API omp takes. With a provider chosen, Model providers writes `models.yml` in the `tereditor` profile with the key as a `!command` for the `provider-key` helper, and the launch line gets `--profile tereditor` (once, with or without MCP, also with a custom command; an agent launch contribution read as a signal). The profile's setup wizard switch is prepared as for MCP. The key is never in the line, and omp's credential database is never written. With none chosen or the plugin off, the line is the one omp builds itself. With that plugin off the page shows one hint line and nothing else changes. omp does not depend on Model providers; its manifest declares `agentRouting` (Anthropic, OpenAI Responses and OpenAI Chat through its models file).

## Not yet

Hook-based status (running/blocked/done), account and usage, tereditor's appended instructions (so no generated
instructions file) and skills, IDE
context, and a settings editor for omp's `config.yml` are out of this basic integration.

## Checks

`--omp` (gate cases `omp-host`, `omp-ui`): launch argv for profile (also with a model source's contribution), session folder, model, approval mode, advisor and
`--no-lsp` (and its omission), resume lines and Windows; context resolution with nested, cycle-safe, depth-limited
imports, shadowing, ignored code/e-mail tokens and offers; the profile MCP file; the setup wizard switch (set once, not again, skipped when setupVersion > 0, failures and hangs tolerated); the version probe against a fixture
`omp` and a missing one; zombie-safe detection; newest-session lookup; a governed launch run by a real shell against fake `omp` and `central` (proxy started, wire
in omp's environment, arguments unchanged; none when direct; blocked without Central's Pi integration, which is only read); and end to end the
Early badge, version, notes, nested rows, an Add offer, the start action's argv, the omp mark (not the terminal glyph) on the start action and an omp terminal tab, the sign-in import against a fixture
`omp` (data lands in the fixture profile, nothing else persisted, a failure shows a masked message) and the
missing-binary state. The
Settings › Plugins card badge is covered in `--plugins`.
