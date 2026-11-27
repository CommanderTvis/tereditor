# Continuation log

Recent task outcomes that a later session may need to continue: what landed, what was checked, and what was not.
Durable facts belong in the owning module `SPEC.md` files and open work in [STATUS.md](STATUS.md); this file is not
a second contract and code is the source of truth.

Rules for adding to it: one short entry per landed task (heading `## YYYY-MM-DD — topic`, at most about ten lines:
what changed, checks run, what was not verified). Keep roughly the last twenty entries; when adding one, delete the
oldest, and delete any entry whose facts have moved into a spec or STATUS.md or that a later entry supersedes. No
build logs, step-by-step progress or rebase notes.

## History before 2026-11-26 (summary)

The full chronological log is in Git history (`git log -p -- context-log.md`). September 2026 built the Avalonia
prototype over the transport-independent host and ported the upstream docking, layout and E2E behavior; the separate
validation ledger was retired on 2026-10-15. October added language servers (later one builtin plugin each, Plugin API
H21), the optional IntelliJ provider, embedded Air Context, the Linux AppImage port, embedded Tailscale for Android and
the browser, the Specs and Blueprint builtins (off by default), workspace switching without the loading flash, and the
GitHub and Processes plugins. November moved IntelliJ to JetBrains' licensed release, added Pi, Model providers, the
shared chat view and Runtimes for tooling, started refusing external plugins, and made the desktop app and remote host
packages NativeAOT. Facts are in the module specs; open work is in [STATUS.md](STATUS.md).

## 2026-11-26 — Pi pane and Air Context 0.9.22

The Pi pane follows its chats, Air Context pins CLI 0.9.22.1774 and downloads it only on a click, and a gate case that
leaves a window open now fails. Not verified: the running app; the limits of the new binary are in the
[Air Context spec](src/Tereditor.Plugins.AirContext/SPEC.md).

## 2026-11-27 — Submenus open beside their parent

A submenu sits flush beside its parent and turns left at the window's edge ([spec](src/Tereditor.UI/SPEC.md), Popups).
Checked headless on macOS only.

## 2026-11-27 — omp 19.2.1, no self-update for managed agents, omp colors

The pin is omp 19.2.1. The managed omp and Pi cannot update themselves
([omp spec](src/Tereditor.Plugins.Omp/SPEC.md), [Pi spec](src/Tereditor.Plugins.Pi/SPEC.md)). omp wears its gradient mark
with colored agent icons. Proven from the package source and by fixture checks; not verified: a real omp or Pi run, Windows,
the running app. Pi stays at 1.6.0 (1.6.1 exists).

## 2026-11-27 — Pi and omp share their plugin code

`Tereditor.Plugins.Agent.Host` and `Agent.UI` now hold the shared download, version probe, pane and settings parts; no
wire or plugin API change. The browser build passed with `DisableParallelAot` (the memory guard stopped a parallel
one). Not verified: the running app, Android.

## 2026-11-27 — Codex and omp share one process poll

The poll lives in `Tereditor.Plugins.Agent.Host` ([spec](src/Tereditor.Plugins.Agent.Host/SPEC.md)), which Codex now
references; no behavior change. Claude Code keeps its own poll. Checks: build, format, `--codex`, `--omp`,
`--agent-launches`, `--conformance`. Not verified: the running app, browser and Android builds.

## 2026-11-27 — Tailscale pairing with Serve on start

With Serve on start on, an approved device got a bare 503. Pairing now serves beside a listener the tailnet cannot
reach, and the client says why pairing failed ([spec](src/Tereditor.Plugins.Tailscale/SPEC.md)). Not verified: a
real phone and tailnet.

## 2026-11-27 — Plugin clients no longer share keyboard focus

All clients of an external plugin shared Avalonia's one keyboard device, so another client's new view or click canceled
a held button (a CI failure) and typing could land in another client's field. Each client context now has its own, and
a view opens without focus ([spec](src/Tereditor.Plugins.Runner/SPEC.md)). Not verified: the running app.

## 2026-11-27 — The Claude debug log keeps its lines in order

Three threads could list a line after a newer one, and the panel then dropped it ([spec](src/Tereditor.Plugins.ClaudeCode/SPEC.md),
Debug mode). Checks on macOS: `--claude-chat-host`, format; CI runs it on Linux. Not verified: the running app. Open: a new
log restarts its numbering at 1, so a panel that stayed open would likely drop its lines (read in code, not reproduced).

## 2026-11-27 — Claude Code rules in the Context tab

The tab reads `.claude/rules` as Claude Code does and says when each rule loads, now including folders below the
session's as on demand rows, from a bounded search asked apart from the configuration
([spec](src/Tereditor.Plugins.ClaudeCode/SPEC.md), Rules: sources, limits, skipped folders). Checks on macOS:
`--claude-code`, format. Not verified: the running app, Windows, a very large project.

## 2026-11-27 — Terminal file underlines stay while the line is edited

Any edit gave a line new text, so its links vanished until the debounced re-resolve. A changed line now shows the names
still whole on it from the lines it replaced ([spec](src/Tereditor.UI/Terminal/SPEC.md)). Checks: gate cases
`terminal-links-ui`, `terminal-commit-navigation`, `terminal-commit-links`, format. Not verified: the running app. Open:
Metal can still blank for up to its 200 ms pass, since its native renderer keys links by line text.
