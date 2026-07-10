#!/usr/bin/env node
/**
 * Since this sandbox cannot obtain an authenticated real `claude` session
 * (bundled claude.exe found at
 *  C:\Users\raymo\AppData\Roaming\Claude\claude-code\2.1.197\claude.exe
 *  reports "Not logged in"; the harness correctly blocked credential-hunting
 *  to fix that), this script proves the Stop-hook -> JSONL -> extracted-text
 *  pipeline using a synthetic transcript written in the REAL format observed
 *  in an actual local transcript file:
 *  C:\Users\raymo\.claude\projects\C--Users-raymo\0cbb88a8-6174-45d0-8429-f79e8d93ad8e.jsonl
 *
 * It writes several transcript variants (plain text reply, multiline reply,
 * tool-use-then-text turn, tool-only-last-turn edge case) to a temp JSONL
 * file, then invokes the Stop hook command exactly as Claude Code would
 * (JSON on stdin with transcript_path + session_id), and reports what the
 * extractor recovered vs. what we planted as ground truth.
 *
 * This is a wiring proof, NOT a claim that live Claude Code sessions behave
 * identically in every version - that still needs a real run (see README).
 */
const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const WORKDIR = __dirname;
const HOOK_CMD = `node "${path.join(WORKDIR, 'extract-last-assistant-text.js')}"`;

function msgLine({ role, content, isSidechain = false, stop_reason = 'end_turn' }) {
  return JSON.stringify({
    type: role, // real transcripts carry top-level type:"assistant"/"user" mirroring message.role
    parentUuid: 'fake-parent-' + Math.random().toString(36).slice(2),
    isSidechain,
    message: {
      model: 'claude-sonnet-5-simulated',
      id: 'msg_fake_' + Math.random().toString(36).slice(2),
      type: 'message',
      role,
      content,
      stop_reason: role === 'assistant' ? stop_reason : null,
    },
    uuid: 'fake-' + Math.random().toString(36).slice(2),
  });
}

const scenarios = [
  {
    name: 'plain-single-line-reply',
    expectedText: 'Hallo! Hoe kan ik helpen?',
    lines: [
      msgLine({ role: 'user', content: 'hallo' }),
      msgLine({ role: 'assistant', content: [{ type: 'text', text: 'Hallo! Hoe kan ik helpen?' }] }),
    ],
  },
  {
    name: 'multiline-reply',
    expectedText: 'Regel een.\n\nRegel twee met witregel ervoor.\nRegel drie.',
    lines: [
      msgLine({ role: 'user', content: 'geef een lang antwoord' }),
      msgLine({ role: 'assistant', content: [{ type: 'text', text: 'Regel een.\n\nRegel twee met witregel ervoor.\nRegel drie.' }] }),
    ],
  },
  {
    name: 'tool-use-then-final-text',
    // Mirrors real sessions: several tool-only assistant turns, then a
    // final assistant turn with just text. Extractor must skip the
    // tool-only turns and find the last one WITH a text block.
    expectedText: 'Klaar, ik heb het bestand gelezen en het antwoord is 42.',
    lines: [
      msgLine({ role: 'user', content: 'lees het bestand en zeg het antwoord' }),
      msgLine({ role: 'assistant', content: [{ type: 'tool_use', id: 'toolu_1', name: 'Read', input: { file_path: 'x.txt' } }] }),
      msgLine({ role: 'user', content: [{ type: 'tool_result', tool_use_id: 'toolu_1', content: '42' }] }),
      msgLine({ role: 'assistant', content: [{ type: 'text', text: 'Klaar, ik heb het bestand gelezen en het antwoord is 42.' }] }),
    ],
  },
  {
    name: 'ends-on-tool-only-turn-no-final-text',
    // Edge case: last assistant turn before Stop has NO text (e.g. it just
    // ran a tool and the transcript ends there). Extractor should walk
    // further back and find the PRECEDING text turn instead of failing.
    expectedText: 'Ik ga het bestand nu opslaan.',
    lines: [
      msgLine({ role: 'user', content: 'sla het op' }),
      msgLine({ role: 'assistant', content: [{ type: 'text', text: 'Ik ga het bestand nu opslaan.' }] }),
      msgLine({ role: 'assistant', content: [{ type: 'tool_use', id: 'toolu_2', name: 'Write', input: { file_path: 'y.txt' } }] }),
      msgLine({ role: 'user', content: [{ type: 'tool_result', tool_use_id: 'toolu_2', content: 'ok' }] }),
    ],
  },
  {
    name: 'noise-lines-interspersed',
    // Real transcripts have last-prompt / attachment / queue-operation /
    // system lines mixed in. Extractor must not choke on these.
    expectedText: 'Antwoord ondanks ruis.',
    lines: [
      msgLine({ role: 'user', content: 'test' }),
      msgLine({ role: 'assistant', content: [{ type: 'text', text: 'Antwoord ondanks ruis.' }] }),
      JSON.stringify({ type: 'last-prompt', lastPrompt: 'test', leafUuid: 'x', sessionId: 'sim' }),
      JSON.stringify({ type: 'system', content: 'some system note' }),
      JSON.stringify({ parentUuid: 'x', isSidechain: false, attachment: { type: 'hook_non_blocking_error', hookName: 'Stop' } }),
    ],
  },
];

const logPath = path.join(WORKDIR, 'extracted.log.jsonl');
if (fs.existsSync(logPath)) fs.unlinkSync(logPath); // fresh run

const results = [];
for (const scenario of scenarios) {
  const transcriptPath = path.join(WORKDIR, `sim-transcript-${scenario.name}.jsonl`);
  fs.writeFileSync(transcriptPath, scenario.lines.join('\n') + '\n');

  const stdinPayload = JSON.stringify({
    session_id: 'sim-' + scenario.name,
    transcript_path: transcriptPath,
    cwd: WORKDIR,
    hook_event_name: 'Stop',
  });

  try {
    execSync(HOOK_CMD, { input: stdinPayload, stdio: ['pipe', 'ignore', 'inherit'] });
  } catch (e) {
    results.push({ scenario: scenario.name, error: 'hook command threw: ' + e.message });
    continue;
  }

  // Read back the log to see what the hook extracted for this run.
  const logLines = fs.readFileSync(logPath, 'utf8').split('\n').filter(Boolean);
  const lastEntry = JSON.parse(logLines[logLines.length - 1]);

  const pass = lastEntry.text === scenario.expectedText;
  results.push({
    scenario: scenario.name,
    pass,
    expected: scenario.expectedText,
    extracted: lastEntry.text,
    error: lastEntry.error,
  });
}

console.log('=== Stop-hook JSONL extraction spike results ===\n');
for (const r of results) {
  console.log(`[${r.pass ? 'PASS' : 'FAIL'}] ${r.scenario}`);
  if (!r.pass) {
    console.log('  expected :', JSON.stringify(r.expected));
    console.log('  extracted:', JSON.stringify(r.extracted));
    if (r.error) console.log('  error    :', r.error);
  }
}
const allPass = results.every(r => r.pass);
console.log(`\nOverall: ${allPass ? 'ALL PASS' : 'SOME FAILED'}`);
process.exit(allPass ? 0 : 1);
