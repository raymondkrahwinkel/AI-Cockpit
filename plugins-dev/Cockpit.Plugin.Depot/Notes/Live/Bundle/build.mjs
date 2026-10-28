// AC-1899: builds room.bundle.js from one pinned AI-Depot commit (clean clone, `npm ci` from its lockfile, its own esbuild), checks the
// bundle's schema against that commit's schema.json and prints the bundle's SHA-256. Usage: node build.mjs [commit] [work dir]; without a
// commit it rebuilds the csproj's pin and fails on other bytes, without a work dir it uses a temporary one and removes it after.
import { execFileSync, execSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const here = dirname(fileURLToPath(import.meta.url));
const csproj = readFileSync(join(here, '../../../Cockpit.Plugin.Depot.csproj'), 'utf8');
const pinned = (name) => new RegExp(`<${name}>([^<]+)</${name}>`).exec(csproj)?.[1];
const commit = process.argv[2] ?? pinned('DepotBundleCommit');
const work = resolve(process.argv[3] ?? mkdtempSync(join(tmpdir(), 'depot-bundle-')));
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
const git = (args) => execFileSync('git', args, { stdio: 'inherit' });
// npm is a script on Windows, so it runs through the shell; its arguments are fixed here.
const npm = (command, cwd) => execSync(`npm ${command}`, { cwd, stdio: 'inherit' });

const depot = join(work, 'depot');
rmSync(depot, { recursive: true, force: true });
git(['clone', '-q', '--filter=blob:none', '--no-checkout', 'https://github.com/raymondkrahwinkel/AI-Depot', depot]);
git(['-C', depot, 'checkout', '-q', commit]);
const notes = join(depot, 'src/Depot.Api/ClientApp/notes');
npm('ci --no-audit --no-fund --ignore-scripts', notes);
npm('rebuild esbuild', notes);

const out = join(here, 'room.bundle.js');
const { build, stop } = createRequire(join(notes, 'package.json'))('esbuild');
await build({
  entryPoints: [join(here, 'room.js')],
  outfile: out,
  bundle: true,
  format: 'iife',
  globalName: 'DepotRoom',
  target: 'es2022',
  platform: 'neutral',
  mainFields: ['module', 'main'],
  // Whitespace only, which also drops esbuild's path comments, and paths relative to the notes folder: the bytes do not depend on
  // where the checkout sits (AC-1896).
  minifyWhitespace: true,
  absWorkingDir: notes,
  // room.js sits outside the notes folder: its packages and 'depot-notes/' resolve to the notes folder's own.
  nodePaths: [join(notes, 'node_modules')],
  alias: { 'depot-notes': join(notes, 'src') },
  logLevel: 'warning',
});

// The bundle's own schema, run the way Jint runs it (lib0 wants crypto.getRandomValues at load).
const context = { crypto: { getRandomValues: (a) => a } };
vm.createContext(context);
vm.runInContext(`${readFileSync(out, 'utf8')};globalThis.R = DepotRoom;`, context);
const schema = readFileSync(join(depot, 'scripts/notes-roundtrip-fixtures/schema.json'), 'utf8').replace(/\r/g, '');
const expected = sha256(schema).slice(0, 12);
const actual = sha256(context.R.schemaJson()).slice(0, 12);
if (expected !== actual) {
  console.error(`FAIL the bundle's schema ${actual} is not schema.json ${expected} of ${commit}`);
  process.exit(1);
}
writeFileSync(join(here, 'schema.json'), schema);

const bundle = sha256(readFileSync(out));
// esbuild's service holds its own binary in the clone open; Windows refuses to remove that until the service is gone.
stop();
if (!process.argv[3]) {
  rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
console.log(`commit ${commit}\nschema ${expected}\nbundle ${bundle}`);
if (commit === pinned('DepotBundleCommit') && bundle !== pinned('DepotBundleSha256')) {
  console.error(`FAIL the pinned commit builds ${bundle}, the csproj pins ${pinned('DepotBundleSha256')}`);
  process.exit(1);
}
