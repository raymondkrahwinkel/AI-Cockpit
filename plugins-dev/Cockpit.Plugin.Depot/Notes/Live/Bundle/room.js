// AC-1899: the live note's room for Jint. The Y.Doc runs on Depot's own yjs and y-tiptap, and its text runs on Depot's own markdown.ts,
// so Cockpit writes what the web writes. The bridge keeps the host's Markdown (the mirror) and where each top-level block sits in it:
// a keystroke re-parses one block and changes one Y item, a remote update re-serializes only the blocks it touched (AC-1896 G4, G5).
import * as Y from 'yjs';
import { marked } from 'marked';
import { getSchema } from '@tiptap/core';
import { Fragment } from '@tiptap/pm/model';
import {
  absolutePositionToRelativePosition,
  initProseMirrorDoc,
  prosemirrorJSONToYXmlFragment,
  relativePositionToAbsolutePosition,
  updateYFragment,
  yXmlFragmentToProsemirrorJSON,
} from '@tiptap/y-tiptap';
import { lineEnding, noteExtensions, parseBody, serializeBody, serializeNote, splitFrontmatter } from 'depot-notes/markdown.ts';
import { mergeNote } from 'depot-notes/merge.ts';

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

// The host's own edits are LOCAL, and only those are undone (AC-1898: undo belongs to the source, like y-prosemirror's).
const REMOTE = 'remote';
const LOCAL = 'local';
const SEED = 'seed';
const OUTSIDE = 'outside';
// editor.ts: a block someone else in the room changed this recently may still have keystrokes on their way into it (DEP-405 M3).
const REMOTE_RECENT_MS = 5000;
let remoteAt = new WeakMap();
let doc;
let undoManager;
let outgoing = [];
let pmDoc = null; // the fragment as ProseMirror, the way the binding knows it; null until load
let binding;
let mirror = ''; // the host's text
let ranges = []; // per top-level block: [start, end) in the mirror, exact, or null where the block is not found in the text
let frontLength = 0; // the front matter's length at the start of the mirror: the Y text's, even when a merge left it odd

export function createRoom(clientId) {
  doc = new Y.Doc();
  doc.clientID = clientId;
  outgoing = [];
  pmDoc = null;
  mirror = '';
  ranges = [];
  frontLength = 0;
  remoteAt = new WeakMap();
  doc.on('update', (update, origin) => {
    if (origin !== REMOTE) {
      outgoing.push(toBase64(update));
    }
  });
  undoManager = new Y.UndoManager([doc.getXmlFragment('default'), doc.getText('frontmatter')], { trackedOrigins: new Set([LOCAL]) });
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
    prosemirrorJSONToYXmlFragment(noteSchema(), body(split.body).toJSON(), fragment());
    frontmatter().insert(0, split.frontmatter);
    doc.getMap('meta').set('eol', lineEnding(markdown));
  }, SEED);
}
/** Protocol §5.3: the rows are empty as text while the file has text. Rows that draw something, however odd, are not repaired here. */
export function needsRefill(markdown) {
  try {
    const rows = repair(noteSchema().nodeFromJSON(yXmlFragmentToProsemirrorJSON(fragment())));
    return (rows ? serializeBody(rows.toJSON()) : '').trim() === '' && serializeBody(body(splitFrontmatter(markdown).body).toJSON()).trim() !== '';
  } catch {
    return false;
  }
}

