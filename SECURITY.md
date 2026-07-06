# Security policy

AI-Cockpit launches the Claude Code CLI under your own login and gates its tool use, so we take
security seriously.

## Reporting a vulnerability

**Please do not open a public issue for security vulnerabilities.**

Report a vulnerability privately through GitHub's
[**Report a vulnerability**](https://github.com/raymondkrahwinkel/AI-Cockpit/security/advisories/new)
(the repository's Security → Advisories tab).

Reports are acknowledged as soon as possible and you will be kept posted while a fix is worked on.
Please allow reasonable time to ship a fix before disclosing the issue publicly (coordinated
disclosure).

When reporting, please include:
- the affected component (session driver, permission gating/MCP server, profiles, TTY mode, notifications),
- steps to reproduce,
- the impact you foresee.

## Scope

Things this project considers security-sensitive:

- **Credential handling** — the cockpit must never read, store or transmit Claude credentials or
  API keys; it only checks that a profile's login file exists. Anything that leaks credential
  content is a vulnerability.
- **Permission gating** — the in-process MCP permission-prompt server and the always-allow rule
  store. A path that lets a tool call bypass an expected prompt (outside the explicitly chosen
  bypass mode) is a vulnerability.
- **Local configuration** — `cockpit.json` contents (profiles, permission rules, webhook URL) and
  how they are written.
- **Notification egress** — the Discord webhook notifier posts to a user-configured URL; content
  beyond the intended notification text leaking through it is a vulnerability.

## Supported versions

The project is pre-1.0; security fixes target `main`.
