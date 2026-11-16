---
id: module-plugin-providers
type: module-design
status: active
title: Model providers builtin
parent: group-plugins-agents
---

# Model providers builtin

Settings › Model providers lists the model providers the user can give to an agent, keeps their keys, and records which provider each
agent uses. Enabled by default; not headless because the keys go into files of the host's operating system. Agents never depend on
this plugin: each agent plugin declares what it accepts (`agentRouting` in its manifest, Plugin API) and works the same with this plugin
off. This plugin is the optional side that matches providers to those declarations.

## Model providers and agents

- Two entities. A model provider declares the APIs it offers: for each, a kind (`anthropic-messages`, `openai-responses`, `openai-chat`,
  `google-generative`, `vertex`, `bedrock`), a base URL and the credential kinds (`api-key`, `oauth`). An agent declares what it accepts.
  A provider is offered to an agent when they share an API kind and a credential kind. There is no translation proxy: an agent that takes
  only the Anthropic API is never offered a provider that speaks only OpenAI Chat.
- Sign-in policy. A provider's `oauth` field says whether the vendor allows the sign-in (`allowed`), the flow, the vendor, a note and the
  reason when it is not allowed. A sign-in counts for matching only when `allowed` is true. Anthropic and Google are never allowed, with a
  fixed reason and no override. OpenAI is allowed with a note for remote hosts. A vendor the table does not name, or a provider with no
  policy, is not offered; its policy says the user could mark it, but no mark or sign-in flow exists yet.
- Bundled providers need only an endpoint and a key and are data rows: Anthropic API, OpenAI API, OpenRouter, DeepSeek, Mistral, Groq,
  Together, Fireworks, xAI, Cerebras. A provider that needs more (JetBrains AI through Air Gateway, OpenAI with sign-in, Vertex, Bedrock,
  Azure) is its own plugin in the Model providers category that depends on this one. It contributes through the contribution contract.
  Air Gateway is not moved yet.
- The contribution contract. A dependent plugin calls `contribute` with its id and provider definitions, and `withdraw` when it stops;
  contributed ids cannot take over a bundled or earlier id. Its UI fills a place of the page with `SlotSection`: `providers:jetbrains-ai`,
  `providers:hyperscalers` or `providers:other`.
- The page has the groups JetBrains AI and Cloud platforms (each hidden, heading included, until a plugin fills it), Other routers and
  providers (the bundled rows and the user's own), and an Add a custom provider form: a name, a base URL, the API kinds it speaks and an
  optional key (none for a local endpoint). A custom provider is stored in this plugin's state without its key. A plain `http://` address is
  accepted only on this computer. Below stays the page for the keys Pi and omp read from their own files.
- The model source of an agent is recorded in this plugin's own state (`modelSources`), per agent plugin id: none (the default, tereditor
  does not step in) or a ready provider, that is one with a key stored or none needed. Removing a key, a custom provider or a contributor
  drops the choices that used it. Each agent settings page embeds the slot (`core:model-source:<id>`): a list of the agent's own provider
  and the ready providers the agent is offered. For now the choice is only recorded; it does not change how an agent starts.

## Key store

- One host-side store: `<state>/providers/keys/<provider id>` (state is `TEREDITOR_STATE_DIR`, else `~/.tereditor`). The directory is 0700,
  each file 0600; a key is written to a temporary file that is owner-only from the start, then moved over the old one. A key is one line.
  tereditor never reads a key back.
- The `provider-key <id>` helper prints a stored key to standard output and nothing else. The host writes it into `<state>/providers/bin`
  when a key is first stored: a POSIX shell script (`provider-key`) and a Windows `provider-key.cmd`, each with the keys directory
  built in, so it needs nothing shipped with the app and works in the NativeAOT host and the headless archive. It refuses an id that is
  not a plain provider id. An agent calls it through its own credential-helper setting: Claude Code `apiKeyHelper`, Codex `auth.command`,
  Pi and omp `!command`. A real key is never put into an argument, an environment variable, a wrapper, a revive line, host state, a log,
  a settings file or a report.

## Acceptance contract (keys for Pi and omp)

- Keys only. No subscription or OAuth sign-in is offered or started. In particular Anthropic is offered by API key (Claude Console) with the
  note that a Claude subscription sign-in is not supported here (JetBrains/thinkrail#437). A Claude sign-in Pi already stored is shown as signed in
  with a warning; adding an API key replaces it only when the user does so.
- Pi: `auth.json` in the agent directory (`PI_CODING_AGENT_DIR`, else `~/.pi/agent`): `{"<provider id>": {"type": "api_key", "key": "..."}}`.
  Other entries, including sign-ins, are kept as they are. The catalog is the table in Pi's `docs/providers.md`.
- omp: `<omp home>/.omp/profiles/tereditor/agent/.env` (`TEREDITOR_OMP_HOME` moves the home, as for the omp plugin), one `NAME=value` line per
  provider variable (https://omp.sh/docs/env); omp reads it for launches with tereditor's profile. Only the named variable's line is replaced or
  removed; other lines and comments stay. omp's own credential database is never written. The catalog is the variable table on that page.
- A provider the catalog does not list is added by hand with an id, an optional name and, for omp (and optionally Pi), the variable that holds its
  key. Custom providers are remembered in the plugin's state without their keys; removing the key forgets the provider.
- A key is validated (one line, not empty), written once and never read back: a report carries ids, names, variables and where each credential
  comes from (stored key, sign-in, the host's environment), never a key. Files are rewritten whole through a temporary file readable by the owner
  only, then moved over the original. Keys are not logged.
- The page is open whatever plugins are on: Pi is always offered, and omp is offered unless its plugin is refused (a host without omp, such as a browser tab). A missing program does not hide an agent. The page follows the agent roster's change notification (Plugin API, `AgentRoster`). The Pi / omp switch shows whenever omp is offered; where omp is refused, Pi is the only agent and there is no switch. The page has a Pi / omp switch, the file the keys live in, Connected rows (Replace key, Remove), Available rows (Add key) and the custom form;
  keys are typed into masked fields and cleared as soon as they are sent. Reads and writes run off the UI thread on the host.

## Checks

`--providers` (gate case `providers`): the manifest; Pi: sign-in preserved and flagged for Anthropic, keys added, replaced and removed without
leaking into a report, owner-only permissions, custom providers; omp: the `.env` line replaced or removed with other lines kept, quoting, catalog
variables, custom variables and their validation. Also what each agent declares and that none depends on this plugin, matching including the
sign-in policy, the key store (0600 and 0700, atomic, a sentinel key found only in its own file), the helper's output and refusals, custom providers
(validation, add, remove), contributions, the recorded model source and the wire contract. `--providers-ui` (gate case `providers-ui`) mounts the
page and the slot headless: the three groups, a group shown only once a component fills it, adding a key and a custom provider without showing the
key, the slot's choice filtered by each agent's declaration and recorded, and the slot with this plugin off being only the hint. `--case
agent-launch-contributions` checks that with no contribution each agent's launch line is unchanged.