// Two edits merged can leave what the schema refuses (a list whose items were all deleted). The web's binding drops such a node; the
// bridge fills it in (repair) and writes that to the room once, so the doc reads again for everyone.
const REPAIR = 'repair';
function rebind() {
  let made;
  try {
    made = initProseMirrorDoc(fragment(), noteSchema());
    made.doc.check();
  } catch {
    const fixed = repair(noteSchema().nodeFromJSON(yXmlFragmentToProsemirrorJSON(fragment())));
    if (!fixed) {
      throw new Error('The room holds what no note can be.');
    }
    doc.transact(() => updateYFragment(doc, fragment(), fixed, { mapping: new Map(), isOMark: new Map() }), REPAIR);
    made = initProseMirrorDoc(fragment(), noteSchema());
    made.doc.check();
  }
  pmDoc = made.doc;
  binding = made.meta;
}
// Half-typed Markdown parses to what the schema refuses (`>` alone is a quote with nothing in it, `1.` an item without a paragraph):
// ProseMirror fills in what a node lacks, the way the editor itself would, so nothing invalid ever reaches the room.
function repair(node) {
  if (node.isLeaf) {
    return node;
  }
  const children = [];
  node.forEach((child) => {
    const fixed = repair(child);
    if (fixed) {
      children.push(fixed);
    }
  });
  const content = Fragment.fromArray(children);
  return node.type.validContent(content) ? node.copy(content) : node.type.createAndFill(node.attrs, content, node.marks);
}
function tree(json) {
  const node = repair(noteSchema().nodeFromJSON(json));
  if (!node) {
    throw new Error('The text does not make a note.');
  }
  node.check();
  return node;
}
const body = (markdown) => tree(parseBody(markdown));
// The blocks a stretch of the text parses to; none for blank text (where a whole note would get an empty paragraph to be valid).
function parsed(text) {
  const json = parseBody(text);
  const nodes = [];
  if ((json.content ?? []).length > 0) {
    tree(json).forEach((node) => nodes.push(node));
  }
  return nodes;
}
function blockText(node) {
  const text = serializeBody({ type: 'doc', content: [node.toJSON()] }).replace(/\n$/, '');
  return eol() === '\r\n' ? text.replace(/\n/g, '\r\n') : text;
}
// A block with no Markdown of its own (an empty paragraph, an upload's placeholder): it has no range, and an edit must keep it.
const hiddenCache = new WeakMap();
function hidden(node) {
  if (!hiddenCache.has(node)) {
    hiddenCache.set(node, blockText(node) === '');
  }
  return hiddenCache.get(node);
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

// The ranges of `nodes` in the mirror between `from` and `to`, from marked's own tokens (the parser's split of the text). A token
// takes as many nodes as its text parses to; a range counts only for a token that is exactly one node, and the first token that
// does not match ends the matching, so a range is exact or null and never a guess. marked lexes "\r\n" as "\n": offsets map back.
function spans(from, to, nodes) {
  const text = mirror.slice(from, to);
  const back = [];
  let lf = '';
  for (let i = 0; i < text.length; i++) {
    if (!(text[i] === '\r' && text[i + 1] === '\n')) {
      back.push(i);
      lf += text[i];
    }
  }
  back.push(text.length);
  const tokens = [];
  let at = 0;
  // The lexer @tiptap/markdown parses with: the global marked, with markdown.ts' tokenizers and the note extensions on it.
  for (const token of new marked.Lexer(marked.defaults).lex(lf)) {
    // A token's raw can carry the blank lines around it; the end maps from the block's last character, not a CRLF's "\r".
    const lead = token.raw.length - token.raw.replace(/^\n+/, '').length;
    const trimmed = token.raw.replace(/\n+$/, '').length;
    if (token.type !== 'space' && trimmed > lead) {
      tokens.push([from + back[at + lead], from + back[at + trimmed - 1] + 1]);
    }
    at += token.raw.length;
  }
  const out = [];
  let next = 0;
  let matching = true;
  while (out.length < nodes.length) {
    const node = nodes[out.length];
    if (!matching || hidden(node)) {
      out.push(null);
      continue;
    }
    const token = tokens[next++];
    const found = token ? parsed(mirror.slice(token[0], token[1])) : [];
    if (token && found.length === 0) {
      continue;
    }
    matching = found.length > 0 && found.every((made, k) => out.length + k < nodes.length && made.eq(nodes[out.length + k]));
    if (matching) {
      found.forEach(() => out.push(found.length === 1 ? token : null));
    }
  }
  return out;
}
function locate() {
  const children = [];
  pmDoc.forEach((node) => children.push(node));
  ranges = spans(frontLength, mirror.length, children);
}

/** Builds the mirror from the doc. Returns the ops that turn `previous` (the host's text so far) into it. */
export function load(previous) {
  rebind();
  mirror = serializeNote(frontmatter().toString(), pmDoc.toJSON(), eol());
  frontLength = frontmatter().length;
  locate();
  return JSON.stringify(previous === mirror ? [] : [minimalPatch(0, previous, mirror)]);
}
export function text() {
  return mirror;
}

// Cursors (AC-1900): a Markdown offset in the mirror <-> a ProseMirror position, per block, and from there a Yjs RelativePosition as
// relativePositionToJSON (spike AC-1896 G6). The block's text characters are aligned with its Markdown in order; what does not align is
// markup (#, -, **, `, |, a link's target), and an offset on markup goes to the next text character.
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
      pairs.push([mirror[k] === '\n' && mirror[k - 1] === '\r' ? k - 1 : k, chars[j].pos]);
      j++;
    }
  }
  return { pairs, end, last: chars.length ? chars[chars.length - 1].pos + 1 : base + 1 };
}
/** The cursor of a Markdown offset as relativePositionToJSON, or null where no block holds it (front matter, an unmatched block). */
export function relativeAtOffset(offset) {
  if (!pmDoc) {
    return null;
  }
  const index = ranges.findIndex((range) => range && range[0] <= offset && offset <= range[1]);
  if (index < 0) {
    return null;
  }
  const { pairs, last } = alignment(index);
  const hit = pairs.find(([k]) => k === offset);
  const after = pairs.find(([k]) => k === offset - 1);
  const next = pairs.find(([k]) => k >= offset);
  const pos = hit ? hit[1] : after ? after[1] + 1 : next ? next[1] : last;
  return JSON.stringify(Y.relativePositionToJSON(absolutePositionToRelativePosition(pos, fragment(), binding.mapping)));
}
/** The Markdown offset of a relativePositionToJSON value, or -1 when it is not in the text any more. */
export function offsetOfRelative(relativeJson) {
  if (!pmDoc) {
    return -1;
  }
  const pos = relativePositionToAbsolutePosition(doc, fragment(), Y.createRelativePositionFromJSON(JSON.parse(relativeJson)), binding.mapping);
  if (pos === null || pos === undefined) {
    return -1;
  }
  for (let index = 0; index < ranges.length; index++) {
    if (!ranges[index]) {
      continue;
    }
    const { pairs, end } = alignment(index);
    if (pairs.length && pos >= pairs[0][1] && pos <= pairs[pairs.length - 1][1] + 1) {
      const hit = pairs.find(([, p]) => p === pos);
      const after = pairs.find(([, p]) => p === pos - 1);
      const next = pairs.find(([, p]) => p >= pos);
      return hit ? hit[0] : after ? after[0] + 1 : next ? next[0] : Math.min(pairs[pairs.length - 1][0] + 1, end);
    }
  }
  return -1;
}

