// Sanity check: run the extractor against a REAL local transcript file
// (not synthetic) and confirm it recovers the actual last assistant reply.
const { execSync } = require('child_process');
const path = require('path');

const realTranscript = String.raw`C:\Users\raymo\.claude\projects\C--Users-raymo\0cbb88a8-6174-45d0-8429-f79e8d93ad8e.jsonl`;
const hookCmd = `node "${path.join(__dirname, 'extract-last-assistant-text.js')}"`;

const stdinPayload = JSON.stringify({
  session_id: 'real-check',
  transcript_path: realTranscript,
  cwd: __dirname,
});

execSync(hookCmd, { input: stdinPayload, stdio: ['pipe', 'inherit', 'inherit'] });

const fs = require('fs');
const logLines = fs.readFileSync(path.join(__dirname, 'extracted.log.jsonl'), 'utf8').split('\n').filter(Boolean);
const last = JSON.parse(logLines[logLines.length - 1]);
console.log('Extracted from REAL transcript:', JSON.stringify(last.text));
console.log('Expected (from manual tail inspection): "Tot de volgende keer! \\ud83d\\udc4b" (waving hand emoji)');
