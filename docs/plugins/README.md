# Cockpit plugin docs

- **[PLUGIN-SDK.md](PLUGIN-SDK.md)** — the how-to guide: overview and load model, quickstart, contribution
  points (settings, side-menu button/section, dialogs, **session header items**, **conversation pickers**,
  **provider plugins**, **widget plugins** for a Dashboard workspace, **MCP server registration**), the
  `plugin.json` manifest, project setup, packaging/install/enable/disable/remove, the plugins that ship with the
  app, publishing a plugin store (the `index.json` schema), gotchas, and a tour of the example plugins.
- **[API-REFERENCE.md](API-REFERENCE.md)** — every type and method in `Cockpit.Plugins.Abstractions`
  (`ICockpitHost`, `ICockpitActions`, `IPluginStorage`, `IPluginSettingsView`, `IPluginSessionContext`,
  `ConversationPickerRegistration`, `PluginProfileInfo`, the `Sessions` namespace for provider plugins, the
  `Mcp` namespace for MCP registration), with signatures and small code examples.
- **[example-store-index.json](example-store-index.json)** — a real `index.json` excerpt (the official store's,
  for the GitHub Issues and System Monitor plugins) to use as a template for your own store catalogue. The
  second one is a widget, so it also shows the `"category": "Widgets"` that puts a plugin in the store's Widgets
  section.
- **[AUTOMATED-PUBLISH.md](AUTOMATED-PUBLISH.md)** — maintainer note: the one-click **Publish plugin to store**
  Actions workflow that builds, hashes and opens a PR against the official store, with a host-version gate that
  refuses to publish a plugin ahead of the app it needs. For pushing *this* repo's plugins to the official store.

Start with the [Quickstart](PLUGIN-SDK.md#quickstart) in the SDK guide if you just want a plugin running, or
["How do I set up my own store?"](PLUGIN-SDK.md#the-index--indexjson) if you're publishing a catalogue.

The official plugin store is
**[github.com/raymondkrahwinkel/AI-Cockpit-Plugins](https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins)**,
pre-seeded into a fresh cockpit install; the example plugins under
[`plugins-dev/`](../../plugins-dev) — UI plugins (GitHub Issues, GitHub Pull Requests, YouTrack, Git Status,
GitHub Actions, Session Review, Prompt Library, Claude Transcript Search), MCP plugins (Docker, Kubernetes),
widget plugins (Clock, System Monitor) and provider plugins (Gemini/OpenAI, GitHub Models,
CLI Agent/Codex) — are published there.

**Git Status, Claude Transcript Search and the Clock ship with the app.** The first two used to be core
features, and bundling them means you have them without having to know they exist. The clock is there so a new
Dashboard workspace has something on it from the start — while the System Monitor is not, because a CPU meter
nobody asked for is not the price of a working dashboard. All three are in the store as well: bundling decides
what you get without asking, the store decides what you can update, remove and put back on its own.
