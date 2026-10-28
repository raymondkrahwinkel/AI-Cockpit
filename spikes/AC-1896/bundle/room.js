// AC-1896 spike, workaround for G1/G2: the room's Y.Doc in Jint, with the web's own yjs and @tiptap/y-tiptap, filled and read with the
// calls editor.ts makes (prosemirrorJSONToYXmlFragment to seed, updateYFragment to change, yXmlFragmentToProsemirrorJSON to read).
// Bytes cross into .NET as base64; Jint has no atob/btoa, so the codec is here.
import * as Y from 'yjs';
import {
  absolutePositionToRelativePosition,
  initProseMirrorDoc,
  prosemirrorJSONToYXmlFragment,
  relativePositionToAbsolutePosition,
  updateYFragment,
  yXmlFragmentToProsemirrorJSON,
} from '@tiptap/y-tiptap';
import { getSchema } from '@tiptap/core';
import { lineEnding, noteExtensions, parseBody, serializeNote, splitFrontmatter } from 'depot-notes/markdown.ts';

const ABC = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
export function toBase64(bytes) {
  let out = '';
  for (let i = 0; i < bytes.length; i += 3) {
    const n = (bytes[i] << 16) | ((bytes[i + 1] ?? 0) << 8) | (bytes[i + 2] ?? 0);
    out += ABC[(n >> 18) & 63] + ABC[(n >> 12) & 63] + (i + 1 < bytes.length ? ABC[(n >> 6) & 63] : '=') + (i + 2 < bytes.length ? ABC[n & 63] : '=');
  }
  return out;
}
export function fromBase64(text) {
  const clean = text.replace(/=+$/, '');
  const bytes = new Uint8Array(Math.floor((clean.length * 3) / 4));
  let bits = 0;
  let value = 0;
  let at = 0;
  for (const c of clean) {
    value = (value << 6) | ABC.indexOf(c);
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      bytes[at++] = (value >> bits) & 255;
    }
  }
  return bytes;
}

const REMOTE = 'remote';
const SEED = 'seed';
let schema;
const noteSchema = () => (schema ??= getSchema(noteExtensions()));
let doc;
let outgoing = [];

export function createRoom(clientId) {
  doc = new Y.Doc();
  doc.clientID = clientId;
  outgoing = [];
  doc.on('update', (update, origin) => {
    if (origin !== REMOTE) {
      outgoing.push(toBase64(update));
    }
  });
}
const fragment = () => doc.getXmlFragment('default');
const meta = () => doc.getMap('meta');

export function apply(base64) {
  Y.applyUpdate(doc, fromBase64(base64), REMOTE);
}
export function takeUpdates() {
  const taken = outgoing;
  outgoing = [];
  return JSON.stringify(taken);
}
export function isEmpty() {
  return fragment().length === 0;
}
// What the room lacks: everything this doc has beyond the state vector of the rows the room holds (sync.ts' "missing").
export function missing(rowsJson) {
  const scratch = new Y.Doc();
  for (const row of JSON.parse(rowsJson)) {
    Y.applyUpdate(scratch, fromBase64(row));
  }
  return toBase64(Y.encodeStateAsUpdate(doc, Y.encodeStateVector(scratch)));
}

/** G1: the fragment from a *.pm.json tree, the front matter and eol beside it, in one transaction (editor.ts `seed`). */
export function seedPm(pmJson, frontmatter, eol) {
  doc.transact(() => {
    prosemirrorJSONToYXmlFragment(noteSchema(), JSON.parse(pmJson), fragment());
    if (frontmatter) {
      doc.getText('frontmatter').insert(0, frontmatter);
    }
    meta().set('eol', eol);
  }, SEED);
}
export function seedMarkdown(markdown) {
  const split = splitFrontmatter(markdown);
  seedPm(JSON.stringify(parseBody(split.body)), split.frontmatter, lineEnding(markdown));
}

/** G2: the fragment back as ProseMirror JSON, in the layout of *.pm.json. */
export function readPm() {
  return `${JSON.stringify(yXmlFragmentToProsemirrorJSON(fragment()), null, 2)}\n`;
}
/** A tree as the schema makes it (every attribute present): the fair form to compare two trees in, next to the raw text. */
export function normalize(pmJson) {
  return `${JSON.stringify(noteSchema().nodeFromJSON(JSON.parse(pmJson)).toJSON(), null, 2)}\n`;
}
export function readNote() {
  return serializeNote(doc.getText('frontmatter').toString(), yXmlFragmentToProsemirrorJSON(fragment()), meta().get('eol') === '\r\n' ? '\r\n' : '\n');
}

// The editor keeps a mapping between Y types and ProseMirror nodes in its binding; a bridge without a view keeps its own, refreshed
// after every change, so updateYFragment can skip what it has seen instead of comparing every block in full.
let binding = { mapping: new Map(), isOMark: new Map() };
let pmDoc;
function rebind() {
  const made = initProseMirrorDoc(fragment(), noteSchema());
  pmDoc = made.doc;
  binding = made.meta;
}

/** G4: a new body (as Markdown) put into the room by diffing against the fragment, the way editor.ts merges an outside write. */
export function setBody(markdown) {
  const node = noteSchema().nodeFromJSON(parseBody(splitFrontmatter(markdown).body));
  doc.transact(() => updateYFragment(doc, fragment(), node, binding));
  rebind();
}
/** G4, per block: only top-level block `index` is replaced by what its own Markdown parses to. */
export function setBlock(index, markdown) {
  const parsed = parseBody(markdown).content ?? [];
  if (!pmDoc) {
    rebind();
  }
  const blocks = pmDoc.toJSON().content ?? [];
  blocks.splice(index, 1, ...parsed);
  const node = noteSchema().nodeFromJSON({ type: 'doc', content: blocks });
  doc.transact(() => updateYFragment(doc, fragment(), node, binding));
  rebind();
}

/** G6: a ProseMirror position as relativePositionToJSON, and back. */
export function relativeAt(pos) {
  if (!pmDoc) {
    rebind();
  }
  return JSON.stringify(Y.relativePositionToJSON(absolutePositionToRelativePosition(pos, fragment(), binding.mapping)));
}
export function absoluteOf(relativeJson) {
  if (!pmDoc) {
    rebind();
  }
  return relativePositionToAbsolutePosition(doc, fragment(), Y.createRelativePositionFromJSON(JSON.parse(relativeJson)), binding.mapping);
}
export function stateVector() {
  return toBase64(Y.encodeStateVector(doc));
}
