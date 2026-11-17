---
id: module-plugin-openai
type: module-design
status: active
title: OpenAI builtin
parent: group-plugins-agents
---

# OpenAI builtin

A builtin plugin in the Model providers category. It hard-depends on Model providers (`dependsOn`, wire version of Providers) and adds one
component to the Providers page, in the `providers:other` place. An agent never depends on it. It has a UI half only: the key goes
through Model providers, the sign-in is Codex's own. Early support; not headless.

## What it does

- Key path. The component stores the OpenAI API key through the Model providers key store (`saveModelKey`, `removeModelKey` on provider `openai`)
  and shows only whether one is stored. The bundled `openai` provider declares the Responses API, so Codex is offered it as a model
  source and the Codex launch seam (Providers SPEC) points Codex at the `provider-key` helper. The key is typed into a masked field, sent
  once and never shown again.
- Sign-in, phase 1: policy only. The component is a row like the other providers (title, hint, Add key or Replace key and Remove key opening the shared key form, no inline field; `ApiKeyRow` in UI.Kit), named `ModelProvider-openai`; Model providers hides its bundled OpenAI API row while the component is present, so the page lists OpenAI once. Under it the component shows a "Sign in with ChatGPT" row with the Sign in button on the right, and under it OpenAI's sign-in policy
  (allowed; a remote host needs OpenAI's approval for hosted apps). The button opens a terminal in the active workspace and runs `codex login`. tereditor runs no OAuth flow, reads no token and stores
  none. In the Playground (a browser tab: no loopback listener, no sign-in helper) the policy is not allowed with a plain reason and the Sign in row is not shown; only the key path works. Without an open workspace the button says so. A sign-in does not make the provider a model source; the agent's own provider already
  uses it.
- Open decision: the `openai` row stays bundled in Model providers, because a contributed provider cannot take a bundled id. This plugin
  therefore contributes no provider definition, only the component.

## Checks

The policy in a browser tab (off, with a reason) is part of `--openai`, as is `UnavailableOn pureWeb` for Bedrock and Vertex.

`--openai` (gate case `openai`): the manifest (category, dependency on Model providers at its wire version, UI half only, not headless), and
the component mounted headless over the real key store: key saved and removed without showing the key, Sign in running `codex login` in the
active workspace and refusing without one.
