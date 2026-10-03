// AC-1464: loaded by deploy/smoke.sh into the codex launcher through NODE_OPTIONS, so it runs inside the very process
// a wrapper (or a control) started. Prints one PROBE line: who it runs as, which env reached it, what it can read, and
// whether it can commit in the worktree SMOKE_WORKTREE names. Exits before codex itself runs.
const fs = require('fs');
const { execFileSync } = require('child_process');

const can = path => { try { fs.readFileSync(path); return 'read'; } catch (e) { return e.code; } };
const cmdline = pid => { try { return fs.readFileSync(`/proc/${pid}/cmdline`, 'utf8'); } catch { return ''; } };
const server = fs.readdirSync('/proc').find(pid => /^\d+$/.test(pid) && cmdline(pid).startsWith('/app/Cockpit.Server'));

const out = {
  uid: process.getuid(),
  pane: process.env.COCKPIT_PANE_ID ?? null,
  home: process.env.HOME,
  config: can('/state/cockpit.json'),
  certificate: can('/state/node-certificate.pfx'),
  secret: can('/run/cockpit/cockpit_connect_key'),
  original: can('/run/secrets/cockpit_connect_key'),
  environ: server ? can(`/proc/${server}/environ`) : 'no-server',
};

const tree = process.env.SMOKE_WORKTREE;
if (tree) {
  try {
    fs.writeFileSync(`${tree}/agent.txt`, 'agent\n');
    execFileSync('git', ['-C', tree, '-c', 'user.name=agent', '-c', 'user.email=agent@smoke', 'commit', '-q', '-m', 'agent', '--', 'agent.txt'], { stdio: 'ignore' });
    out.worktree = 'committed';
  } catch (e) {
    out.worktree = e.code ?? `exit ${e.status}`;
  }
}

console.log(`PROBE ${JSON.stringify(out)}`);
process.exit(0);
