# Publishing a store plugin from the Actions tab

Maintainer note. This is about pushing a plugin from this repo to the **official store**
([AI-Cockpit-Plugins](https://github.com/raymondkrahwinkel/AI-Cockpit-Plugins)) without hand-editing that
repo. It replaces the manual chore — build Release, zip, hash, copy the zip across, edit `index.json`,
commit — with one click. If you are an out-of-repo author running your own store, that is
[PLUGIN-SDK.md → Publishing a plugin store](PLUGIN-SDK.md#publishing-a-plugin-store), not this.

## What it does

The **Publish plugin to store** workflow (`.github/workflows/publish-plugin.yml`) is a manual
`workflow_dispatch`. You pick a plugin id and run it; it then:

1. Reads that plugin's `plugin.json` (the source of truth: version, `minHostVersion`, `abstractionsVersion`).
2. **Gates on host availability.** If the plugin's `minHostVersion` is newer than any published host — the
   highest `v*` release and the base version baked into the current nightly — it stops. A plugin published
   ahead of the host it needs would install and then be refused with *"Needs a newer AI-Cockpit"*, so the
   workflow blocks it and tells you to ship the app release/nightly first. **This gate is only as honest as
   the `minHostVersion` you set** — bump it whenever a plugin starts using a new host API, or the gate waves
   through a plugin that will not run.
3. Skips if the plugin's current version is already in the store index (a published version is immutable —
   users may have pinned its `sha256`).
4. Builds Release, zips the output (`plugin.json` at the zip root, `.pdb` excluded), and hashes it.
5. Upserts the version into the store's `index.json` (`scripts/store-index-upsert.py`).
6. **Opens a PR** against the store repo — it does not push to its `main`. You review the added zip and the
   `index.json` diff, then merge to publish.

Tick **dry_run** to do everything except open the PR — useful to see the diff and the hash first.

## One-time setup: the `STORE_PUBLISH_TOKEN` secret

The workflow runs in this repo but has to write to the *store* repo, which the default `GITHUB_TOKEN`
cannot reach. Create a repository secret named **`STORE_PUBLISH_TOKEN`**:

- A **fine-grained PAT** scoped to `raymondkrahwinkel/AI-Cockpit-Plugins` with **Contents: read and write**
  and **Pull requests: read and write**, or
- a **GitHub App** installation token with the same permissions.

Add it under *Settings → Secrets and variables → Actions → New repository secret*. Without it, the
"Check out the store repo" step fails.

## Editorial metadata: optional `store.json`

`plugin.json` carries what the app enforces (id, name, version, `minHostVersion`, …) but not the store's
presentation fields — `category`, `icon`, `homepage`, `repository`, `featured`. The publish resolves those
in this order:

1. a **`store.json`** beside the plugin's `plugin.json`, if present;
2. otherwise the values already on that plugin's entry in the store index (preserved across a version bump);
3. otherwise left unset for you to fill in on the PR.

So a plugin already in the store keeps its polish across updates with no extra file. A brand-new plugin is
listed with just its identity unless you add a `store.json`, e.g.:

```json
{ "category": "Widgets", "icon": "📈", "homepage": "https://…", "repository": "https://…", "featured": false }
```

Any field `store.json` sets overrides the manifest/preserved value; `name`, `description` and `author` may be
overridden there too, but default to the manifest.

## Why manual, and what comes next

A plugin and the host change it depends on often land together, and a human is the right judge of "is the
host out yet". The gate + steps above would make an **auto-on-merge** trigger (publish when a plugin's
`plugin.json` version changes on `main`) safe later — only the trigger changes; the safety logic stays. This
manual button is deliberately the first half of that.
