---
id: module-plugin-lsp-java
type: module-design
status: draft
title: Java (Eclipse JDT) — a language server as a builtin plugin
parent: group-plugins-language-servers
depends-on: [module-plugin-code-intelligence, module-plugin-api]
references: [module-host-language]
tags: [plugins, language]
---

# Java (Eclipse JDT) language server plugin

The plugin id is `lsp-java`; it ships enabled. Its host half is a `LanguageServerPlugin` whose activation provides the
`java` server (Plugin API H21); it has no UI half, methods, channels or settings of its own. Disabled, the server leaves
the catalog, its sessions stop and its saved settings are kept. Implementation: Eclipse JDT Language Server · Eclipse Foundation.

The spec declares the pinned version, owned files, pinning and its pins, configuration controls, project manifests
and scope, and any install and launch steps; the shared behavior is the host's ([language services](../Tereditor.Host.Core/Language/SPEC.md)).

Headless: declared `Yes`. A headless host downloads the pinned Eclipse Temurin JDK 21 (SHA-256 checked, in the host cache) and starts the server with it, `JAVA_HOME` and `PATH` included, so no JDK need exist on the machine. Nothing else in the plugin needs a window.

It reports JDT's source roots (`java.project.listSourcePaths`) for directory marks.
