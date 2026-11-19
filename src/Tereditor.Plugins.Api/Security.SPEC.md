---
id: module-plugin-security
type: module-design
status: active
title: Plugin security — one API, an in-process runtime for trusted plugins and a sandboxed one for the rest
parent: module-plugin-api
references: [submodule-host-plugins, submodule-ui-plugins, module-plugin-ui-kit, module-host-headless]
tags: [plugins, security]
---

# Plugin security

Today a plugin is code in the host process and in the app, with the user's full rights
([Trust](SPEC.md#trust)). This spec replaces that for plugins tereditor does not ship. The plugin API stays the
contract; what changes is where an untrusted plugin runs and what it can reach from there. When this spec is
adopted, it supersedes the "There is no sandbox" paragraph and "sandboxing or crash isolation" in the API spec's
out-of-scope list.

## The two invariants

A UI half has the whole of Avalonia and the same UI kit the app uses. It runs in its plugin's one process, beside
the host half, so it reaches the computer exactly as the plugin does: through the API and the plugin's grants, and
nothing else. No client runs plugin code.

A host half has nothing except the API. It reaches the computer only through capabilities the user granted,
each scoped as narrowly as the plugin's manifest asked for, and the user is at least informed of every grant.

Both invariants are enforced by the operating system, not by the language. A plugin that bypasses the SDK and
writes raw bytes to its channel gains nothing, because every check happens on the trusted side.

## Where the code runs

The two runtimes, the channel and runner, the rules for calls across the boundary and the UI protocol are in the [Plugin API spec](SPEC.md#isolated-runtime). This spec covers what the sandbox and the grants allow.

## Processes and sandboxes

An external plugin runs in one process, both halves together, on the host machine, which is where files and processes
live. That process serves every connected client. The app never runs external plugin code, wherever it is. The UI
half therefore holds every grant the host half holds: the plugin as a whole has exactly its grants, and a crash or
hang in either half fails the plugin for every client.

The runtime creates the channel (a socketpair or pipe), starts the plugin runner with that one end inherited, and
applies the sandbox before any plugin code runs. The sandbox forbids opening any other channel. The plugin's
identity is the connection: the broker knows which plugin it is talking to because it created the pipe, and never
reads an id, path or client key the plugin claims.

Inside the sandbox a plugin process can read its plugin directory and the runner, read and write its own state
directory (H19) and a private temporary directory, and use its channel. Nothing else.

Linux uses Landlock and seccomp, applied by a small helper (`tereditor-confine`, C built with the pinned Zig) that
then executes the runner: Landlock allows reading the plugin and runner directories and writing only the state and
temporary directories, and seccomp refuses sockets, other processes' memory, `io_uring` and terminal injection. This
replaced bubblewrap (namespaces and binds), because Ubuntu 24.04 restricts unprivileged user namespaces through
AppArmor, and the helper needs no external binary and no privilege. The costs: Linux 5.13 at least, and 6.7 for
Landlock's network rules (below that, seccomp alone blocks sockets); without PID and mount namespaces a plugin can
learn which paths exist (`stat`) though it cannot read them; whether it works inside containers, where headless
tereditor's image runs, is unverified until the Linux sandbox slice probes it. macOS uses a Seatbelt profile generated per plugin (unconfirmed:
`sandbox_init` with a generated profile, rather than the App Sandbox, which needs entitlements per binary).
Windows uses an AppContainer with no capabilities (unconfirmed). A host that cannot apply its sandbox refuses
external plugins with that as the reason, rather than running them unconfined.

### Headless tereditor

Headless tereditor (`tereditor-headless`, see [module-host-headless](../Tereditor.Host.Headless/SPEC.md)) is the third
host composition, beside the app's own host and the remote host (`RemoteServer.Create`). It serves MCP over stdio for
one workspace and has no client: no window, no app and no remote client. Its image is `ghcr.io/commandertvis/tereditor`.

- It is published as NativeAOT, like the other two ([Headless host](../Tereditor.Host.Headless/SPEC.md)).
- It runs as `HostKind.Headless`. A builtin plugin runs there only when its manifest says `Headless = Yes`, and a
  plugin that depends on a refused one is refused too ([Plugins](../Tereditor.Host.Core/Plugins.SPEC.md)).
- External plugins in `<stateDir>/plugins` are found and listed, and refused for now: the isolation seam is null there.
  Once the isolated host half exists, an external plugin runs there only when its manifest says `Headless = Yes`. A
  manifest that says nothing counts as `No`. Such a plugin is isolated exactly as on other hosts: one sandboxed process,
  the same broker, protected set and audit log. Only its host half runs. There is no client, so no UI half activates
  and no surface opens.
- No app draws a prompt. A plugin's grants come only from a grants file the user writes in the headless state
  directory (`TEREDITOR_STATE_DIR`, else `~/.tereditor/headless`). Enabling a plugin grants nothing there, not even the
  enable-time set. A permissioned call without a grant in the file is denied at once, with no prompt and no wait, and
  is written to the audit log. A full-trust grant needs its own explicit entry in the file, like any other grant. The
  file sits in the state directory, so it is in the protected set. Its format, and how an entry is bound to the
  plugin's content hash, are settled by the grants slice; neither exists yet.
- The sandbox applies inside a container too: the image confines a plugin with `tereditor-confine` like any Linux
  host. Whether Landlock and seccomp work inside a container is unverified until the Linux sandbox slice probes it.
  A container that cannot apply the sandbox refuses external plugins with that reason, as any host does.

## Filesystem and secrets

The central promise is about directories. A plugin may change the user's source, which cannot be prevented,
because that is what a plugin that edits code does. A plugin must never read the user's secrets: `~/.ssh`, cloud
and token files, keychains, browser profiles, shell history, or tereditor's own credentials and state.

### What a sandbox sees

The sandbox has an allowlist view of the filesystem and no home directory. A plugin process sees only its own
plugin directory and the runner (read-only), its own state and temporary directories, and its channel. Everything
else is reached through the broker, which opens the file itself.

The broker's file scope is a set of roots, never "everything the user can read". The default scope is the
workspaces the plugin is enabled for. A scope can be widened only to a specific directory the user names, never
to a home directory or a filesystem root.

### What no grant reaches

A protected set is denied for reads and writes whatever the scope says, and no prompt offers to grant it:

- Credentials and keys: `~/.ssh`, `~/.gnupg`, `~/.aws`, `~/.azure`, `~/.config/gcloud`, `~/.config/gh`,
  `~/.docker`, `~/.kube`, `~/.netrc`, `~/.npmrc`, the platform keychains and credential stores, and browser and
  mail profiles.
- tereditor's own state directory, which holds other plugins' settings, tokens and the grants themselves. A
  plugin reaches only its own subtree, `<stateDir>/plugin-state/<id>/`, through `ReadState`/`WriteState`.
- Shell startup and history files in the home directory, since a write there is code execution outside the
  sandbox.
- Inside a granted workspace: key and certificate files (`id_*`, `*.pem`, `*.key`, `*.p12`), `.env` files and
  other files matching the user's secret patterns are readable only under a separate `files.secrets` grant.
  Writes to `.git/hooks` and `.git/config` are denied, since they run code at the user's next commit.

The protected list is data in the host, extended per platform, and it is the same list the sandbox profile and
the broker use. Unconfirmed: that the list is complete for Windows and for each Linux desktop.

### How the broker holds the line

- It canonicalises every path, resolving `..` and symlinks, and checks the resolved target against the scope and
  the protected set. A symlink inside a workspace that points at `~/.ssh` is refused as its target.
- It opens relative to a directory descriptor with no-follow semantics and checks the opened object, so a path
  swapped between check and open (a time-of-check race) cannot redirect the read.
- It passes back data or a read-only descriptor for the one file opened, never a directory descriptor.
- A user-chosen path (`PickFileAsync`, a viewer's open file) grants that path for the activation. A pick inside
  the protected set is refused, not confirmed.

### Programs started on a plugin's behalf

A program the broker starts is not bound by the plugin's sandbox unless the broker confines it, and a program
that can read files can read the protected set and hand it to the plugin through its output. So a program a
plugin starts is run in one of two ways:

- Confined, the default. It runs in a sandbox built from the same file scope and protected set as the plugin,
  with no network unless `net:` was granted and no secrets in its environment. Language servers, formatters and
  most tools work here.
- Unconfined, for a tool that has to use the user's credentials (git over SSH, a cloud CLI). This needs its own
  grant, named by executable, and is described as reading everything the user can read. Only a fixed argument
  shape is allowed for the tools tereditor knows, such as `GitAsync`: the broker rejects options that make the
  tool run other programs or read other files (`-c`, `--upload-pack`, `core.sshCommand`, `GIT_*` environment).

Any grant that lets a plugin run arbitrary programs unconfined, type into terminals, read terminal output or
environment, change what shells and agents run, or put text in an agent's instructions can reach the protected
set by that route. These are the full-trust grants. The prompt shows them in a separate group above the rest
with the words "can read anything you can", and they are never part of the enable-time grants.

## Capabilities and permissions

A capability is a part of the API that acts outside the plugin's own namespace. In the isolated runtime it exists
only if the user granted it. Holding the handle is the permission, so an API member never has to remember an
`if (allowed)` check. Calling a capability that was not granted fails with a denied error naming the permission.
It never returns an empty result that looks like a real one.

The rule is: anything that reads the user's data outside the plugin's own state, reaches the network, or causes
code to run on the user's machine needs a permission. That includes indirect routes. Text typed into a shell,
environment injected into a shell, a wrapper around an agent launch, a Git run with arbitrary arguments, and text
placed in an agent's instructions are all code execution, and are classified that way.

### Every member, classified

This covers every member of `IPluginHostContext`, `IPluginUIContext` and the interfaces and registrations
reachable from them, as listed in the three `PublicAPI.Unshipped.txt` files.

Ambient members need no permission. They are granted by enabling the plugin and are listed on its card:

- Its own namespace: `Id`, `Log`, `Method`, `Publish`, `Settings`, `OnSettings`, `UpdateSettingsAsync`,
  `ReadState`, `WriteState`, `StateDirectory`, `AssetsDirectory`, `ReadAssetAsync`, `Preference` (`Get`, `Set`,
  `Remove`), `RequestAsync`, `Subscribe`, `Invalidate`.
- Declared dependencies: `Dependency`.
- Workspace metadata: project and workspace names, paths and branches, and the projection's terminal tabs and
  roster. This covers `Projects`, `TerminalTitle`, `WorkspacesAsync`, `WorkspaceAsync`, `WatchWorkspaceAsync`, `OnWorkspace`,
  `Host`, `WatchHost`, `OnReconnect` and `OnWorkspaceRemoved`.
- The instruction texts plugins compose for agents, which the agents read anyway: `PromptTexts` and
  `ToolGuidanceSections`.
- UI surfaces: `SideTool`, `Companion`, `FocusCompanion`, `FileViewer`, `SettingsSection`, `SlotSection`,
  `HeaderItem`, `WorkspaceAction`, `TabDecoration`, `FileIconSlot`, `DocumentLinkSlot`, `FileAction`,
  `TerminalTabAction`, `KeymapAction`, `Document`, `RepoHostUI`, `Launcher` (registering it, plus `Launchers` and
  `OnLaunchersChanged`), `TerminalAccessory` (the row with its `Focus`, `SetKeyEncoding`, `TabKey` and
  `WorkspaceId`), `Editors.ReportSelection`, in-window `Notify`, and `Prompt`, which is always drawn with the
  plugin's name and icon. The same holds for `IRepoHostSurface`'s `Changed`, `RefreshStatus`, `PreviewAsync`,
  `ConfirmAsync`, `NoticeAsync` and `Toast`, whose dialogs and toasts are drawn by the app and attributed to the
  plugin.
- Navigation: `RevealTool`, `OpenSettings`, `OpenDocumentAsync`, `OpenTextDiffAsync`, `EnterDefaultWorkspaceAsync`,
  `SetDiffScope`, `OpenCommitChanges`, `Editors.OpenAsync` and `Editors.Close`, and `OpenTerminalAsync` without a
  `Command`.
- User-mediated reads, where the user's act is the grant: the file the user opened in the plugin's viewer, the
  target of an action the user invoked, and the path the user chose in `PickFileAsync`. Picking grants `files.read`
  on that path for the activation.
- Its own terminal identity tokens for its own route: `TerminalToken` and `TerminalForToken`.

Permissioned members:

| permission | members | told to the user as |
| --- | --- | --- |
| `files.read:<scope>` | `ReadFileAsync`, `FileRevision`, `ObserveFileRevision`, `OnFilesChanged`, `Editors.OnEvent` (selections carry text) | reads files in your projects |
| `files.write:<scope>` | `Editors.SaveAsync`, and the brokered write surface | changes files in your projects |
| `files.secrets:<scope>` | reads of key, certificate and `.env`-pattern files inside a granted workspace | reads secrets stored in your projects |
| `files.external:<paths>` | `ExternalFiles` | offers files outside your projects for editing |
| `net:<host>[:<port>]` | brokered outbound connections, `ILanguageServerInstallation.Http`, `FetchNuGetAsync`, `GitRunOptions.Network` | talks to these servers |
| `net.loopback.route` | `Route`, `PublicBaseUrl` | serves your terminals and agents on this computer |
| `process:<executable>` | `GitAsync` (as `process:git` with the fixed argument shape above; network operations are the unconfined form), `RunToolAsync`, `IWorkspaceEnvironmentEdit.RunAsync` and `Find`, `ILanguageServerInstallation.RunAsync`, `FindTool`, `RequireTool` and `FindDotnet`, `LanguageServerSpec.LaunchAsync`, `InstallAsync` and `FindFallbackAsync`, and the brokered start surface | runs this program with your rights |
| `terminal.command` | `OpenTerminalAsync` with a `Command`, `AgentLauncher.TerminalCommand` and `Open` | runs commands in new terminals |
| `terminal.input` | `WriteTerminal`, `ITerminalAccessoryApi.Write` and `HoldInput`, `RevivePrefill` | types into your terminals |
| `terminal.read` | `ITerminalAccessoryApi.ReadScreenTailAsync`, `OnTerminal`, `Terminals` with pids, `WorkspaceForProcess`, `ShellEnvironmentAsync` (environments carry secrets) | reads your terminals and their environment |
| `shell.environment` | `TerminalEnvironment`, `AgentLaunch` and `AgentLaunchContributions` on either side (other plugins' contributions carry environment values), `WorkspaceEnvironment`, `WorkspaceEnvironmentStep`, `OnWorkspaceEnvironment`, and every `IWorkspaceEnvironmentEdit` member other than `RunAsync` and `Find` (`Get` reads variables, which carry secrets; `Set`, `PrependPath` and `ShellInit` change them; `Directory`, `DefaultDirectory`, `Edited`, `MatchesDefault`, `Report` and `Log` come with the step) | changes what your shells and agents run |
| `agent.instructions` | `Prompt` (H20), `ToolGuidance`, `Tool` (H4), `SetInstructionSectionAsync` | tells your agents what to do |
| `agent.records` | `AgentRecord`, `SetAgentRecord`, `AgentSessions`, `ListAgentSessions`, `OnInstructionSectionsChanged`, `InstructionSectionsAsync` | reads and labels your agent sessions |
| `language.server` | `LanguageServer` registration and everything reachable from its hooks, except what runs programs or reaches the network, which also needs `process:` or `net:`. That is `ILanguageServerInstallation` (`Root`, `Platform`, `ServerCache`, `SharedCache`, `InstallationPath`, `SessionDirectory`, `Workspace`, `WorkspaceState`, `FindInstalled`, `AcceptedTerms`), `ILanguageServerStart` (`Expand`, `FullPath`, `Manifests`, `Options`, `ResolveProjectJdk`, `ServerCache`, `SessionDirectory`, `Workspace`, `WorkspaceState`, `Log`), `ILanguageServerSession` (`RequestAsync`, `Contains`, `SupportsCommand`, `SetProgress`, `SetState`, `SetUnavailable`, `Track`, `Workspace`) and `ILanguageServerPrime` (`Source`, `SourceState`, `Target`, `TargetState`). The server's cache, installation, session and workspace-state directories are bind-mounted read-write into the plugin's sandbox under this grant | provides code intelligence |
| `language.settings` | `LanguageSettingsAsync`, `OnLanguageChanged`, `SetLanguageSettingAsync` (only `lsp-enabled`, `java-kotlin-provider`, `language-limits` and `language-server:<id>`), `LanguageCallAsync` (only `catalog`, `resolution`, `provision`, `configurationSchema` and `running`) and `LanguageTermsAsync` | changes how language features are switched on and set up |
| `repo.host` | `RepoHost`, `OnBranchStatus`, `CommitChecksAsync`, `IRepoHostSurface.ApplyAsync` and `OpenPrAsync` | acts on your pull requests |
| `processes.read` | `Processes` (`Table`, `Usage`, `LanguageServers`, `LanguageServerName`, `OwnedProcesses`, `RefreshAsync`) | sees what runs on your computer |
| `processes.control` | `IProcessHost.StopProcessAsync`, `StopLanguageServerAsync`, `ManageOwned`, `ReportOwned` and `ReleaseOwned` | stops programs on your computer |
| `clipboard.write` | `CopyText`, `IRepoHostSurface.CopyCommandAsync` | writes your clipboard |
| `open.url` | `IRepoHostSurface.OpenLink` for anything but `http` and `https`, which stay ambient | opens other apps |
| `notify` | `NotifyAttention` | sends desktop notifications |

`process:` runs the executable confined by default (see [Filesystem and secrets](#filesystem-and-secrets)); an
unconfined `process:` grant, `terminal.*`, `shell.environment` and `agent.instructions` are the full-trust
grants, because each can reach the protected set indirectly. The prompts say so in those words rather than
presenting these as narrow permissions. `files.secrets` is the only way a plugin reads secret-pattern files
inside a workspace, and it never reaches the protected set.

The UI invariant holds against these as well. A UI half shares its plugin's process and grants. A UI capability that
touches the computer, such as `ITerminalAccessoryApi.Write`, `ReadFileAsync` or `CopyText`, is covered by the plugin's
permissions exactly like the host capability it resembles.

The isolated runtime adds brokered surfaces that do not exist today: scoped file reads and writes, outbound
connections to named hosts, and starting a named executable. They replace the ordinary .NET a host half uses now.
The API spec's "no dialog, process, or filesystem wrappers" rule keeps holding for the trusted runtime. Builtin
plugins may keep using ordinary .NET, so in-process builtins pay nothing for these surfaces.

### Granting

A plugin declares its permissions in the manifest as a new `permissions` field. Declaring a permission grants
nothing.

- Enabling an external plugin shows its permissions in plain language, ordered by risk, with the scope of each.
  Enabling grants `files.read` inside the plugin's workspaces (never the protected set or secret-pattern files),
  `net.loopback.route`, `clipboard.write`, `notify` and `processes.read`. No full-trust grant is ever part of it.
- Everything else is asked for at first use. The prompt is drawn by the app outside any plugin surface, so a
  plugin cannot draw a fake one. The user can allow once, allow always, or deny.
- Grants are stored in host state per plugin and bound to the content hash of its entry assemblies and its
  declared permission list. An update that changes either asks again.
- Settings › Plugins shows each grant with its scope and last use, and the user can revoke any of them. A revoked
  grant's handles stop working at once.

The broker canonicalises every path, resolving `..` and symlinks, before it compares it against a scope. It does
the file or network operation itself and passes back data or a read-only descriptor, so the plugin never holds an
open handle wider than what it asked for.

Every use of a permissioned member is written to a per-plugin audit log the user can open from the plugin's
Settings card. Network, process and terminal use also show a live indicator on that card.

Headless tereditor has no app, so it has no prompt and no Settings card. It grants from a file instead
([below](#headless-tereditor)).

## Threat model

Protected: the user's secrets and credentials (the protected set), files outside the granted workspaces, network,
processes and shells against an external plugin beyond its grants; other plugins' state and channels; and the
integrity of tereditor's own prompts. A plugin without a full-trust grant cannot read the protected set by any
route.

Not protected:

- Changes to files inside a workspace the plugin may write, including malicious code in the user's source. This
  is accepted: the user reviews source changes, and a plugin that edits code needs write access to it.
- Misuse of a capability the user granted. The full-trust grants can reach the protected set, and the prompts
  say so.
- Denial of service inside the plugin's own surfaces.
- Timing and resource side channels.
- Builtin plugins, which are trusted by construction.
- Anything installed outside tereditor's plugin roots.

## Non-goals

These were decided against:

- A language-level sandbox: WebAssembly, an in-process IL interpreter (dotnow, ILRuntime), or IL verification as
  a boundary. An install-time IL check may remain as a lint that gives early, readable refusals. It is never what
  keeps a plugin out.
- A declarative UI subset in place of Avalonia.
- A sandbox on the client: clients render and run no external plugin code, so web and Android need none.
- External plugins on the playground host.
- Hiding input lag of a remote plugin UI.
- A supported non-C# SDK.
- Moving an external plugin into the trusted runtime by a setting.
