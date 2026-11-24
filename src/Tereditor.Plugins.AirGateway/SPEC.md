---
id: module-plugin-air-gateway
type: module-design
status: draft
title: Air Gateway — Claude Code, Codex and Pi through JetBrains Central
parent: group-plugins-agents
depends-on: [module-plugin-api, module-plugin-ui-kit, module-plugin-providers]
references: [submodule-host-plugins, submodule-ui-plugins, submodule-ui-panels]
tags: [plugins, central, governance]
---

# Air Gateway — Claude Code, Codex and Pi through JetBrains Central

Adapted from the former ThinkRail fork's JetBrains Central integration. The fork routed Central into `pi`
through one extension file; this plugin keeps the fork's detection, sign-in, proxy,
update and access-source behavior and replaces the pi extension with Central's `central run <agent>`, one launch at a time, for Claude Code, Codex and Pi.
omp and the ACP agents are not routed through Central: Central declares wiring for Claude Code, Codex and Pi only.

Air Gateway was called Air Governance until JetBrains renamed the product; the plugin id `air-governance` is migrated to `air-gateway` when host state loads (HostState.SPEC.md), and older history keeps the old name.

## Responsibility

Adds JetBrains (Air Gateway) to Model providers and starts the agents that choose it through Central. The plugin depends on Model providers (all three halves). The plugin registers no settings section; Central is set up in the JetBrains (Air Gateway) pane of Model providers. That pane has no AI source switch and no agent list: the choice is the Model source setting, the first setting of the Claude Code, Codex and Pi pages (owned by Model providers).

- The host half contributes the provider `jetbrains-ai` (`AirLaunch.Provider`: no key, no API, `WiredAgents` = Claude Code, Codex, Pi) while the plugin runs, and withdraws it when the plugin stops, which also drops the choices that used it. The UI half fills the provider's place in the Model providers list (`ModelProvider-jetbrains-ai`) with a pane that says which agents it works with and holds the Central status: the version and path, signed in or out, Sign in and Sign out, Start proxy and Update Central when they apply, the credits, and the JetBrains AI access source list with Switch (`AirSettingsView`, hosted by `JetBrainsAiRow`). The pane text is three short sentences: what choosing Central does, that it only changes the command of new terminals and chats, and that the sign-in is checked when the pane opens or the window regains focus. The pane checks when it becomes visible (its row selected), not while hidden. Only those three agents are offered the option; with the plugin off, or unavailable (the Playground), it is absent.
- Both halves follow Model providers' `models` channel and keep the set of agents whose Model source is `jetbrains-ai` (`AirLaunch.Chosen`) as an in-memory snapshot: the UI half for launch lines, the host half for revived terminals and chats, credits and the Account read.
- The plugin has no settings of its own (`AirSettings` is empty). The plugin ships disabled, since governing agents is the user's choice.

The Model source hint says the choice applies to terminals and chats started afterwards; sessions already running keep their old command.

## Mechanism

`central run <agent> [flags] [-- extra-args...]` launches an agent through JetBrains Air with settings passed for that
launch only; Central states that the agent's configuration is not changed. The plugin therefore never edits any agent
configuration and never calls `central add` or `central remove`: nothing under `~/.claude`, `~/.codex`,
`CLAUDE_CONFIG_DIR` or `CODEX_HOME` is written, and switching back leaves nothing to undo.

For each agent whose Model source is JetBrains (Air Gateway), the plugin contributes, through the Plugin API's agent launch seam (W22 in the UI half for
launchers and starts, H18 in the host half for revived terminals), a replacing wrapper
`<central> run <claude-code|codex|pi> --` to launcher ids `claude`, `codex` and `pi`. Model providers itself adds nothing to those launches. The agent plugins apply it to the
executable word of the command they build: the launcher, its right-click flags, the Create-workspace and Project Home
starts, resumes and the host's revive offer, so the agent's usual arguments follow the `--`. Pi has no terminal launcher: its chat host applies the wrapper to the `pi --mode rpc` child (`central run pi -- --mode rpc …`) and refuses to start while a contribution is blocked. tereditor's own
environment prefixes stay outermost. A custom command in the agent's settings (not the default `claude` or `codex`)
is never rewritten and so is not governed; the pane says so. Running terminals keep the command they started with.
`<central>` is the path the host found, else the bare name: if Central is missing the launch fails in the shell
rather than silently starting an ungoverned agent.
Central chooses its own AI access source and proxy; `central access <id>` (switch, then `central proxy restart`)
stays the way to change it, as in the fork. The pane states persistently that switching affects every app using the
host OS user's Central.

## Detection and actions

