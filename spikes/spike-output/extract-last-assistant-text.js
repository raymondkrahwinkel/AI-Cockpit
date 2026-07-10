#!/usr/bin/env node
/**
 * Zyra-Voice spike-OUTPUT probe.
 *
 * Reads the Stop-hook JSON from stdin ({session_id, transcript_path, cwd, ...}),
 * reads transcript_path (JSONL), extracts the LAST assistant text reply
 * (searching backwards past tool-only turns, tool_result/user turns, and
 * housekeeping lines like "last-prompt" / "attachment" / "queue-operation"),
 * and appends the extracted text as one line of JSON to a local log file so
 * we can inspect after the run whether it matched what Claude actually said.
 *
 * This is throwaway spike code, not production. Kept deliberately dumb/linear.
 */
const fs = require('fs');
const path = require('path');

const LOG_FILE = path.join(__dirname, 'extracted.log.jsonl');

function readStdin() {
  try {
    return fs.readFileSync(0, 'utf8');
  } catch (e) {
    return '';
  }
}

function lastAssistantText(transcriptPath) {
  let raw;
  try {
    raw = fs.readFileSync(transcriptPath, 'utf8');
  } catch (e) {
    return { error: `cannot read transcript_path: ${e.message}` };
  }

  const lines = raw.split('\n').filter(Boolean);

  // Walk backwards. Skip anything that isn't an assistant message, and skip
  // assistant messages whose content has no text block (i.e. tool-only turns).
  for (let i = lines.length - 1; i >= 0; i--) {
    let obj;
    try {
      obj = JSON.parse(lines[i]);
    } catch (e) {
      continue; // malformed line - skip, do not fail the whole hook
    }

    if (obj.type !== 'assistant') continue;
    if (!obj.message || obj.message.role !== 'assistant') continue;
    if (obj.isSidechain) continue; // exclude subagent chatter from main TTS stream

    const content = Array.isArray(obj.message.content) ? obj.message.content : [];
    const textBlocks = content.filter(b => b && b.type === 'text' && typeof b.text === 'string');
    if (textBlocks.length === 0) continue; // tool-only turn, keep looking further back

    const text = textBlocks.map(b => b.text).join('\n\n');
    return {
      text,
      matchedLineIndex: i,
      totalLines: lines.length,
      stopReason: obj.message.stop_reason || null,
      uuid: obj.uuid || null,
    };
  }

  return { error: 'no assistant text message found in transcript' };
}

function main() {
  const stdinRaw = readStdin();
  let hookInput;
  try {
    hookInput = JSON.parse(stdinRaw);
  } catch (e) {
    fs.appendFileSync(LOG_FILE, JSON.stringify({ ts: new Date().toISOString(), error: 'bad stdin JSON', raw: stdinRaw }) + '\n');
    process.exit(0); // non-blocking: don't break the session over a spike bug
  }

  const { session_id, transcript_path } = hookInput;
  const result = lastAssistantText(transcript_path);

  const entry = {
    ts: new Date().toISOString(),
    session_id,
    transcript_path,
    ...result,
  };

  fs.appendFileSync(LOG_FILE, JSON.stringify(entry) + '\n');

  // "Forward to harness" stand-in: in production this would push to a
  // localhost socket / named pipe that feeds TTS. For the spike, appending
  // to a well-known file is the simplest thing that proves the mechanism.
  process.exit(0);
}

main();
