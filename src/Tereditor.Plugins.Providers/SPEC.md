---
id: module-plugin-providers
type: module-design
status: active
title: Model providers builtin
parent: group-plugins-agents
---

# Model providers builtin

Settings › Plugins › Model providers lists the model providers the user can give to an agent, keeps their keys, records which provider each
agent uses, and starts Pi, omp, Codex and Claude Code with it. Its page is a plugin page, nested under Settings › Plugins like every other plugin's. Enabled by default; not headless because the keys and the agents' models files are files of the host's operating system. Agents never depend on
this plugin: each agent plugin declares what it accepts (`agentRouting` in its manifest, Plugin API) and works the same with this plugin
off. This plugin is the optional side that matches providers to those declarations.

## Model providers and agents

- Two entities. A model provider declares the APIs it offers: for each, a kind (`anthropic-messages`, `openai-responses`, `openai-chat`,
  `google-generative`, `vertex`, `bedrock`), a base URL and the credential kinds (`api-key`, `oauth`). An agent declares what it accepts.
  A provider is offered to an agent when they share an API kind and a credential kind. There is no translation proxy: an agent that takes
  only the Anthropic API is never offered a provider that speaks only OpenAI Chat.
- Sign-in policy. A provider's `oauth` field says whether the vendor allows the sign-in (`allowed`), the flow, the vendor, a note and the
  reason when it is not allowed. A sign-in counts for matching only when `allowed` is true. Anthropic and Google are never allowed, with a
  fixed reason and no override. OpenAI is allowed with a note for remote hosts, and not allowed in the Playground (`For(vendor, browser)`; reason: it needs a local helper). A vendor the table does not name, or a provider with no
  policy, is not offered; its policy says the user could mark it, but no mark or sign-in flow exists yet.
- Bundled providers need only an endpoint and a key and are data rows: Anthropic API, OpenAI API, OpenRouter, DeepSeek, Mistral, Groq,
  Together, Fireworks, xAI, Cerebras. A provider that needs more (JetBrains AI through Air Gateway, OpenAI with sign-in, Vertex, Bedrock,
  Azure) is its own plugin in the Model providers category that depends on this one. Amazon Bedrock, Google Vertex AI and JetBrains AI through Air Gateway exist (Cloud platforms and JetBrains AI (Central) below). It contributes through the contribution contract.
- The contribution contract. A dependent plugin calls `contribute` with its id and provider definitions, and `withdraw` when it stops;
  contributed ids cannot take over a bundled or earlier id. A definition may name `WiredAgents` (agent plugin ids): the contributor starts those agents itself, so they are offered the provider with no shared API, this plugin writes no file and adds no launch word for them (`ProviderLaunch` returns nothing), and a source naming an agent outside the list is refused. Startup keeps a choice of `jetbrains-ai` whose contributor has not started yet. `ProviderMatching.Works` and `AgentNames` say which agents a provider works with. Its UI fills a place of the page with `SlotSection`: `providers:jetbrains-ai`,
  `providers:hyperscalers` or `providers:other`. A slot is a place in the one list, not a heading.
- The page is a master-detail (`MasterDetail`, the rail is resizable like Settings › ACP): a rail of providers on the left, one row each with an icon, the name, a muted line "Works with Claude Code, Codex" and a mark (a check when a key is set or none is needed, "Not set up" otherwise), under the headings Set up and Not set up; the selected provider's pane on the right. The rail is sorted by popularity inside each heading (`ProviderOrder`, the only place of the order): OpenAI, Anthropic, Google, then JetBrains AI, OpenRouter, Together, Fireworks, then Amazon Bedrock and Google Vertex AI; every other provider (the rest of the bundled rows and the user's own) follows by name. A plugin block that fills a slot takes the rank and the rail row of the provider it names (`ModelProvider-<id>`, or `<Id>Component`), which replaces that provider's catalog pane; it shows in the pane only while its row is selected, and a block that names no provider gets a row of its own (an unconfigured Amazon Bedrock or Google Vertex AI component is found by its `<Id>Component` view and listed under its accessible name with its help text as the "Works with" line, never under its plugin mount's name `PluginMount_<id>`). A row without a lock icon: the icon column is dropped when a row has no icon, so the text starts at the left. A rail row has a red dot when an agent has the provider as its model source while it has no key (a provider nobody uses and with no key is only Not set up); the tooltip and the top of the provider's pane say so in one sentence. A catalog provider's pane has its name, the agents it works with, the hints (the note, why a sign-in is not offered) and the key buttons. Under the rail is an Add a custom provider button that opens a form in the pane: a name, a base URL, the API kinds it speaks and an
  optional key (none for a local endpoint). Each rail row says what the provider is for as the agents it works with ("Works with Claude Code, Codex"), taken from the agents' declared routing and the contributor's wired agents, not as API kinds. A custom provider is stored in this plugin's state without its key. A plain `http://` address is
  accepted only on this computer. This is the only provider page: there is no per-agent key page.
