// AC-1897: bundles room.js with the yjs of one AI-Depot checkout (its own node_modules, so the same lockfile and esbuild it builds with).
// Usage: node build.mjs <AI-Depot ClientApp/notes dir> <out.js>. The checkout is pinned in PIN.md; the output is byte-equal per commit.
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const [notes, out] = process.argv.slice(2).map((p) => resolve(p));
const { build } = createRequire(join(notes, 'package.json'))('esbuild');
await build({
  entryPoints: [join(dirname(fileURLToPath(import.meta.url)), 'room.js')],
  outfile: out,
  bundle: true,
  format: 'iife',
  globalName: 'DepotRoom',
  target: 'es2022',
  platform: 'neutral',
  mainFields: ['module', 'main'],
  minifyWhitespace: true,
  absWorkingDir: notes,
  nodePaths: [join(notes, 'node_modules')],
  logLevel: 'warning',
});
