// AC-1899: the live note's room for Jint. The Y.Doc runs on Depot's own yjs and y-tiptap, and its text runs on Depot's own markdown.ts,
// so Cockpit writes what the web writes. The bridge keeps the host's Markdown (the mirror) and where each top-level block sits in it:
// a keystroke re-parses one block and changes one Y item, a remote update re-serializes only the blocks it touched (AC-1896 G4, G5).
import * as Y from 'yjs';
import { marked } from 'marked';
import { getSchema } from '@tiptap/core';
import { Fragment } from '@tiptap/pm/model';
import { initProseMirrorDoc, prosemirrorJSONToYXmlFragment, updateYFragment, yXmlFragmentToProsemirrorJSON } from '@tiptap/y-tiptap';
import { lineEnding, noteExtensions, parseBody, serializeBody, serializeNote, splitFrontmatter } from 'depot-notes/markdown.ts';

export { Y };

// Bytes cross into .NET as base64; Jint has no atob/btoa, so the codec is here.
const ABC = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
function toBase64(bytes) {
  let out = '';
  for (let i = 0; i < bytes.length; i += 3) {
    const n = (bytes[i] << 16) | ((bytes[i + 1] ?? 0) << 8) | (bytes[i + 2] ?? 0);
    out += ABC[(n >> 18) & 63] + ABC[(n >> 12) & 63] + (i + 1 < bytes.length ? ABC[(n >> 6) & 63] : '=') + (i + 2 < bytes.length ? ABC[n & 63] : '=');
  }
  return out;
}
function fromBase64(text) {
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

// The description notes-roundtrip-check.mjs writes to schema.json; its SHA-256 prefix is the version Join carries (protocol §5.4).
const describe = (kind) => Object.fromEntries(Object.entries(kind).sort(([a], [b]) => (a < b ? -1 : 1)).map(([name, type]) => [name, {
  group: type.spec.group ?? null, content: type.spec.content ?? null, marks: type.spec.marks ?? null, inline: type.isInline ?? null,
  atom: type.isAtom ?? null, isolating: type.spec.isolating ?? null, code: type.spec.code ?? null, defining: type.spec.defining ?? null,
  attrs: Object.fromEntries(Object.entries(type.attrs).map(([key, attr]) => [key, attr.hasDefault ? { default: attr.default } : { required: true }])),
}]));
let schema;
const noteSchema = () => (schema ??= getSchema(noteExtensions()));
export function schemaJson() {
  return `${JSON.stringify({ nodes: describe(noteSchema().nodes), marks: describe(noteSchema().marks) }, null, 2)}\n`;
}

// The converter on its own, as notes-roundtrip-check.mjs runs it: the contract with Depot's fixtures.
export function parse(markdown) {
  return JSON.stringify(parseBody(splitFrontmatter(markdown).body));
}
/** The tree written as the file `markdown` is: with its front matter and its line ending. */
export function serialize(markdown, docJson) {
  return serializeNote(splitFrontmatter(markdown).frontmatter, JSON.parse(docJson), lineEnding(markdown));
}

const REMOTE = 'remote';
const LOCAL = 'local';
let doc;
let outgoing = [];
let pmDoc = null; // the fragment as ProseMirror, the way the binding knows it; null until load
let binding;
let mirror = ''; // the host's text
let ranges = []; // per top-level block: [start, end) in the mirror, exact, or null where the block is not found in the text

export function createRoom(clientId) {
  doc = new Y.Doc();
  doc.clientID = clientId;
  outgoing = [];
  pmDoc = null;
  mirror = '';
  ranges = [];
  doc.on('update', (update, origin) => {
    if (origin !== REMOTE) {
      outgoing.push(toBase64(update));
    }
  });
  // markdown.ts sets up marked on its first parse; the lexer below has to see the same tokenizers.
  parseBody('');
}
export const current = () => doc;
const fragment = () => doc.getXmlFragment('default');
const frontmatter = () => doc.getText('frontmatter');
const eol = () => (doc.getMap('meta').get('eol') === '\r\n' ? '\r\n' : '\n');

/** What the hub lacks: this doc beyond the state of `knownJson` (rows and early updates), as one v1 update that covers every local one. */
export function missing(knownJson) {
  const known = new Y.Doc();
  for (const update of JSON.parse(knownJson)) {
    Y.applyUpdate(known, fromBase64(update));
  }
  outgoing = [];
  return toBase64(Y.encodeStateAsUpdate(doc, Y.encodeStateVector(known)));
}
export function snapshot() {
  return toBase64(Y.encodeStateAsUpdate(doc));
}
export function isEmpty() {
  return fragment().length === 0 && frontmatter().length === 0;
}
/** Protocol §5.3: the front matter, the body and the eol in one transaction. */
export function seed(markdown) {
  const split = splitFrontmatter(markdown);
  doc.transact(() => {
    prosemirrorJSONToYXmlFragment(noteSchema(), parseBody(split.body), fragment());
    frontmatter().insert(0, split.frontmatter);
    doc.getMap('meta').set('eol', lineEnding(markdown));
  }, LOCAL);
}
/** Protocol §5.3: the rows draw nothing while the file has text. */
export function needsRefill(markdown) {
  let drawn = '';
  try {
    const node = noteSchema().nodeFromJSON(yXmlFragmentToProsemirrorJSON(fragment()));
    node.check();
    drawn = serializeBody(node.toJSON());
  } catch {
    drawn = '';
  }
  return drawn.trim() === '' && serializeBody(parseBody(splitFrontmatter(markdown).body)).trim() !== '';
}

function rebind() {
  const made = initProseMirrorDoc(fragment(), noteSchema());
  pmDoc = made.doc;
  binding = made.meta;
}
const parsed = (text) => (parseBody(text).content ?? []).map((json) => noteSchema().nodeFromJSON(json));
const invisible = (node) => node.type.name === 'paragraph' && node.content.size === 0;
function blockText(node) {
  const text = serializeBody({ type: 'doc', content: [node.toJSON()] }).replace(/\n$/, '');
  return eol() === '\r\n' ? text.replace(/\n/g, '\r\n') : text;
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
  return { offset: offset + start, length: before.length - start - end, text: after.slice(start, after.length - end) };
}
function setText(type, value) {
  const patch = minimalPatch(0, type.toString(), value);
  type.delete(patch.offset, patch.length);
  type.insert(patch.offset, patch.text);
}

// Each top-level block's range, from marked's own tokens (the parser's split of the text); a range counts only when its text parses
// to exactly that block, so a range is exact or null and never a guess. marked lexes "\r\n" as "\n", so CRLF offsets are mapped back.
function locate() {
  const bodyStart = splitFrontmatter(mirror).frontmatter.length;
  const body = mirror.slice(bodyStart);
  const back = [];
  let lf = '';
  for (let i = 0; i < body.length; i++) {
    if (!(body[i] === '\r' && body[i + 1] === '\n')) {
      back.push(i);
      lf += body[i];
    }
  }
  back.push(body.length);
  const spans = [];
  let at = 0;
  // The lexer @tiptap/markdown parses with: the global marked, with markdown.ts' tokenizers and the note extensions on it.
  for (const token of new marked.Lexer(marked.defaults).lex(lf)) {
    // A token's raw can carry the blank lines around it (a list after a list starts with one); the range is the block's own text.
    if (token.type !== 'space') {
      const lead = token.raw.length - token.raw.replace(/^\n+/, '').length;
      // The end maps from the block's last character: the one after it may be the "\r" of a CRLF.
      spans.push([bodyStart + back[at + lead], bodyStart + back[at + token.raw.replace(/\n+$/, '').length - 1] + 1]);
    }
    at += token.raw.length;
  }
  let next = 0;
  ranges = [];
  pmDoc.forEach((node) => {
    const span = invisible(node) ? null : spans[next++];
    const found = span ? parsed(mirror.slice(span[0], span[1])) : [];
    ranges.push(span && found.length === 1 && found[0].eq(node) ? span : null);
  });
}

/** Builds the mirror from the doc. Returns the ops that turn `previous` (the host's text so far) into it. */
export function load(previous) {
  rebind();
  mirror = serializeNote(frontmatter().toString(), pmDoc.toJSON(), eol());
  locate();
  return JSON.stringify(previous === mirror ? [] : [minimalPatch(0, previous, mirror)]);
}
export function text() {
  return mirror;
}

/** The host's ops, each on the text the one before it left, as one local transaction. Returns the v1 updates to send. */
export function edit(opsJson) {
  doc.transact(() => {
    for (const op of JSON.parse(opsJson)) {
      editOne(op.offset, op.length, op.text);
    }
  }, LOCAL);
  const taken = outgoing;
  outgoing = [];
  return JSON.stringify(taken);
}
function editOne(offset, length, text) {
  mirror = mirror.slice(0, offset) + text + mirror.slice(offset + length);
  const front = splitFrontmatter(mirror).frontmatter;
  if (front !== frontmatter().toString()) {
    setText(frontmatter(), front);
    full();
    return;
  }
  const index = ranges.findIndex((range) => range && range[0] <= offset && offset + length <= range[1]);
  if (index < 0 || !editBlock(index, length, text.length)) {
    full();
  }
}
// The neighbour the edited block has to stay apart from: null when there is none (or only an empty paragraph, which has no text),
// undefined when it has text but no range, which leaves the edit to the full path.
function neighbour(index) {
  if (index < 0 || index >= pmDoc.childCount || invisible(pmDoc.child(index))) {
    return null;
  }
  return ranges[index] ? { node: pmDoc.child(index), range: ranges[index] } : undefined;
}
// One block, parsed again with its neighbours, so a change that would join it to one of them (a list that grows into the next) is seen.
function editBlock(index, removed, inserted) {
  const [start, end] = ranges[index];
  const delta = inserted - removed;
  const before = neighbour(index - 1);
  const after = neighbour(index + 1);
  if (before === undefined || after === undefined) {
    return false;
  }
  const window = parsed(mirror.slice(before ? before.range[0] : start, after ? after.range[1] + delta : end + delta));
  const expected = (before ? 1 : 0) + 1 + (after ? 1 : 0);
  if (window.length !== expected || (before && !window[0].eq(before.node)) || (after && !window[expected - 1].eq(after.node))) {
    return false;
  }
  const made = window[before ? 1 : 0];
  const children = [];
  pmDoc.forEach((child, _, i) => children.push(i === index ? made : child));
  const next = pmDoc.copy(Fragment.fromArray(children));
  fragment().delete(0, fragment().length);
  prosemirrorJSONToYXmlFragment(noteSchema(), next.toJSON(), fragment());
  rebind();
  ranges = ranges.map((range, i) => (i === index ? [start, end + delta] : range && i > index ? [range[0] + delta, range[1] + delta] : range));
  return true;
}
// ponytail: a whole parse of the note (p95 350 ms at 2,000 lines), for an edit across blocks or one that splits a block in two;
// splitting per range if Enter-Enter in long notes turns out slow.
function full() {
  const node = noteSchema().nodeFromJSON(parseBody(splitFrontmatter(mirror).body));
  updateYFragment(doc, fragment(), node, binding);
  rebind();
  locate();
}

/**
 * A row or an update from the hub. Once the mirror is loaded, returns what it changed in the host's text: blocks it did not touch
 * keep the host's own text, the touched ones are serialized again, and only the stretches that differ go out as ops.
 */
export function apply(base64) {
  if (!pmDoc) {
    Y.applyUpdate(doc, fromBase64(base64), REMOTE);
    return '[]';
  }
  const was = new Map(fragment().toArray().map((type, i) => [type, ranges[i]]));
  const touched = new Set();
  let structural = false;
  let front = false;
  const onBody = (events) => events.forEach((event) => {
    if (event.target === fragment()) {
      structural = true;
    } else {
      touched.add(fragment().get(event.path[0]));
    }
  });
  const onFront = () => {
    front = true;
  };
  fragment().observeDeep(onBody);
  frontmatter().observe(onFront);
  try {
    Y.applyUpdate(doc, fromBase64(base64), REMOTE);
  } finally {
    fragment().unobserveDeep(onBody);
    frontmatter().unobserve(onFront);
  }
  if (!structural && !front && touched.size === 0) {
    return '[]';
  }
  rebind();
  return JSON.stringify(rebuild(was, touched, front));
}
function rebuild(was, touched, front) {
  const old = mirror;
  const blank = (from, to) => /^\s*$/.test(old.slice(from, to));
  const oldFront = splitFrontmatter(old).frontmatter.length;
  // The new text as pieces: a stretch of the old text ([start, end)) or new text.
  const pieces = [front ? { text: frontmatter().toString() } : { keep: [0, oldFront] }];
  const blocks = [];
  let last = null;
  let any = false;
  fragment().toArray().forEach((type, i) => {
    const node = pmDoc.child(i);
    const kept = touched.has(type) ? null : (was.get(type) ?? null);
    const text = kept ? null : blockText(node);
    if (!kept && text === '') {
      blocks.push(null);
      return;
    }
    if (any) {
      pieces.push(last && kept && last[1] <= kept[0] && blank(last[1], kept[0]) ? { keep: [last[1], kept[0]] } : { text: eol() + eol() });
    } else if (kept && blank(oldFront, kept[0]) && !front) {
      pieces.push({ keep: [oldFront, kept[0]] });
    }
    blocks.push(pieces.length);
    pieces.push(kept ? { keep: kept } : { text });
    last = kept;
    any = true;
  });
  if (any) {
    pieces.push(last && blank(last[1], old.length) ? { keep: [last[1], old.length] } : { text: eol() });
  }

  // Walk the pieces: each run of new text between two kept stretches replaces the old text between them, minimally.
  const ops = [];
  const at = [];
  let out = '';
  let oldAt = 0;
  let runStart = 0;
  const close = (until) => {
    const replaced = old.slice(oldAt, until);
    const inserted = out.slice(runStart);
    if (replaced !== inserted) {
      ops.push(minimalPatch(runStart, replaced, inserted));
    }
  };
  pieces.forEach((piece, p) => {
    const start = out.length;
    if (piece.keep && piece.keep[0] >= oldAt) {
      close(piece.keep[0]);
      out += old.slice(piece.keep[0], piece.keep[1]);
      oldAt = piece.keep[1];
      runStart = out.length;
    } else {
      out += piece.keep ? old.slice(piece.keep[0], piece.keep[1]) : piece.text;
    }
    at[p] = [start, out.length];
  });
  close(old.length);
  mirror = out;
  ranges = blocks.map((p) => (p === null ? null : at[p]));
  return ops;
}
