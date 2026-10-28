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
import { Fragment } from '@tiptap/pm/model';
import { lineEnding, noteExtensions, parseBody, serializeBody, serializeNote, splitFrontmatter } from 'depot-notes/markdown.ts';

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
/** The counter-proof: the same diff in Yjs' v2 encoding, which the hub takes without looking (protocol §5.1). */
export function missingV2(rowsJson) {
  const scratch = new Y.Doc();
  for (const row of JSON.parse(rowsJson)) {
    Y.applyUpdate(scratch, fromBase64(row));
  }
  return toBase64(Y.encodeStateAsUpdateV2(doc, Y.encodeStateVector(scratch)));
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

// G4, the incremental bridge: the host holds Markdown text and sends patches (offset, removed length, inserted text). The room keeps the
// same text (the mirror) and where each top-level block sits in it, so a keystroke re-parses one block, not the note.
let mirror = '';
let ranges = []; // [start, end) of each top-level block in mirror (the body, after the front matter)
let bodyStart = 0;

function blockText(node) {
  return serializeBody({ type: 'doc', content: [node.toJSON()] }).replace(/\n$/, '');
}
/** Builds the mirror and the block ranges from the room. Returns how many blocks were found in the text (the rest are a miss). */
export function loadMirror() {
  rebind();
  mirror = readNote();
  bodyStart = doc.getText('frontmatter').toString().length;
  ranges = [];
  let at = bodyStart;
  let found = 0;
  pmDoc.forEach((node) => {
    const text = blockText(node);
    const start = mirror.indexOf(text, at);
    if (start < 0) {
      ranges.push(null);
      return;
    }
    found++;
    ranges.push([start, start + text.length]);
    at = start + text.length;
  });
  return JSON.stringify({ blocks: pmDoc.childCount, found, length: mirror.length });
}
/** One patch from the host. Returns which block it hit and whether the mirror still equals a full serialization (when checked). */
export function edit(offset, removed, inserted) {
  const index = ranges.findIndex((r) => r && r[0] <= offset && offset + removed <= r[1]);
  mirror = mirror.slice(0, offset) + inserted + mirror.slice(offset + removed);
  if (index < 0) {
    // ponytail: an edit across blocks or between them takes the full path; per-range splitting if the host sends many of those.
    setBody(mirror);
    loadMirror();
    return JSON.stringify({ block: -1 });
  }
  const [start, end] = ranges[index];
  const newEnd = end - removed + inserted.length;
  const parsed = (parseBody(mirror.slice(start, newEnd)).content ?? []).map((json) => noteSchema().nodeFromJSON(json));
  const children = [];
  pmDoc.forEach((node, _, i) => children.push(...(i === index ? parsed : [node])));
  const next = pmDoc.copy(Fragment.fromArray(children));
  doc.transact(() => updateYFragment(doc, fragment(), next, binding));
  pmDoc = next;
  const delta = inserted.length - removed;
  ranges = ranges.flatMap((r, i) => (i === index ? parsed.map(() => [start, newEnd]) : [r && i > index ? [r[0] + delta, r[1] + delta] : r]));
  return JSON.stringify({ block: index, parsed: parsed.length });
}
export function mirrorIsExact() {
  return mirror === readNote();
}

// G5: a change that did not come from the host (a remote update, or an undo) goes back to the host as text patches on the mirror.
// Only the top-level blocks the change touched are serialized again; a change to the list of blocks itself takes the full text.
let undoManager;
export function trackUndo() {
  // Tracks only transactions with origin null: the host's own edits (`edit`). Remote updates and the seed carry their own origin.
  undoManager = new Y.UndoManager(fragment(), { captureTimeout: 0 });
}
function minimalPatch(offset, before, after) {
  let start = 0;
  while (start < before.length && start < after.length && before[start] === after[start]) {
    start++;
  }
  let end = 0;
  while (end < before.length - start && end < after.length - start && before[before.length - 1 - end] === after[after.length - 1 - end]) {
    end++;
  }
  return { offset: offset + start, removed: before.length - start - end, inserted: after.slice(start, after.length - end) };
}
function patchesFor(change) {
  const touched = new Set();
  let structural = false;
  const observer = (events) => events.forEach((event) => {
    if (event.target === fragment()) {
      structural = true;
    } else {
      touched.add(event.path[0]);
    }
  });
  fragment().observeDeep(observer);
  try {
    change();
  } finally {
    fragment().unobserveDeep(observer);
  }
  if (touched.size === 0 && !structural) {
    return [];
  }
  const old = mirror;
  rebind();
  if (structural) {
    loadMirror();
    return [minimalPatch(0, old, mirror)];
  }
  // Last block first, so each patch's offset is still right when the host applies them in order.
  const patches = [...touched].sort((a, b) => b - a).map((index) => {
    const [start, end] = ranges[index];
    const text = blockText(pmDoc.child(index));
    mirror = mirror.slice(0, start) + text + mirror.slice(end);
    const delta = text.length - (end - start);
    ranges = ranges.map((r, i) => (i === index ? [start, start + text.length] : i > index && r ? [r[0] + delta, r[1] + delta] : r));
    return minimalPatch(start, old.slice(start, end), text);
  });
  return patches;
}
export function applyRemote(base64) {
  return JSON.stringify(patchesFor(() => Y.applyUpdate(doc, fromBase64(base64), REMOTE)));
}
export function undo() {
  return JSON.stringify(patchesFor(() => undoManager.undo()));
}

// G6: a Markdown offset in the mirror <-> a ProseMirror position, per block. The block's text characters are aligned with its Markdown
// in order; what does not align is markup (#, -, **, `, |, a link's target). An offset on markup goes to the next text character.
// ponytail: greedy alignment, right for text that does not repeat the markup right next to it; a serializer that reports positions
// (or a per-node source map) if real notes show misses.
function alignment(index) {
  const [start, end] = ranges[index];
  let base = 0;
  for (let i = 0; i < index; i++) {
    base += pmDoc.child(i).nodeSize;
  }
  const chars = [];
  pmDoc.child(index).descendants((node, pos) => {
    if (node.isText) {
      for (let k = 0; k < node.text.length; k++) {
        chars.push({ c: node.text[k], pos: base + 1 + pos + k });
      }
    }
  });
  const pairs = []; // [markdown offset, pm position]
  let j = 0;
  for (let k = start; k < end && j < chars.length; k++) {
    if (mirror[k] === chars[j].c && !(mirror[k] === '\\' && chars[j].c !== '\\')) {
      pairs.push([k, chars[j].pos]);
      j++;
    }
  }
  return { pairs, end, last: chars.length ? chars[chars.length - 1].pos + 1 : base + 1 };
}
export function pmPosOfOffset(offset) {
  const index = ranges.findIndex((r) => r && r[0] <= offset && offset <= r[1]);
  const { pairs, last } = alignment(index);
  const hit = pairs.find(([k]) => k >= offset);
  return hit ? hit[1] : last;
}
export function offsetOfPmPos(pos) {
  for (let index = 0; index < ranges.length; index++) {
    const { pairs, end } = alignment(index);
    if (pairs.length && pos >= pairs[0][1] && pos <= pairs[pairs.length - 1][1] + 1) {
      const hit = pairs.find(([, p]) => p >= pos);
      return hit ? hit[0] : pairs[pairs.length - 1][0] + 1 > end ? end : pairs[pairs.length - 1][0] + 1;
    }
  }
  return -1;
}
export function relativeAtOffset(offset) {
  return relativeAt(pmPosOfOffset(offset));
}
export function offsetOfRelative(relativeJson) {
  return offsetOfPmPos(absoluteOf(relativeJson));
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
