---
id: module-plugin-tailscale
type: module-design
status: draft
title: Tailscale — host discovery and device pairing
parent: group-plugins-workbench
depends-on: [module-plugin-api, module-plugin-ui-kit]
references: [submodule-host-plugins, submodule-ui-plugins, submodule-ui-panels]
tags: [plugins, tailscale]
---

# Tailscale — host discovery and device pairing

No upstream counterpart. Original tereditor builtin plugin.

## Responsibility

Connect clients to a host on the same tailnet without exchanging numeric addresses, ports or
session tokens. The plugin ships disabled. Enabling it starts a bootstrap listener on the host's Tailscale
IPv4 address at port 54124; it never binds a LAN, wildcard or public address. It uses the installed Tailscale
CLI and requires an active connection. Missing CLI, disconnected Tailscale and port conflicts appear in
Settings › Tailscale. Disabling the plugin stops bootstrap and invalidates its grants for serving.

The desktop picker discovers online peers through `tailscale status --json`, probes at most 256 of them
with eight concurrent requests, and lists only tereditor hosts. Android owns an embedded userspace
Tailscale node instead of reading another application's private peer list. It signs in through the system
browser, retains its identity in app-private storage, enumerates online IPv4 peers from its own netmap and
probes the same bootstrap endpoints. No separate Tailscale Android application, system VPN or OS MagicDNS
configuration is required. The Android adapter supplies ConnectivityManager snapshots through Tailscale's supported interface hook,
avoiding restricted raw netlink enumeration and updating the same node on network changes.
The browser client likewise owns a userspace node, a WebAssembly build downloaded only on request and cached per
build, with its identity in IndexedDB; it dials discovery, pairing and RPC through the node as numeric tailnet
streams (see the [Browser spec](../Tereditor.Browser/SPEC.md#tailscale)). Peer resolution and sign-in URL
filtering for such nodes are shared in `TailscalePeers`; the download state machine is `TailscaleWasmModule`.
The shared picker accepts an injected client network for HTTP, name resolution
and peer enumeration; desktop keeps its installed-CLI implementation. Embedded discovery returns numeric
peer addresses so choosing a host never depends on system DNS. Pairing and settings buttons use consistent 40-pixel minimum
heights with horizontally and vertically centered text. No tailnet API key, central registry,
Tailscale Serve configuration, DNS changes or installation of another VPN is needed.

## Pairing and authentication

Bootstrap verifies the request's real source address through `tailscale whois --json`; forwarded headers
are ignored. Only the Tailscale address ranges are eligible. Discovery returns the host name alone. A
client generates a random 256-bit secret and sends it only to a directly resolved tailnet address; the
HTTP client disables redirects. Desktop disables proxies; Android explicitly uses only its authenticated
loopback SOCKS5 proxy, whose dialer is the embedded Tailscale node. Tailscale supplies transport encryption.

A new secret creates a request bound to the verified Tailscale node, with its device name and user identity.
The host shows that identity and an eight-digit hexadecimal fingerprint; the client shows the same code.
An activation-lifetime subscription shows a prompt in every window for each pending request: the device identity,
the comparison code in the code font, and Approve (primary), Decline and an Open Tailscale settings link, even while
Settings is closed. Approve and Decline answer through the same host method as the Settings buttons; while one runs the
prompt's buttons are disabled, a failure is shown on the prompt, and success takes the prompt down in every window.
The prompt does not expire on its own: it comes down in every window when the host's status no longer lists the
request, whether another window or client answered it or it expired. Opening settings keeps it. Repeated polls do not
repeat the prompt for the same pending request. Any client that shows Settings › Tailscale can answer, so every client
shows the choices. Settings › Tailscale shows the "Waiting for approval" and "Remembered devices" headings with their
lists only while the status has pending requests or remembered devices, following the status broadcast in place;
otherwise nothing is rendered. A remembered device is one dense single-line row (ellipsis, tooltip with the full
text) with a small cross button, named "Revoke access", that revokes at once without confirmation and is disabled
while the request runs. Requests expire after two minutes, and the host sweeps them every five seconds and republishes its status when an answerable one expires; declined
requests cannot be recreated by polling, and pending requests and approved grants are bounded to 32 and
128 respectively. Pairing never releases the host's master session token.

A request from a known user and device name (Tailscale's automatic "-N" suffix ignored, user compared
case-insensitively) on another node, or from the same node with a new secret, is shown as "Known device with a new key",
in Settings and in the prompt; for another node it adds that approving replaces the remembered device. The match only
explains the request: it never trusts a changed key, and the old record stays valid until the user approves. Approving
removes the superseded records of that user and name on other nodes, so one device is never listed twice. A different
user with the same name is never matched. On Android the node state lives in app-private files (`filesDir/.tereditor`)
and the node is not ephemeral, so an update keeps its identity; uninstalling the app or clearing its data creates a new node
and a new secret, which is the case above.

Approval persists the hash of the client's secret and its node identity in plugin state. Remembered
devices are grouped by verified node identity, including existing persisted grants. Multiple approved
client secrets on one node remain valid; revoking its single device row revokes every grant for that node. The client stores
its own secret and host name in client-local `tailscale-client.json`, with user-only Unix permissions.
An approved bootstrap returns the actual WebSocket RPC address and port; the client authenticates using its own
secret. A remembered bootstrap secret cannot be used by another Tailscale node. The listener accepts
approved client secrets alongside the existing master token. Revoking a grant rejects new calls and ends
its running streaming calls within one second; disabling does the same for every plugin grant. Terminal
shells retain the normal host-owned lifetime.

## Composition and lifecycle

The plugin owns discovery, identity checks, pairing state, bootstrap HTTP and its UI. A plugin-owned
`TailscaleServing` bridge is injected into builtin host construction by the app and standalone host;
it supplies listener startup and authentication without introducing a Remote or UI reference into the
plugin host or Core. The bridge lives in the plugin contract assembly and is not an extension of the public plugin API. Bootstrap cannot depend on
plugin RPC because the client has no connection yet: the builtin UI exposes a client-local picker before
ordinary UI activation. External plugin activation and capability parity are unchanged.

The embedded host starts an ephemeral WebSocket RPC listener on its Tailscale address after approval. An already
running listener is reused when bound to that address or a wildcard. A listener the tailnet cannot reach, such as
the loopback one that Serve on start opens, stays as it is: pairing opens its own listener on the Tailscale
address beside it. That listener has a random token nobody learns, so only approved device secrets authenticate
there. Pairing opens its own listener too when Serve on start is on and nothing is listening, so it never takes
the shared listener's place. Stopping serving in Settings › Host does not stop pairing's own listener; disabling
the plugin does. The plugin stops only the listeners it started, and the shared one only if that instance is still
running. The standalone host reuses its configured listener and requires its bind address to be reachable through
Tailscale. No local adapter is moved onto RPC.

A pairing failure says what is wrong and what to do; the client never shows a raw HTTP error. When the host cannot
serve an approved device, it answers 503 with the state `unavailable` and a `problem`. The problem is a
`TailscaleServingException` message, which is worded for the device, or a fixed sentence: other exception text stays
on the host, where Settings › Tailscale shows it until a device connects. Any other failed pairing request, such as a
failed identity check, shows its cause there too. The client also explains a host it cannot
reach, a bare 503, 403 or 404, an unreadable reply and a full request queue.

Desktop Settings › Host opens a dedicated client instance; `--tailscale` opens the picker directly. The
Android connect screen offers Tailscale alongside manual entry and QR scanning of host codes. It opens browser sign-in
only after an explicit action, then discovers hosts automatically. Administrator device approval, pending
authentication, missing browser and connection errors remain visible with retry and sign-out controls.
Sign-out logs the node out and forgets the matching tereditor pairing and remembered endpoint token.
Automatic startup restoration reconnects the remembered host, while opening the picker explicitly keeps
host selection and sign-out available instead of immediately reopening that host. After discovery the
picker selects the peer matching the host field (name, address or MagicDNS prefix, case-insensitive) without
rewriting it, else the first peer, and reports "Found N hosts."; the embedded picker hides the host-name field behind
"Enter a host name" while a peer is selected, and the embedded status line shows only non-obvious states.
The app closes the node on session disposal without deleting identity; backgrounding retains it, and
returning to the foreground resumes status polling and existing RPC reconnection. Both pairing HTTP and
every RPC adapter use this node: RPC handlers are registered for the approved endpoint before channels
are created and removed after adapters close. Manual connections keep ordinary network routing. Remembered pairings
bootstrap again on client launch, so host port changes need no manual editing. Disconnecting on Android also forgets that pairing secret. Existing manual and QR
connections keep their current behavior. A live workbench keeps the existing reconnect behavior; after
a host restart changes its listening port, reopening the picker refreshes the endpoint.

Tailscale state is detected again without restarting the app. The host detects once on activation and then
only when asked: the `refresh` plugin method wakes its supervisor, which retries detection and the
bootstrap listener while pairing is unavailable (a ready host ignores it) and republishes the status.
Settings › Tailscale asks on opening, on window activation (throttled to three seconds), after an
approval or revocation while unavailable, and every five seconds while it is visible and unavailable.
The poll stops when the page detaches and a newer request cancels the previous one. A hidden page
causes no detection.

All CLI, DNS, filesystem, identity and HTTP work runs off the dispatcher. Discovery and pairing are
cancellable and bounded by timeouts. The plugin's disposer cancels requests, closes bootstrap, clears its
authentication callback and stops any listener it owns before returning.

A client whose own connection runs over Tailscale cannot turn the plugin off. The host refuses, with
`FailedPrecondition` and the `TailscaleDependence.Refusal` text, any plugin-settings change from such a
connection that disables or resets this plugin or a plugin it depends on, transitively. Settings › Plugins
on that client shows those switches disabled with the same text as tooltip and note. Embedded, loopback and
LAN clients are unaffected. Revoking a paired device stays allowed; this plugin has no separate serve or
logout switch to guard.

## Verification

`--tailscale` exercises registry expiry and limits, node binding, credential secrecy and persistence,
actual HTTP discovery and approval against a fake Tailscale identity provider, embedded WebSocket RPC startup,
pairing beside a listener the tailnet cannot reach and with Serve on start on, the client's failure messages,
local/remote status parity, authenticated calls, revocation and plugin teardown, and a fake CLI that
flips from disconnected to connected: no detection without a request, and a refresh then makes pairing ready. Fixtures never call the
user's Tailscale daemon. `--android` also checks embedded state reuse, netmap validation and real
authenticated SOCKS5 HTTP probing against a local fake proxy; `--transport` checks endpoint-scoped
WebSocket handler routing, reconnect and disposal. The native bridge has isolated Go tests for its
operation and proxy lifetimes. Desktop and Android device-to-device operation requires manual verification.
`--tailscale` also checks the browser node's manifest, cache versioning, download state machine and stream-dialed
pairing and RPC; real browser sign-in and pairing over a tailnet require manual verification.

`--headscale` (opt-in, slow; see the checks module spec) joins a real host and a real embedded-library
client to a throwaway Headscale tailnet as distinct nodes and verifies discovery, pairing, authenticated
WebSocket RPC, broadcasts and reconnection across it. It does not exercise the host plugin's kernel-interface
pairing listener, which still needs a manual device check.


The plugin list and settings navigation use the official Tailscale nine-dot SVG icon,
staged as a host-served plugin asset. Its fill follows the theme; geometry and opacity
are preserved. Source and trademark attribution are recorded in NOTICE.md.
Android's embedded bridge initializes Tailscale's supported log directory under
app-private node storage, with mode 0700; it does not assume HOME or XDG paths exist.
Library log uploads are disabled. A synchronous native panic is converted into a
credential-free error at the C ABI boundary; it is not a substitute for preventing
background-goroutine failures. The Android module's verification section records the
actual emulator auth milestone and the remaining real-account acceptance boundary.
