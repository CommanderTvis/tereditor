---
id: module-plugin-vertex
type: module-design
status: active
title: Google Vertex AI builtin
parent: group-plugins-agents
---

# Google Vertex AI builtin

A builtin plugin in the Model providers category, in the one provider list of Settings › Plugins › Model providers, after the routers. It hard-depends on Model providers
(`dependsOn`, wire version of Providers). An agent never depends on it. It has a host half and a UI half. Early support; not headless.

## What it does

- Settings. Project, Region: non-secret, in the plugin's own settings, edited in its component and saved through the host. Values are checked against a strict pattern, so nothing
  that could reach a shell is ever set.
- Provider. While the settings are complete, the host half contributes the provider `vertex` (a cloud platform, API kind `vertex`, credential kind
  `platform`, no key) with the launch variables for Claude Code. It contributes again after every settings change and withdraws when the settings are incomplete or the plugin stops.
- Credentials. Uses your own Google Cloud credentials: application default credentials. tereditor stores no Google secret. Sign in with gcloud auth application-default login first. tereditor stores no secret for this platform and never reads one.
- Who uses it. Only Claude Code declares this platform (Model providers SPEC, Cloud platforms and the Claude Code seam). Codex, Pi and omp are offered nothing.

- Playground. `UnavailableOn = [pureWeb]`: a browser tab has no Google Cloud profile or sign-in tool and no Claude Code, so the plugin is refused there.

## Not yet

Pi, omp and Codex support (each needs its own seam and a check of what the agent really takes), a model list, a credential check from the app, and a Windows shell.

## Checks

`--cloud-platforms` (gate case `cloud-platforms`): the manifest, the provider from complete and from unsafe settings, who is offered it, the launch variables and their absence of any secret,
Windows blocked and withdrawal. `--plugins-settings` checks the icon.
