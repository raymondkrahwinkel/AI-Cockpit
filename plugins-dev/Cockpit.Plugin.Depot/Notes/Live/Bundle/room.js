// AC-1897: the room's Y.Doc for Jint, with Depot's own yjs (the version in its lockfile) and nothing else. Bytes cross into .NET as
// base64; Jint has no atob/btoa, so the codec is here. AC-1899 replaces this entry with the full bridge (markdown, y-tiptap).
import * as Y from 'yjs';

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

const REMOTE = 'remote';
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

/** A row or an update from the hub; origin 'remote' keeps it out of the outgoing list. */
export function apply(base64) {
  Y.applyUpdate(doc, fromBase64(base64), REMOTE);
}

/** What the hub lacks: everything this doc has beyond the state of `knownJson` (rows and early updates), as a v1 update. */
export function missing(knownJson) {
  const known = new Y.Doc();
  for (const update of JSON.parse(knownJson)) {
    Y.applyUpdate(known, fromBase64(update));
  }
  return toBase64(Y.encodeStateAsUpdate(doc, Y.encodeStateVector(known)));
}

/** Local changes made since the last call, each a v1 update (protocol §5.1: never v2). */
export function takeUpdates() {
  const taken = outgoing;
  outgoing = [];
  return JSON.stringify(taken);
}

/** The doc as text: the body fragment as XML and the front matter, until AC-1899 reads it as ProseMirror JSON. */
export function read() {
  return JSON.stringify({ fragment: doc.getXmlFragment('default').toString(), frontmatter: doc.getText('frontmatter').toString() });
}