The adapter is the fork's, bounded the same way: the binary is the first `central` on the host's PATH or
`~/.local/bin/central`; `--version` (5 s) must parse as `central X.Y.Z` and be at least 1.4.0 (outdated otherwise,
malformed and probe-failed distinguished); `central status` (15 s) is read only for its `Auth` and `Proxy` rows,
and only a positively observed `not connected` or `stopped` sets a flag. Output is capped at 16 KiB, timeouts kill
the process tree, stderr is discarded.

Only local, read-only checks run automatically: `central --version`, `central status` and, while an agent's Model source is Central and
signed in, `central limit --json` (15 s, 16 KiB), never `login`, never a browser, never the OAuth or Cloud endpoints. The pane runs it when it opens, on window
activation (at most once per 15 s), after Sign in and Sign out, and, while a sign-in is awaited after Sign in, every
5 s for at most 3 minutes, stopping once signed in, on Sign out, or when the pane closes. Checks never overlap, run
off the UI thread in the host half under the 5 s and 15 s bounds, drop results arriving after the pane closed, and
do not run while the pane is not attached and visible or the plugin is disabled (nothing then runs Central at all).
The pane shows checking, signed in, signed out (Sign in enabled, Sign out disabled), not found, outdated and
error states. There is no manual check button. Everything else is an explicit button: Sign in (`login`, browser, waits 1.5 s for a fast failure), Sign out (`logout`), Start proxy
(`proxy start`), Update Central (`update --install`) and Switch (`access <id>`, on a source the automatic listing below shows,
then `proxy restart`; a failed restart is a warning). A missing CLI shows the platform install command from
central-cli.labs.jb.gg as text; the plugin never runs it.

While an agent's Model source is Central and Central is signed in, the pane also shows the chosen source's remaining credits, for example `16.27 of 20.00 credits remaining, refills 2027-01-15` (plus the top-up when nonzero). They are read from `central limit --json` with every automatic check and after a successful Switch: `tariffQuota.available` and `maximum` (numbers or numeric strings; else `maxDollars` less `usedDollars`), `topUpQuota.available` and `refillNext` (epoch ms). Status and the pane carry only those numbers, never the email or license name. Missing, malformed, negative, non-finite or inconsistent values, a failed or timed-out command, signed out and the direct source all show nothing.

## Credits in the header

While Central governs at least one agent (`AirLaunch.Wired`: an agent whose Model source is JetBrains (Air Gateway)), the UI half shows the remaining credits as `x / y credits` at the trailing end of each window's header,
before the connection status and Settings, through the Plugin API's header item (W24); otherwise it shows nothing.
The numbers are the status channel's credits (above), so the host that runs `central` supplies them, including a
remote one; the host stamps each successful read with its time (`ReadAt`). The tooltip names the governed agents,
the plan's remaining and total credits, the top-up when nonzero, the next refill and when the credits were read
(the license name and email stay off the status channel, so the plan is not named). Below a tenth of the plan left,
top-up included, the item uses the warning color. Clicking it opens Settings › Plugins › Model providers, where the JetBrains (Air Gateway) pane is.

