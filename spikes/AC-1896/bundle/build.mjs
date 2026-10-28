// AC-1896 spike: Depot's own markdown.ts and merge.ts (plus marked, @tiptap/markdown) bundled into one script for Jint.
// Usage: node build.mjs <depot notes dir> <out.js> [--yjs] [--marked <file>]
//   --yjs     also the room (room.js: yjs + @tiptap/y-tiptap), the workaround for G1/G2
//   --marked  swaps marked for another copy: the counter-proof
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const [notes, out] = process.argv.slice(2).map((p) => resolve(p));
// esbuild comes from the notes folder's own node_modules, so the bundle is built with the version Depot builds with.
const { build } = createRequire(join(notes, 'package.json'))('esbuild');
const markedAt = process.argv.indexOf('--marked');
const yjs = process.argv.includes('--yjs');
const here = dirname(fileURLToPath(import.meta.url));
const entry = `
import { parseBody, serializeNote, serializeBody, splitFrontmatter, lineEnding, roundTrip, noteExtensions } from './src/markdown.ts';
import { mergeNote } from './src/merge.ts';
import { getSchema } from '@tiptap/core';
// The same description notes-roundtrip-check.mjs writes to schema.json; its SHA-256 prefix is the version Join carries.
const describe = (kind) => Object.fromEntries(Object.entries(kind).sort(([a], [b]) => (a < b ? -1 : 1)).map(([name, type]) => [name, {
  group: type.spec.group ?? null, content: type.spec.content ?? null, marks: type.spec.marks ?? null, inline: type.isInline ?? null,
  atom: type.isAtom ?? null, isolating: type.spec.isolating ?? null, code: type.spec.code ?? null, defining: type.spec.defining ?? null,
  attrs: Object.fromEntries(Object.entries(type.attrs).map(([key, attr]) => [key, attr.hasDefault ? { default: attr.default } : { required: true }])),
}]));
export function schemaJson() { const s = getSchema(noteExtensions()); return JSON.stringify({ nodes: describe(s.nodes), marks: describe(s.marks) }, null, 2) + String.fromCharCode(10); }
export function parse(markdown) { return JSON.stringify(parseBody(splitFrontmatter(markdown).body), null, 2) + String.fromCharCode(10); }
export function serialize(frontmatter, docJson, eol) { return serializeNote(frontmatter, JSON.parse(docJson), eol); }
export function merge(base, theirs, frontmatter, docJson) { const m = mergeNote(base, theirs, { frontmatter, doc: JSON.parse(docJson) }); return JSON.stringify({ ...m, doc: m.doc }); }
export { roundTrip, splitFrontmatter, lineEnding, serializeBody };
${yjs ? `export * as room from ${JSON.stringify(join(here, 'room.js'))};` : ''}
`;
await build({
  stdin: { contents: entry, resolveDir: notes, loader: 'ts', sourcefile: 'entry.ts' },
  outfile: out,
  bundle: true,
  format: 'iife',
  globalName: 'DepotNotes',
  target: 'es2022',
  platform: 'neutral',
  mainFields: ['module', 'main'],
  // Whitespace only, which also drops esbuild's path comments: the output must not depend on where the checkout sits (AC-1896).
  minifyWhitespace: true,
  // CommonJS wrappers carry their module path; relative to the notes folder it is the same in every checkout.
  absWorkingDir: notes,
  // room.js sits outside the notes folder: its packages and 'depot-notes/' resolve to the notes folder's own.
  nodePaths: [join(notes, 'node_modules')],
  alias: { 'depot-notes': join(notes, 'src'), ...(markedAt > 0 ? { marked: resolve(process.argv[markedAt + 1]) } : {}) },
  logLevel: 'warning',
});
console.log(out);
