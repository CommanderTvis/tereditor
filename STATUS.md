# tereditor status

Work that is still open. The linked specs hold the details.

## Missing

- The "Close this terminal?" question stays open when another client closes the same terminal.
  [Terminal spec](src/Tereditor.UI/Terminal/SPEC.md#shared-terminals)
- Keymaps cannot be imported, exported or set per workspace, and a shortcut cannot have several steps.
  [UI spec](src/Tereditor.UI/SPEC.md#not-built)
- Mermaid diagrams show as source text on Windows and Linux. [Rendering spec](src/Tereditor.UI/Rendering/SPEC.md#mermaid)

## Missing tests

- Closing a view while it still loads: [Visualize drawings](src/Tereditor.Plugins.Visualize/SPEC.md#remaining-verification),
  [PDF files](src/Tereditor.UI/Resources/Pdf/SPEC.md#remaining-port-gates) and
  [Git Graph](src/Tereditor.Plugins.BranchGraph/SPEC.md#checks-and-remaining-gates).
- One terminal's drawing in [two windows](src/Tereditor.Plugins.Visualize/SPEC.md#remaining-verification), and focus after a
  switch between workspaces that reuse a tab ID. [Docking spec](src/Tereditor.UI/Docking/SPEC.md)
- Claude Code IDE actions and failures, settings dialogs, account views and review over MCP.
  [Claude Code spec](src/Tereditor.Plugins.ClaudeCode/SPEC.md#verification-and-remaining-gates)

## Not tried yet

Nobody has recorded a run of these on the real system. Automated checks cover parts of some.

- macOS: clicking an away notification in the signed app to reach the right terminal.
  [UI spec](src/Tereditor.UI/SPEC.md#away-notifications)
- macOS: several windows with the real menu bar, and their order after a restart. [UI spec](src/Tereditor.UI/SPEC.md#windows)
- Trackpad pinch zoom on [PDF files](src/Tereditor.UI/Resources/Pdf/SPEC.md#remaining-port-gates) and
  [diagrams](src/Tereditor.Plugins.UI.Kit/SPEC.md#agent-visualizations).
- The Windows app in a real window: typing with an input method (IME), clipboard, editor and connecting to a host. CI runs only
  windowless checks. [Terminals spec](src/Tereditor.Host.Core/Terminals.SPEC.md#windows-verification-scope)
- The Linux app on a real desktop, x64 or ARM64: AppImage start, window managers, HiDPI, Wayland, IME, clipboard, file drops,
  notifications, opening and trashing files, and workspace copies on btrfs or XFS. [UI spec](src/Tereditor.UI/SPEC.md)
- The browser client in Safari and Firefox, with keyboards outside macOS, on a Retina screen, with the notification permission
  prompt and behind an HTTPS proxy. [Browser spec](src/Tereditor.Browser/SPEC.md)
- The browser checks since their latest changes. CI runs them only when started by hand.
  [Check reference](docs/develop/agent-reference.md)
- Android beyond a short try on one phone: a full pass, QR scanning, other keyboards, TalkBack, plugin panels and HTTPS hosts.
  [Android spec](src/Tereditor.Android/SPEC.md#validation)
- Tailscale pairing between real devices, from Android and the browser: sign-in, approval, reconnect and revoke.
  [Tailscale spec](src/Tereditor.Plugins.Tailscale/SPEC.md#verification)
- Long sessions with real [Claude Code](src/Tereditor.Plugins.ClaudeCode/SPEC.md#verification-and-remaining-gates) and
  [Codex](src/Tereditor.Plugins.Codex/SPEC.md#current-implementation-and-remaining-gates): typing during output, interrupts,
  restarts, sign-in or model changes and remote reconnects.
- A [Blueprint](src/Tereditor.Plugins.Blueprint/SPEC.md) written by a real agent, and presence in a real
  [Discord](src/Tereditor.Plugins.Discord/SPEC.md#checks) app.