- The model source of an agent is recorded in this plugin's own state (`modelSources`), per agent plugin id: none (the default, tereditor
  does not step in) or a ready provider, that is one with a key stored or none needed. Removing a key, a custom provider or a contributor
  drops the choices that used it. Each agent settings page embeds the slot (`core:model-source:<id>`): a list of the agent's own provider
  and the ready providers the agent is offered. For Pi and omp the choice is live (see Launch seams), and for Codex (Codex launch seam below) and Claude Code (Claude Code launch seam below) too; for the other agents it is only recorded for now.

## Cloud platforms and the Claude Code seam

- A Cloud platforms provider is keyless: its API takes the credential kind `platform` (`ModelAuthKind.Platform`), the platform's own credential chain (an AWS profile, Google application default credentials). tereditor stores no secret for it and never reads one. It is ready as soon as it is contributed. Matching treats `platform` like a key: an agent is offered it when it declares the same API kind with `platform`. Today only Claude Code declares `bedrock` and `vertex`, both injected through the environment (`AgentInjection.Environment`); Codex, Pi and omp declare neither, so they are offered neither.
- A provider carries `Environment`: the non-secret variables a launch sets (region, profile, project). The Claude Code seam (`ProviderLaunch.For` for launcher `claude`, source recorded under `claude-code`) returns exactly those variables and nothing else. A source that is not a platform provider has no Claude Code seam and stays recorded only. With no source, no report or Model providers off, the Claude line is the agent's own. On a Windows shell the variables would be dropped, so the launch is blocked with a reason instead of running on another account.
- The platform plugins (Amazon Bedrock, Google Vertex AI) contribute only while their settings are complete and safe, and withdraw otherwise, which drops a source that used them.

## JetBrains AI (Central)

