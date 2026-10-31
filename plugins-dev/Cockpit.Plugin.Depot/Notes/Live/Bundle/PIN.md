# Room bundle pin (AC-1899)

`room.bundle.js` is `room.js` with Depot's own `markdown.ts`, yjs and y-tiptap, built from one AI-Depot commit. `schema.json` is that
commit's `scripts/notes-roundtrip-fixtures/schema.json`; its SHA-256[:12] is the schema version every Join sends.

The pin is `DepotBundleCommit` and `DepotBundleSha256` in `Cockpit.Plugin.Depot.csproj`, and the build fails on any other bundle.

- **Check the pin:** `node build.mjs` rebuilds the pinned commit from a clean clone (`npm ci` from its lockfile, its own esbuild) and
  fails unless the bytes are the pinned ones.
- **Move the pin:** `node build.mjs <commit>`. It fails when the bundle's schema is not that commit's `schema.json`, and otherwise
  prints the new sha256: put the commit and the sha in the csproj together. Moving it is a deliberate step, never part of other work.
