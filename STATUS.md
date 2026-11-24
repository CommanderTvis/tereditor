# tereditor status

One ledger for what completion requires and what is still open.

- [Completion criteria](#completion-criteria): the original scope, the evidence for each requirement and its limits.
- [Backlog](#backlog): known gaps and outstanding acceptance work.

## Completion criteria

Passing functional checks does not prove the full visual and docking contract. Module specs keep the contracts; `SPEC.md` is the reference authority. When prose and the renderer disagree, record the difference.

| Requirement | Current evidence | Completion limit |
| --- | --- | --- |
| C# host independent of Pi/AI; direct local calls and typed WebSocket RPC remote | Host projects, `ProjectChecks.cs`, published local/remote parity, cancellation and Git parity checks | Functional prototype evidence; no production remote deployment claimed |
| NativeAOT app and remote host, nullable checking, single latest package | `NativeAot.props`, `scripts/mise run package:macos`, `check:packaged` requires NativeAOT payloads and runs the shipped binaries (`--packaged`) | Preserve these checks after every implementation change; iOS execution/distribution remains the documented future gate |
| Functional tabs, preview/keep, close/context actions, reorder/overflow/focus and deferred navigation | `UiChecks.cs`, `NavigationChecks.cs`, `DockInputChecks.cs`; native double-click and drag captures | Final contract audit must cover the combined interaction surface, including all drop collisions and viewport changes |
| Persistent settings and XAML where it fits | Compiled XAML files; settings persistence/theme/font/width/preset checks and native Settings capture | Menu/control typography comparison against the reference is still incomplete |
| End-to-end scenario checks | Headless E2E checks in `tests/Tereditor.Checks/E2E` covering themes, multi-window and remote sync, workspaces, terminals, docking, Markdown, Changes and the editor | Inner chrome across workspace transitions and the full theme catalog remain open |
| Multiple windows and shared host state (settings, presets, labels, projects, workspace lifecycle) with local events and WebSocket RPC streaming | `MultiClientE2E.cs` and the synced cases in `ThemeE2E`, `LineWidthE2E`, `LayoutSettingsE2E` and `ProjectContextE2E` (`--sync` runs them); per-window profile entries | Terminal sessions shared across clients arrive with host-owned sessions; separately launched local processes stay independent by design; native multi-window use (menu bar entry, window restore order) is unverified outside headless checks |
| Native Markdown preview with actual scrolling and selectable content | Markdig renderer checks; wheel/anchor/navigation tests; native scroll and rendering captures | Reference typography, spacing and complete representative-document comparison still need final visual review |
| Opening projects, workspace/window restoration, Git changes/diffs and worktree workflows | `ProjectChecks.cs`, `GitUiChecks.cs`, isolated Git fixtures, restoration/navigation checks; explicit-target merge-base/net-working-content and commit catalog local/remote regressions; commit picker, lightweight index-independent catalog parity, workspace selection and fresh-window persisted-query checks | Commit membership refresh/fallback and complete read-error fidelity remain open; visual fidelity of the corresponding panels/dialogs still needs final review |
| Exact frame geometry, docking grammar, panes, alignment, fold/show/restore and resize behavior | `LayoutChecks.cs`, pointer/menu/keyboard checks, source-verified side projection and collision regressions | Auxiliary stack projection with multiple folded/expanded groups and narrow viewports needs a source comparison; verify any resulting gaps |
| Screenshot-perfect layout and details | [Recorded native comparison limits](src/Tereditor.UI/Rendering/SPEC.md#native-visual-acceptance-limits) | Sampled matches are insufficient: native glyph/control/menu differences remain unresolved or unverified |
| Publish and show the finished app; no Pi/AI or benchmarks | Canonical `artifacts/tereditor.app`, published runtime checks, own-window native captures; implementation scope in `SPEC.md` | The current package is a working prototype, not yet proof that all preceding completion limits are resolved; macOS terminal execution and the Scintilla editor are separately in scope |
| Android client of a remote host | `src/Tereditor.Android` and its `SPEC.md`; Debug and Release builds run on an Android 16 arm64 tablet emulator against `Tereditor.Host.Remote` (connect and auto-connect, workbench, menus, dialogs, project by host path, file tree, Markdown, Scintilla editor, live terminal, Settings, Disconnect, Back), with scope recorded in the owning Android spec | Done with limits: emulator only. Physical devices, phones, IMEs other than Gboard, accessibility, plugin UI beyond the default workbench, HTTPS and CI are unverified; cleartext HTTP, an unencrypted stored token, one window, no on-device tests, untrimmed JIT package |

Also in scope and covered by their own specs: the protobuf WebSocket transport, the browser client ([browser spec](src/Tereditor.Browser/SPEC.md)), the Plugin API with its builtins, language servers ([language spec](src/Tereditor.Host.Core/Language/SPEC.md)), Tailscale pairing ([plugin spec](src/Tereditor.Plugins.Tailscale/SPEC.md)) and the macOS Scintilla editor (`src/Tereditor.Scintilla/README.md`). The [checks contract](tests/Tereditor.Checks/SPEC.md#recorded-verification-scope) separates source evidence from packaged evidence.

Still open from those scopes: the Keymap editor binds only the window commands (not editor, terminal or plugin keys; no import/export, per-workspace keymaps or key sequences).

## Backlog

The acceptance work that remains. Module specs keep the contracts and their "Not built" sections; dated task
continuations stay in `context-log.md`.

### Known gaps

Authorities: [UI](src/Tereditor.UI/SPEC.md), [terminal contract](src/Tereditor.UI/Terminal/SPEC.md) and [browser](src/Tereditor.Browser/SPEC.md). Implementation gaps are listed here; acceptance tasks follow separately.

| ID | Status | Gap |
| --- | --- | --- |
| V04 | Missing | Busy-close confirmation does not dismiss when another client closes the terminal. [TerminalTabs.cs](src/Tereditor.UI/TerminalTabs.cs) awaits a normal confirmation without catalog-driven invalidation; this remains an explicit [terminal contract](src/Tereditor.UI/Terminal/SPEC.md) implementation gap. |

### Outstanding acceptance work

These tasks consolidate the remaining checks; they do not assert missing product features. Owning specs retain durable verification scope and measurements; [the checks contract](tests/Tereditor.Checks/SPEC.md#recorded-verification-scope) distinguishes source and packaged evidence. No unrun journey is treated as passed.

| ID | Latest result | Remaining work |
| --- | --- | --- |
| V03 | Not run | In a signed macOS app, see a real away notification and click it to focus the correct terminal/window. Delivery/activation wiring exists and recording-notifier checks pass. |
| V05 | Not run | Compare native Settings, menus, Markdown, all plugins and docking at matched content/dimensions; exercise PDF trackpad magnify. Include drop/overflow/spacer/folded-weight geometry. Functional/package checks pass, but there is no matched capture corpus. |
| V06 | Incomplete | Complete the packaged gate (a source pass is not packaged acceptance). Add direct regressions for reused-tab-ID workspace focus, held Visualize render/remount and same-terminal/two-window rendering, held graph requests after detach/disable, and held PDF read/raster replacement/detach. |
| V07 | Not run on required platforms | Exercise Safari/Firefox, non-macOS keyboards/IME, real notification permission and production HTTPS/proxies. Accept the Settings fix in a normal bundle and on physical Retina; its parallel-workspace Chromium test-build acceptance already passes. |
| V09 | Portable checks only | Execute the Windows package and its ConPTY, Recycle Bin, native editor/Skia, clipboard/IME and client journeys. Portable macOS checks skip Windows runtime branches. |
| V10 | Emulator/fixture coverage only | Exercise physical Android, camera/QR and real two-device Tailscale pairing, approval, reconnect, revoke and remote UX, including packaged deployment. Emulator and fake-identity transport checks pass. |
| V11 | 120 Hz presentation not measured | Measure physical 120 Hz browser presentation. Recorded restty GPU draw cadence is about 60/s and output throughput is 111 FPS; neither proves the presentation target. No new benchmark was run. |
| V12 | Fixture coverage only | Exercise installed Codex/Claude during sustained streaming while typing/resizing/switching controls, interruptions, process replacement, auth/model changes, editor reload/remount/title changes, remote reconnect and physical drops. Complete Claude pane-driven composers/account/usage/MCP health, nested source navigation after unsaved edits, uninstall/marketplace removal and overrides; Blueprint live-model controls, frontmatter discard, outline, alerts and Mermaid; and live Discord IPC/presence. Generic host/UI tests do not establish these journeys. |
| V13 | Linux: cross-built, headless gate and Xvfb launch only | Run the AppImage on real distributions (the 2026-10-19 pass ran its payload, not the image: the runtime could not execute under Rosetta here; `check:packaged -- --linux` unpacks it in CI). Cover a real window manager (title bar, resize, HiDPI), Wayland/XWayland, IME and clipboard, file drops, `notify-send` delivery, `xdg-open`/`gio trash`, a reflink filesystem for workspace clones, Ubuntu 22.04 (glibc 2.35) hosts and Git 2.36+. Mermaid has no Linux library. linux-arm64 is cross-built (2026-10-20) but nothing has run on an arm64 host or runner yet: the first CI/nightly `appimage-arm64` run is its first test. |