The Air Gateway plugin depends on this one, has no settings page of its own (its Central pane is the provider's pane here) and contributes `jetbrains-ai` ("JetBrains AI (Central)"): no key, no API, `WiredAgents` Claude Code, Codex and Pi (the agents Central declares wiring for; omp and the rest are not offered it). It is offered in those agents' Model source and has one row in the list at its popularity place (`ModelProvider-jetbrains-ai`). Choosing it records the source here and writes nothing else: Air Gateway follows the `models` channel and contributes `central run <agent> --` to that agent's launches (see its SPEC). There is no global switch and no per-agent list in the Air Gateway page. If Air Gateway is off or unavailable (the Playground) the provider is absent and a recorded choice is dropped with it.

## Codex launch seam

- Providers contributes to Codex launches in both halves (`AgentLaunch`), for launcher `codex` only; every other launcher gets nothing. Each half reads the latest merged report from memory. The host pushes it on the `models` channel after every change (wire version 3), so a launch never reads a file. The report carries the path of the `provider-key` helper, never a key.
- With no source chosen for Codex, with no report yet, or with Model providers off, the contribution is absent and the Codex line is byte-identical to today (`--agent-launch-contributions` pins it).
- With a source chosen the contribution adds arguments (a TOML literal string each, so Windows quoting keeps them): `-c model_provider='tereditor-<id>'`, and under `model_providers.tereditor-<id>` the `name`, `base_url` (the provider's OpenAI Responses API), `wire_api='responses'` and, for a provider that needs a key, `auth.command` (the helper) and `auth.args=['<id>']`. The `tereditor-` prefix keeps clear of Codex's built-in provider ids. The key stays in the key store; Codex runs the helper itself. A name or address Codex cannot take, a source that is not ready, or a missing helper blocks the Codex launch with a reason, so it never starts on another account.
- A launch that starts before the first report reaches a half is the agent's own.

## Claude Code launch seam

- A chosen source for Claude Code writes a generated settings file first, `<state>/providers/claude-code/settings.json` (0600, a source whose file cannot be written is not recorded). It holds `apiKeyHelper`, the `provider-key` helper and the provider id quoted for the host's shell, and `env.ANTHROPIC_BASE_URL`, the provider's Anthropic Messages endpoint. A provider that needs no key gets no helper and a placeholder `ANTHROPIC_AUTH_TOKEN` of `none`, because Claude Code will not start without a credential. A key is never in the file, an argument or a variable.
- Only providers with an Anthropic Messages API and an API key are offered (the plugin's `agentRouting`); the others cannot be chosen. OpenRouter, DeepSeek, xAI and the Anthropic API qualify among the bundled rows. Claude Code takes no sign-in, so no OAuth source applies.
- Providers contributes to launcher `claude` in both halves (`AgentLaunch`, like Codex), from the latest report in memory: the only change is the arguments `--settings <file>`, with the file path derived from the helper path in the report (`providers/bin/provider-key` gives `providers/claude-code/settings.json`), so a launch never reads a file. The Claude Code plugin composes it with its own `AgentLaunchComposition` rule and does not depend on this plugin. The generated `--settings` comes first and the user's other arguments and leading `NAME=value` variables stay as written. Two `--settings` may not merge, so a `--settings` (or `--settings=`) in the user's own command blocks the launch with "The Claude command has its own --settings, so the model source cannot apply. Remove it or clear the source." (the contribution's `ConflictingArguments` and `ConflictReason`, checked by `AgentLaunchComposition.Apply` for the terminal line and by the chat launch). The source's endpoint is set in the settings file's `env`, which Claude Code applies over an `ANTHROPIC_BASE_URL` of the same command.
- With no source chosen, no report or this plugin off, the line is byte-identical to today. A source that is not ready, has no Anthropic API, a missing helper or an unquotable path blocks the launch with a reason (the line prints it and exits), so it never starts on another account.

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
- The API used is the one both sides declare. A provider an agent knows by id keeps its own API and only gets the endpoint and the key (Pi: Anthropic,
  OpenAI, OpenRouter, DeepSeek, Mistral, Groq, xAI, Cerebras, Fireworks; omp: those and Together). Any other provider, custom ones included, is written
  as a generic entry of the agent's own adapter (OpenAI Chat, else OpenAI Responses, else Anthropic Messages) with a model list: the provider's
  `models`, else the model given with the source, else `default`. Clearing an omp source removes `models.yml` only when it starts with the
  tereditor marker line. A provider that needs no key is written without a helper (Pi `apiKey: "none"`, omp `auth: none`). At startup a source whose provider or key is gone, or whose file cannot be written, is
  dropped, so the recorded sources always match what launches use.
- Pi: `<state>/providers/pi/agent/models.json`. Pi runs with `PI_CODING_AGENT_DIR` set to that directory, so its sessions live there too
  and its own sign-in is not read. tereditor never creates or changes `auth.json`. Pi's own files stay as they are.
- omp: `<omp home>/.omp/profiles/tereditor/agent/models.yml` (`TEREDITOR_OMP_HOME` moves the home, as for the omp plugin). omp starts with
  `--profile tereditor`, with or without MCP. omp's own credential database is never written.
- Both are delivered as an agent launch contribution (Plugin API `AgentLaunch`): Pi gets only the `PI_CODING_AGENT_DIR` variable, omp only
  the `--profile tereditor` argument, which the omp plugin reads as a signal and writes once itself, so a custom command keeps the profile. The UI half contributes
  the same omp profile from the sources its pages last showed, because the launcher builds its line there. The contributor reads an in-memory snapshot. With this plugin off, or no source chosen, no contribution
  exists and each launch is byte-identical to what the agent builds itself.

## Checks

`--providers` (gate case `providers`): the manifest; what each agent declares and that none depends on this plugin, matching including the
sign-in policy, the key store (0600 and 0700, atomic, a sentinel key found only in its own file), the helper's output and refusals, custom providers
(validation, add, remove), contributions, the recorded model source and the wire contract (version 3, the models channel, no per-agent key-file methods). The Claude Code seam: the generated settings file (helper command, endpoint, no key, 0600, the command printing the key when a shell runs it), the contribution being only `--settings` with the derived path and never a key, the user's own variable and arguments surviving in the terminal line, and their own `--settings` blocking it, keyless and blocked sources, providers without an Anthropic API refused, and clearing, removal and startup. `--providers-ui` also reads the four agent pages and requires Model source before every other setting. The
launch seams: the models files of Pi and omp with a helper command and no key (the command run by a shell prints the key), Pi's directory holding
no `auth.json`, the contributions carrying no key, the omp line unchanged without a source and with one profile flag with it, custom and keyless
providers, and startup, clearing and removal. `--cloud-platforms` (gate case `cloud-platforms`) covers the platform plugins and the Claude Code seam (see their specs). `--providers-ui` (gate case `providers-ui`) mounts the page and the slot headless: the three
groups (shown only once a component fills one), a contributed component, adding a key and a custom provider without showing the key, the slot's choice filtered by each
agent's declaration and recorded, and the slot with this plugin off being only the hint. `--pi` checks that a contributed variable reaches the Pi
child's environment. `--case agent-launch-contributions` checks that with no contribution each agent's launch line is unchanged. A wired provider (JetBrains AI (Central)) is checked to be offered to exactly its agents, to write no file and add no launch word, to survive a start before its contributor, and to leave with it; the page rows read as the agents a provider works with.
