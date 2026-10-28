# AC-1896 spike: not product code

The report is in Depot `krahwinkel-it/cockpit` › `Research/AC-1896-spike.md`. None of these projects is in `Cockpit.slnx`; nothing here is
merged. Every run is against a **local** Depot of AI-Depot `dev` @ `08104a29` (SQLite, own port and data folder), never production.

- `bundle/`: `build.mjs` bundles Depot's own `markdown.ts` and `merge.ts` (and, with `--yjs`, `room.js`: the room's Y.Doc with yjs and
  `@tiptap/y-tiptap`) into one script for Jint. `build-pinned.sh <commit> <dir>` builds it from a clean checkout of one AI-Depot commit and
  checks the schema version against that commit's `schema.json`; two builds of one commit are byte-equal.
- `fixtures/`: a copy of Depot's golden fixtures at `08104a29` (so a counter-proof can be a commit).
- `JintConverter/`: G3, the converter in Jint against the fixtures, plus its cost.
- `Bridge/`: `offline` and `hub` (G1/G2, `jint` or `ydotnet` engine, `--v2`), `g4`/`g4hub`, `g5`, `g6`, `saver` (B7), `mem`.
- `PluginProbe/` + `PluginHost/`: G7, the dependencies in a plugin loaded through Cockpit's own `PluginLoadContext`.

```
bash spikes/AC-1896/bundle/build-pinned.sh 08104a29 <dir>
dotnet spikes/AC-1896/JintConverter/bin/Release/net10.0/JintConverter.dll <dir>/notes.jint.js spikes/AC-1896/fixtures
dotnet spikes/AC-1896/Bridge/bin/Release/net10.0/Bridge.dll hub http://localhost:<port> <secret file> <dir>/bridge.jint.js spikes/AC-1896/fixtures jint
```

The secret file holds the local admin's password and lives outside the repository. `PW_CHROMIUM` points at a Chromium if Playwright's own
build is missing.
