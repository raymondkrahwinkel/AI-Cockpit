# Cockpit plugin docs

- **[PLUGIN-SDK.md](PLUGIN-SDK.md)** — the how-to guide: overview and load model, quickstart, contribution
  points, the `plugin.json` manifest, project setup, packaging/install/enable/disable/remove, publishing a
  plugin store, gotchas, and a tour of the example plugins.
- **[API-REFERENCE.md](API-REFERENCE.md)** — every type and method in `Cockpit.Plugins.Abstractions`, with
  signatures and small code examples.
- **[example-store-index.json](example-store-index.json)** — a real `index.json` (the official store's, for
  the GitHub Issues plugin) to use as a template for your own store catalogue.

Start with the [Quickstart](PLUGIN-SDK.md#quickstart) in the SDK guide if you just want a plugin running.

The official plugin store is
**[github.com/raymondkrahwinkel/AI-Cockpit-Plugins](https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins)**,
pre-seeded into a fresh cockpit install; the three example plugins under
[`plugins-dev/`](../../plugins-dev) are published there.
