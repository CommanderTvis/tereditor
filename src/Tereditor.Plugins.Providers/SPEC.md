---
id: module-plugin-providers
type: module-design
status: active
title: Model providers builtin
parent: group-plugins-agents
---

# Model providers builtin

Settings › Model providers lists the model providers the user can give to an agent, keeps their keys, records which provider each
agent uses, and starts Pi and omp with it. Enabled by default; not headless because the keys and the agents' models files are files of the host's operating system. Agents never depend on
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
  accepted only on this computer. This is the only provider page: there is no per-agent key page.
- The model source of an agent is recorded in this plugin's own state (`modelSources`), per agent plugin id: none (the default, tereditor
  does not step in) or a ready provider, that is one with a key stored or none needed. Removing a key, a custom provider or a contributor
  drops the choices that used it. Each agent settings page embeds the slot (`core:model-source:<id>`): a list of the agent's own provider
  and the ready providers the agent is offered. For Pi and omp the choice is live (see Launch seams); for the other agents it is only recorded for now.

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

## Launch seams (Pi and omp)

- A chosen source writes the agent's models file first; a source whose file cannot be written is not recorded. The file names the provider's
  endpoint and gives the key as a `!command` that runs the `provider-key` helper with the provider id, quoted for the host's shell. No key is
  ever in a models file, an argument, an environment variable, a launch line, host state or a log. The files are rewritten at startup, and
  removed when the source is cleared or its provider or key goes. Reads and writes run off the UI thread on the host.
- The API used is the one both sides declare. A provider the agents know by id (Anthropic, OpenAI, OpenRouter, Mistral, Groq, xAI, Cerebras)
  keeps its own API and only gets the endpoint and the key. Any other provider is written with its API (`anthropic-messages`,
  `openai-responses` or `openai-completions`). A provider that needs no key is written without a helper (Pi `apiKey: "none"`, omp `auth: none`).
  Pi and omp need a model list for a provider they do not know; none is written yet, so such a provider works only where the agent can list
  its models itself.
- Pi: `<state>/providers/pi/agent/models.json`. Pi runs with `PI_CODING_AGENT_DIR` set to that directory, so its sessions live there too
  and its own sign-in is not read. tereditor never creates or changes `auth.json`. Pi's own files stay as they are.
- omp: `<omp home>/.omp/profiles/tereditor/agent/models.yml` (`TEREDITOR_OMP_HOME` moves the home, as for the omp plugin). omp starts with
  `--profile tereditor`, with or without MCP. omp's own credential database is never written.
- Both are delivered as an agent launch contribution (Plugin API `AgentLaunch`): Pi gets only the `PI_CODING_AGENT_DIR` variable, omp only
  the `--profile tereditor` argument. The contributor reads an in-memory snapshot. With this plugin off, or no source chosen, no contribution
  exists and each launch is byte-identical to what the agent builds itself.

## Checks

`--providers` (gate case `providers`): the manifest; what each agent declares and that none depends on this plugin, matching including the
sign-in policy, the key store (0600 and 0700, atomic, a sentinel key found only in its own file), the helper's output and refusals, custom providers
(validation, add, remove), contributions, the recorded model source and the wire contract (version 3, no per-agent key-file methods). The
launch seams: the models files of Pi and omp with a helper command and no key (the command run by a shell prints the key), Pi's directory holding
no `auth.json`, the contributions carrying no key, the omp line unchanged without a source and with one profile flag with it, custom and keyless
providers, and startup, clearing and removal. `--providers-ui` (gate case `providers-ui`) mounts the page and the slot headless: the three
groups (shown only once a component fills one), a contributed component, adding a key and a custom provider without showing the key, the slot's choice filtered by each
agent's declaration and recorded, and the slot with this plugin off being only the hint. `--pi` checks that a contributed variable reaches the Pi
child's environment. `--case agent-launch-contributions` checks that with no contribution each agent's launch line is unchanged.