Reads are the status method with the probe (so the same `--version`, `status` and `limit --json` as the pane), one
shared reader per activation however many windows show the item: when an item attaches or its window activates,
when a terminal whose agent ran through `central run` leaves the host projection, and every 5 minutes, at most once
per 15 s (the pane's throttle); and at once when an agent's Model source changes to or from Central. Reads never overlap; a forced
read during one runs after it. Nothing is read while no item is attached, Central governs no agent or the plugin is
disabled. Status pushes (the pane's checks, actions and the agents' Account reads) update the item without a read.
Before the first read nothing is shown; after a read without credits the item shows `credits ?` in the warning
color with the reason in the tooltip (not installed, outdated, cannot be used, not signed in, sign-in unknown, no
credits reported, or the call's error). A compact (phone) workbench shows no header items; the credits stay in the
JetBrains (Air Gateway) pane of Settings › Plugins › Model providers.

## Account for governed agents

A host-side launch contribution also carries `Account` (Plugin API H18): source `Central`, read on request. Codex and
Claude Code (Pi has no Account tab) call it from their Account tab while their launches are governed, and it runs a fresh probe and
`central limit --json` (the read above), then returns the `email` and `licenseName` of that output, the credits and
the refill time. They are passed on for display only, never stored or logged, and never put on the plugin's status
channel. An unavailable Central is reported as one actionable message instead of numbers: not installed, cannot be
used, not signed in (sign in in the JetBrains (Air Gateway) pane of Settings › Plugins › Model providers), outdated, or credits unreadable. The UI-side
contribution carries no account.

## Listing access sources


`central access` output is not parsed. Listing runs automatically, with no button, only while an agent's Model source is Central and Central is supported and signed in: when the pane opens, when an agent starts using Central, after sign-in, after a Switch (which also refreshes the credits) and on the throttled activation check (at most once per 15 s). Until then the pane says what it waits for (checking, Central not installed or unavailable, sign in to Central) and with no agent on Central it shows nothing. The listing runs in the host half; a result arriving after a newer request, the source changing or the pane closing is dropped. It runs:
`central login export` (16 KiB, 15 s) yields a version-1 `central-<base64url>` envelope with `environment`,
`refresh_token` and the current `ai_access` selection. The refresh token is exchanged (`client_id=central-cli`) at
the fixed OAuth endpoint of that environment and the access token is sent to JetBrains Cloud's
`user-management/api/ai-access-options`. Only `prod` and `staging` are known; both endpoint pairs are a fixed HTTPS
table in the host half, so nothing in the envelope or a response can name a host. Enabled workspaces and licenses give
the selection ids (`workspace:<org>:<ws>`, `license:<id>`) and display names; the exported selection gives the
current marker.

Everything fails closed with a specific pane message: no export (signed out), a malformed envelope, an unknown
environment (nothing is sent), a redirect (never followed), any HTTP error, a response over 64 KiB, a malformed token
or options response, a timeout (15 s), and a refresh token that differs from the exported one. Only a well-formed
empty list says Central listed no selectable sources. The envelope and endpoint are internal interfaces of Central
and JetBrains Cloud, not a documented listing contract, and may need updating when they change. Tokens stay in
locals and byte buffers that are cleared after use (strings from the JSON reader cannot be cleared and are dropped
for collection); nothing is logged, stored, thrown or sent to the pane. Listing runs one at a time.

Compatibility risk: listing refreshes Central's credential independently of Central. If the OAuth server
rotates the refresh token and revokes the original, rejecting the replacement does not undo that server-side change;
Central would keep an invalid credential and every app sharing its session could be signed out. This is conditional,
not a reproduced failure: a live check saw two exchanges return the identical refresh token (RFC 6749 section 6 permits
rotation). A Central-owned authenticated listing operation or a supported
credential synchronization contract would remove the assumption.

Switches serialize behind the action gate; a request for the source queued last shares that switch, any other source
queues its own, so A, B, A runs in order and ends on A.

## Untrusted output

Central's output and the endpoint responses are data. Only the version, the sign-in and proxy verdicts, exit codes and
failure reasons (as fixed messages) and access-source display names reach the pane; names are the one derived text
shown (as in the fork), stripped of control characters and capped at 80 characters. Selection ids must match `workspace:<id>[:<id>]` or
`license:<id>` before they are passed back as an argument. The plugin stores and logs no credentials, tokens or
Central output, and never passes `--accept-tos`.

## Threading

All process and filesystem work is in the host half behind plugin RPC; the pane only builds controls and applies
replies. Mutating calls are serialized and publish a busy status on the `status` channel before and after.

## Verification and limits

`--air-gateway` runs the host and pane checks against a fake `central` modeled on the fork's fixture (version,
status rows, `login export`, access switching, proxy, login, update) plus a `run` that records its argv, under a throwaway home: detection,
the contributions per agent Model source (Claude Code, Codex and Pi), the Claude and Codex launch lines executed by a real shell so prompts with
quotes and `$(...)` arrive as single arguments, a fake HOME whose `.claude`, `.codex`, `CLAUDE_CONFIG_DIR` and
`CODEX_HOME` stay byte- and time-identical across every action and Model source change, failures, access switching, the
host seam over plugin RPC (including the provider contributed and withdrawn, and Model sources followed), the pane (no source choice, no agent list) and real launchers through headless input, and the header
item against a stand-in host: hidden with no agent on Central, credits after a Model source change, the warning
color, the reason when credits cannot be read, a pushed status, the click into Settings, and removal on disable.
The listing runs against a loopback fake of the OAuth server and Cloud (a checks-only endpoint override that production
code never sets): success with the current marker, signed out, unknown environment, redirect, malformed and oversized
responses, rotation, secrets absent from every surfaced string, switching with proxy-restart failure and A, B, A.
Not verified against the real CLI or production endpoints (never called from checks): the live envelope and endpoint,
the exact `central status` output format, that
`central run <agent> -- <args>` forwards tereditor's prompt, plugin-dir and MCP arguments and environment to the agent
unchanged, how `central run` picks the access source, and the ToS prompt of `central access`. The parsers follow the
fork's.