/** Protocol §6 duty 1: the note as the saver writes it, serializeNote of the doc (not the host's text, which may still be half-typed). */
export function serialized() {
  const json = pmDoc ? pmDoc.toJSON() : yXmlFragmentToProsemirrorJSON(fragment());
  return serializeNote(frontmatter().toString(), json, eol());
}

/**
 * Protocol §6 duty 4: merges an outside write (theirs, the file now) into the doc against base (the room's last save, or null when
 * it is gone), with Depot's own merge.ts, as editor.ts' mergeOnce does. Returns the host's ops, the v1 updates, the marked blocks.
 */
export function merge(base, theirs, from) {
  if (!pmDoc) {
    throw new Error('The room has no text to merge into yet.');
  }
  // As editor.ts' guardedSince: measured from when the merge was asked for, not from when a save in flight let it run.
  const since = Math.min(from, Date.now()) - REMOTE_RECENT_MS;
  const guarded = fragment().toArray().flatMap((block, index) => ((remoteAt.get(block) ?? 0) >= since ? [index] : []));
  const merged = mergeNote(base, theirs, { frontmatter: frontmatter().toString(), doc: pmDoc.toJSON() }, guarded);
  const node = tree(merged.doc);
  const ops = changed(() => doc.transact(() => {
    updateYFragment(doc, fragment(), node, binding);
    setText(frontmatter(), merged.frontmatter);
  }, OUTSIDE));
  const taken = outgoing;
  outgoing = [];
  return JSON.stringify({ ops, updates: taken, blocks: merged.changed, conflicts: merged.conflicts });
}

