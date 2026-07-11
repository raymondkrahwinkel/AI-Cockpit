# Cockpit plugin docs

- **[PLUGIN-SDK.md](PLUGIN-SDK.md)** — the how-to guide: overview and load model, quickstart, contribution
  points (settings, side-menu button/section, dialogs, **provider plugins**, **MCP server registration**),
  the `plugin.json` manifest, project setup, packaging/install/enable/disable/remove, publishing a plugin
  store (the `index.json` schema), gotchas, and a tour of the six example plugins.
- **[API-REFERENCE.md](API-REFERENCE.md)** — every type and method in `Cockpit.Plugins.Abstractions`
  (`ICockpitHost`, `ICockpitActions`, `IPluginStorage`, `IPluginSettingsView`, the `Sessions` namespace for
  provider plugins, the `Mcp` namespace for MCP registration), with signatures and small code examples.
- **[example-store-index.json](example-store-index.json)** — a real `index.json` excerpt (the official
  store's, for the GitHub Issues plugin) to use as a template for your own store catalogue.

Start with the [Quickstart](PLUGIN-SDK.md#quickstart) in the SDK guide if you just want a plugin running, or
["How do I set up my own store?"](PLUGIN-SDK.md#the-index--indexjson) if you're publishing a catalogue.

The official plugin store is
**[github.com/raymondkrahwinkel/AI-Cockpit-Plugins](https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins)**,
pre-seeded into a fresh cockpit install; the six example plugins under
[`plugins-dev/`](../../plugins-dev) — three UI plugins (GitHub Issues, GitHub Pull Requests, YouTrack) and
three provider plugins (Gemini/OpenAI, GitHub Models, CLI Agent/Codex) — are published there.