/**
 * The host's ops, each on the text the one before it left, as one local transaction. Returns the v1 updates to send. When an op
 * fails, the room goes back to the host's text before the edit and the error goes to the host, which then has nothing applied.
 */
export function edit(opsJson) {
  const ops = JSON.parse(opsJson);
  if (!pmDoc && ops.length > 0) {
    throw new Error('The room has no text to edit yet.');
  }
  const before = mirror;
  try {
    doc.transact(() => {
      for (const op of ops) {
        editOne(op.offset, op.length, op.text);
      }
    }, LOCAL);
  } catch (error) {
    mirror = before;
    doc.transact(() => {
      setText(frontmatter(), splitFrontmatter(mirror).frontmatter);
      frontLength = frontmatter().length;
      full();
    }, LOCAL);
    throw error;
  }
  const taken = outgoing;
  outgoing = [];
  return JSON.stringify(taken);
}
function editOne(offset, length, text) {
  const oldLength = mirror.length;
  mirror = mirror.slice(0, offset) + text + mirror.slice(offset + length);
  const front = splitFrontmatter(mirror).frontmatter;
  if (front !== frontmatter().toString() || offset < frontLength) {
    setText(frontmatter(), front);
    frontLength = front.length;
    full({ from: offset, to: offset + length, length: oldLength });
    return;
  }
  if (!editRun(offset, length, text.length)) {
    full({ from: offset, to: offset + length, length: oldLength });
  }
}
// Whether the edit touches the gap block `index` (one without text) sits in, between the blocks with text around it. A gap whose
// ends are not known counts as untouched.
function inGap(index, edited) {
  let start = frontLength;
  let stop = edited.length;
  for (let i = index - 1; i >= 0; i--) {
    if (!hidden(pmDoc.child(i))) {
      start = ranges[i]?.[1] ?? -1;
      break;
    }
  }
  for (let i = index + 1; i < pmDoc.childCount; i++) {
    if (!hidden(pmDoc.child(i))) {
      stop = ranges[i]?.[0] ?? -1;
      break;
    }
  }
  return start >= 0 && stop >= 0 && edited.from <= stop && edited.to >= start;
}
// The block with text nearest to `index` going `step`, past the ones without: null at the end, undefined when it has no range.
function neighbour(index, step) {
  for (let i = index; i >= 0 && i < pmDoc.childCount; i += step) {
    if (!hidden(pmDoc.child(i))) {
      return ranges[i] ? { index: i, node: pmDoc.child(i), range: ranges[i] } : undefined;
    }
  }
  return null;
}
// The blocks the edit touches, parsed again between the nearest blocks with text on either side: a change that joins the run to one
// of them (a list that grows into the next, a fence that swallows what follows) is seen, and the full path takes it. Blocks without
// text outside the run stay the same Y items.
function editRun(offset, removed, inserted) {
  const end = offset + removed;
  const delta = inserted - removed;
  let first = -1;
  let last = -1;
  ranges.forEach((range, i) => {
    if (range && first < 0 && range[1] >= offset) {
      first = i;
    }
    if (range && range[0] <= end) {
      last = i;
    }
  });
  if (first < 0 || last < 0) {
    return false;
  }
  if (last < first) {
    [first, last] = [last, first];
  }
  for (let i = first; i <= last; i++) {
    if (!ranges[i] && !hidden(pmDoc.child(i))) {
      return false;
    }
  }
  const before = neighbour(first - 1, -1);
  const after = neighbour(last + 1, 1);
  if (before === undefined || after === undefined) {
    return false;
  }
  const start = Math.min(ranges[first][0], offset);
  const stop = Math.max(ranges[last][1], end) + delta;
  const window = parsed(mirror.slice(before ? before.range[0] : start, after ? after.range[1] + delta : stop));
  const made = window.slice(before ? 1 : 0, after ? window.length - 1 : window.length);
  if ((before && !window[0]?.eq(before.node)) || (after && !window[window.length - 1]?.eq(after.node)) || made.length === 0) {
    return false;
  }
  const children = [];
  pmDoc.forEach((child, _, i) => {
    if (i === first) {
      children.push(...made);
    }
    if (i < first || i > last) {
      children.push(child);
    }
  });
  const next = pmDoc.copy(Fragment.fromArray(children));
  updateYFragment(doc, fragment(), next, binding);
  pmDoc = next;
  const shifted = ranges.slice(last + 1).map((range) => range && [range[0] + delta, range[1] + delta]);
  ranges = [...ranges.slice(0, first), ...spans(start, stop, made), ...shifted];
  return true;
}
// ponytail: a whole parse of the note (p95 350 ms at 2,000 lines), for an edit in the front matter or next to a block without a range.
// Blocks without text keep their place after the same number of blocks with text, so the Y items others type into stay, unless the
// edit (`edited`, old offsets) touches the gap they sit in: there the text decides. One the parse makes as well is that same item.
function full(edited) {
  const kept = [];
  let seen = 0;
  pmDoc.forEach((node, _, i) => {
    if (!hidden(node)) {
      seen++;
    } else if (!edited || !inGap(i, edited)) {
      kept.push({ node, after: seen, used: false });
    }
  });
  const children = [];
  let visible = 0;
  const flush = (all) => kept.filter((entry) => !entry.used && (all ? entry.after >= visible : entry.after === visible)).forEach((entry) => {
    entry.used = true;
    children.push(entry.node);
  });
  body(mirror.slice(frontLength)).forEach((node) => {
    if (hidden(node)) {
      const same = kept.find((entry) => !entry.used && entry.after === visible && entry.node.eq(node));
      if (same) {
        same.used = true;
      }
      children.push(same ? same.node : node);
      return;
    }
    flush(false);
    children.push(node);
    visible++;
  });
  flush(true);
  updateYFragment(doc, fragment(), pmDoc.copy(Fragment.fromArray(children)), binding);
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
  return JSON.stringify(changed(() => Y.applyUpdate(doc, fromBase64(base64), REMOTE), Date.now()));
}
/** Undoes (or redoes) the host's own edits; returns the ops for the host and the v1 updates to send. */
export function undo(redo) {
  const ops = pmDoc ? changed(() => (redo ? undoManager.redo() : undoManager.undo())) : [];
  const taken = outgoing;
  outgoing = [];
  return JSON.stringify({ ops, updates: taken });
}
// A change the host did not type, as ops on its text. When the doc no longer reads (another client wrote what the schema refuses),
// the bridge lets go of its view rather than edit on a stale one: the session loads again once the doc reads.
function changed(run, remote) {
  const was = new Map(fragment().toArray().map((type, i) => [type, ranges[i]]));
  const touched = new Set();
  let structural = false;
  let front = false;
  const added = [];
  const onBody = (events) => events.forEach((event) => {
    if (event.target === fragment()) {
      structural = true;
      event.changes.added.forEach((item) => item.content.getContent().forEach((type) => added.push(type)));
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
    run();
  } finally {
    fragment().unobserveDeep(onBody);
    frontmatter().unobserve(onFront);
  }
  if (remote !== undefined) {
    [...touched, ...added].forEach((type) => typeof type === 'object' && type !== null && remoteAt.set(type, remote));
  }
  if (!structural && !front && touched.size === 0) {
    return [];
  }
  try {
    rebind();
    return rebuild(was, touched, front);
  } catch (error) {
    pmDoc = null;
    ranges = [];
    throw error;
  }
}
function rebuild(was, touched, front) {
  const old = mirror;
  const blank = (from, to) => /^\s*$/.test(old.slice(from, to));
  const oldFront = frontLength;
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
    } else if (kept && !front && oldFront <= kept[0] && blank(oldFront, kept[0])) {
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
  frontLength = front ? frontmatter().length : oldFront;
  ranges = blocks.map((p) => (p === null ? null : at[p]));
  return ops;
}
