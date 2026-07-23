# Changelog

All notable changes to AI-Cockpit are recorded here, newest first. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are the tag you release
(`v1.2.3` → `1.2.3`) — the same string the About dialog and the in-app updater read.

## How this file is kept

- **When a work item is finished**, add a bullet under `## [Unreleased]`, grouped under the matching
  heading. The wording is the same as the commit style in [CONTRIBUTING](CONTRIBUTING.md): the commit
  types map straight onto the sections here.

  | Commit type   | Section       |
  | ------------- | ------------- |
  | `added:`      | **Added**     |
  | `changed:`    | **Changed**   |
  | `fixed:`      | **Fixed**     |
  | `removed:`    | **Removed**   |
  | `refactored:` | **Changed**   |

- **On release** — pushing a `v*` tag — the CI rolls everything under `## [Unreleased]` into a dated
  version section and uses that text as the GitHub release notes. You don't edit released sections by
  hand afterwards, and you don't write the version heading yourself: the tag decides the version and
  the pipeline writes it.

- Keep entries operator-facing: describe what changed for the person running the cockpit, not the
  class that changed. **No internal tracker numbers** — a reader on GitHub cannot follow an `AC-…`, so it
  is only noise here; link a public GitHub issue instead, and only when one actually exists. The commit
  keeps the tracker reference; the changelog stays clean.

## [Unreleased]

### Added

- added: a "Stop run" button on a running Autopilot run, so you can end a run mid-flight instead of only
  intervening on a step or closing the whole workspace. A stopped run settles cleanly and is recorded in the
  history as "Stopped" — a neutral outcome, not a failure — with any unmerged work left as-is.
- added: an Autopilot run now raises a toast the moment it needs your answer, so you notice a run waiting on
  you even while you are working elsewhere in the app — before, it only showed inline on the run surface and
  was easy to miss.
- added: Autopilot — take a piece of work all the way to a merge-ready pull request. A CEO agent plans the
  run with you (from a YouTrack or GitHub issue, or a goal you type), resolves the open questions up front,
  and once you approve the plan it runs the steps autonomously — each in the run's own isolated git
  worktree, on the model you or the CEO pick for it, including free local models kept confined to that
  worktree. It reviews and security-reviews its own work behind hard gates before reporting merge-ready,
  posts progress and questions back on the source issue and moves its stage, and asks you when it hits a
  decision only you can make. You can queue several runs and see a history of what each did and why it
  passed or failed. You approve once and always do the merge yourself — Autopilot stops at merge-ready and
  never merges.
- added: the New-session dialog and the profile's MCP pre-selection now show a rough estimate of the prompt
  tokens the ticked MCP servers' tools add — a per-server figure and a live running total — so you can see a
  heavy selection heading toward a context limit before you start, instead of only hitting an error mid-turn.
  It counts the tools portion only (labelled as an estimate), is cached so it does not re-count on every tick,
  and a Refresh re-reads a server whose toolset changed. A server that can't be reached shows as unknown, with a
  hover that explains why (offline, needs a sign-in, or its plugin isn't loaded) rather than reading as a zero.
- added: when an agent delegates a task, it can now restrict that one task to a subset of the target
  profile's MCP servers — so a sub-agent runs with just the tools its job needs. It can only narrow within
  what the profile already allows, never grant more: asking for a server the profile does not have refuses
  the delegation outright. The available servers per profile are listed alongside the profiles, so the choice
  is an informed one.
- added: plugins can provide a whole workspace of their own — not just a widget in the dashboard grid,
  but the entire surface, drawn and driven by the plugin, picked from the workspace "+" menu beside
  Sessions and Dashboard. Such a workspace can embed a live session inside its own layout; and if the
  plugin that provides it is not installed, the workspace shows a placeholder and comes back intact once
  the plugin is.
- added: hover an assistant reply to copy it or have it read aloud, and a "starting…" banner appears
  while a session is still coming up — so long-running actions and a launching session both show they
  are working rather than sitting silent.
- added: a visual verify loop. An agent can run a command you register for a project (in the sidebar
  menu → Verify runners) that renders your UI, and gets it back as a text snapshot — control
  positions, colours and text — plus a screenshot for image-capable providers, so it checks its UI
  work against what actually rendered instead of guessing. Every run asks for your approval and shows
  the exact command; the agent can only trigger a command you registered, never write one.
- added: an awareness banner for unencrypted credentials. When your API keys and tokens are stored in
  the clear, a dismissible amber bar under the title bar offers to turn on encryption in one click
  (the same password flow as Options → Security). Dismissing it hides it until you add a new
  credential; turning encryption off brings it straight back. Turning it on now also scrubs the
  plaintext out of the backup and any recovery copies it leaves behind, so the credentials are not
  left readable next door.
- added: a project changelog. Every finished work item is recorded here, and each release turns the
  `[Unreleased]` section into that version's GitHub release notes, so it is clear from one release to
  the next what changed.
- added: a persistent update banner. A newer build is announced by a dismissible bar under the title
  bar — new version, current build, and an "Open release" button — instead of only a startup toast
  that auto-dismisses before the window has focus and is easy to miss. Dismissing hides it until a
  newer build is found.
- added: macOS release downloads now carry the Gatekeeper quarantine workaround in the release notes and the
  README. A downloaded `.app` is ad-hoc signed, so macOS quarantines it ("is damaged and can't be opened"); the
  fix is one command (`xattr -cr /Applications/AI-Cockpit.app`), now shown where a macOS downloader sees it
  instead of only in the packaging script's output.
- added: the Clone-from-a-Git-URL dialog now shows the folder it will clone into, pre-filled from the
  URL and editable, with a "Browse…" button to pick another location — so you can see and change where
  a repository lands before cloning. Below the field it names the default folder and where to change it.
- added: a Clone location setting (Options → Sessions) to change where repositories cloned from a URL
  are stored, alongside the existing Worktree location. Blank keeps the default under the app's config
  directory, and existing clones stay where they are.
- added: a profile can now pre-select which MCP servers a new session uses and a default working
  directory to launch it in — so a per-project profile opens with its servers already ticked and lands
  in its project folder, instead of setting both by hand every time. Both are set in Manage profiles and
  stay changeable when you start the session; left unset they keep today's behaviour (every enabled
  server, and no default folder).
- added: an option (Options → Sessions) to combine the messages you queue while the agent is working into
  a single follow-up, sent together when the turn finishes — so a few quick follow-ups reach the agent as
  one turn instead of each getting its own. Off by default, which keeps today's one-turn-per-message
  behaviour.

### Changed

- changed: when an Autopilot worker gets stuck it now consults the run's CEO first, instead of interrupting
  you directly. The CEO — which has the plan and can read the code — answers most questions itself (a
  convention to follow, a reasonable default, a design call within the plan), relayed straight back to the
  worker so the run keeps going without you. Only a decision that genuinely needs you — an irreversible
  choice, a missing credential, a business preference — is escalated to you, and better phrased. A per-step
  limit stops a weak model looping on questions.
- changed: Autopilot is more reliable and faster to plan. An approved run no longer stops mid-way to ask a
  question it could answer itself — for anything the plan did not spell out, the step agent now makes a
  reasonable assumption that follows the codebase's existing conventions and notes it, keeping the run
  autonomous rather than waiting on you. The CEO also plans quicker: it is handed only the tools it needs
  instead of every tool in the cockpit, and searches the code deliberately (a scoped read) instead of
  sweeping the whole repository, so planning uses less context and stalls less.
- changed: an Autopilot run now lets you name the folder it works in, right where you name the run — pick a
  recent or pinned folder (the same ones the New-session dialog remembers) or browse to one. A run planned
  from a YouTrack or GitHub issue no longer needs a session open on a repository to know where to work, and
  the CEO can propose the folder for you to confirm. A folder that is a git repository still isolates each
  step in its own worktree; a plain folder — an admin task with no repository — now runs in it directly
  instead of failing at the first step.
- changed: a local model whose runtime can't do tool-calling no longer just fails a tool-enabled turn. When
  the model rejects the request because its chat template can't handle tools (seen with some LM Studio GGUFs),
  the session says so plainly and retries that turn once without tools, so a plain question still gets an
  answer — with a visible note that tools were off for that turn. Turn the profile's MCP servers off to stop
  offering them at all.
- changed: the plugins that ship with the cockpit (the Claude provider and the rest) are now ordinary,
  store-updatable plugins that simply come pre-installed. They are put in place once, the first time
  they appear, and after that a newer version arrives through the plugin store like any other plugin's —
  a new app build no longer replaces or rolls back the version you are running, and a plugin you
  uninstalled stays gone instead of quietly returning on the next start. If a provider plugin ever fails
  to load after an update — for example it is waiting for you to re-approve it because its files changed —
  the session now says so and points you to the plugin manager, instead of failing with a cryptic "no
  such provider" message.
- changed: the chat transcript (SDK and local-model sessions) got an identity and look pass — each
  reply shows the model's avatar and name and your own messages a "You" label, a fresh session shows a
  model card (name, provider, connected tools) instead of a bare "Ready" line, tool steps and thinking
  read as quiet chips, and a tool's allow/deny outcome now sits inline after the command instead of on
  a line below it.
- changed: the SDK session header is calmer — the model, effort and permission-mode pickers fold behind
  one settings icon, and Stop moved down beside the message box and only appears while the assistant is
  working.
- changed: a consent request now dims the whole session and shows the Approve/Deny card centred on top,
  instead of a small banner wedged above the terminal. The old banner changed the content's height when
  it appeared and cleared, so the terminal (or transcript) visibly jumped; it was also easy to miss. As
  a full-pane overlay nothing shifts underneath and it is unmistakable that the session is waiting on
  your decision (AC-47).
- changed: the Release workflow now builds its notes from the changelog and rolls `[Unreleased]` into
  the tagged version after a successful release, instead of publishing only the auto-generated commit
  list.
- changed: the New-session folder quick-pick is easier to keep tidy — each remembered folder has a ✕ to
  forget it, a divider separates your pinned favourites from the recent folders, and the recents list is
  capped at the five most recent (favourites stay unlimited and unaffected).
- changed: when message timestamps are on, the time for your own messages and for each tool step now sits on
  the same line as the "You" label or the tool name, instead of stacked on a separate line above the row — so
  the transcript reads tighter.

### Fixed

<<<<<<< HEAD
- fixed: an Autopilot step running on a free local model (qwen-coder via Ollama) no longer hangs the whole
  run. Some local models write their tool calls as plain text instead of the structured form the runtime
  can run, so the call was never executed and the step waited forever while appearing to "succeed". Those
  text tool-calls are now recognised and run like any other; a step that still goes silent is failed after a
  hard timeout instead of hanging indefinitely; and a tool-call that slips through as text surfaces as a
  clear error rather than a stuck run.
- fixed: an Autopilot run started from a YouTrack or GitHub issue moves that issue's stage as it progresses
  again — the stage and note calls were addressed to the wrong tool endpoint and silently did nothing, so a
  tracker-triggered run stopped keeping its issue in sync. The run name now also carries the ticket key
  ("AC-191 - …") in the queue and history instead of only the bare summary, so a tracker-triggered run is
  recognisable at a glance.
- fixed: the history and Browse buttons in the Autopilot run's working-directory row now line up with the
  text box beside them instead of stretching to different heights.
- fixed: an isolated Autopilot step on Claude is now genuinely confined to its worktree. If such a step is
  set to a bypass-permissions mode — which switches off the permission guard its confinement relies on — it
  is no longer allowed to run, because it could otherwise write outside its worktree (reachable via a
  malicious issue title/description). The default remains safe, and Codex, confined by a real OS sandbox, is
  unaffected in every mode.
- fixed: voice dictation now transcribes in a separate process, so a crash in the speech engine's native
  runtime — a bad model or a GPU backend the machine can't really use — no longer takes the whole cockpit
  down. The worker restarts on its own, and a crash while loading falls back to the CPU, so dictation
  degrades instead of failing outright.
- fixed: a finished worktree whose work was already merged no longer lingers in the Managed worktrees
  panel. Its commits were counted against the point it forked from, so once the branch was merged it still
  read as "N commit(s) ahead" forever and neither "Clean up finished" nor the automatic cleanup when a
  session closes would remove it — merged, session-gone trees just piled up. A worktree is now measured
  against its base branch's current tip, so a merged one reads as clean and is swept away, while one that
  still holds unmerged commits is kept for review as before.
- fixed: a delegated task now starts with only the MCP servers its profile has selected, instead of every
  enabled server. A profile's per-server pre-selection was honoured when you opened a session from the dialog
  but ignored when the same profile ran a delegated task, so a sub-agent could reach servers you had unticked
  for it; the delegation path now applies the profile's selection too (an unset selection still means all
  enabled, and a sub-agent still never gets the orchestrator unless its profile may delegate further).
- fixed: a local model (Ollama / LM Studio) that rejects a request no longer drops the turn silently. A failed
  request — an exceeded context window, a template the server can't parse — used to make the "thinking"
  indicator simply vanish with nothing shown; the session now surfaces a red error row with the server's actual
  reason (read from the response body), a genuine interrupt still ends cleanly with no error, and a turn that
  comes back with nothing at all leaves a visible notice instead of quietly nothing.
- fixed: the terminal no longer garbles lines that mix em-dashes, arrows or emoji. Characters like `—`, `→`
  and `✅` advance wider than a monospace cell, and they used to push the rest of the line off its columns —
  so `store` could read `stuore`, a version like `0.22.0→0.22.1` collapse into `0.22.0.0.22.1`, and checks
  run together — most visibly while scrolling a unicode-heavy transcript or diff. Each cell is now painted on
  its own column, so such output stays aligned.
- fixed: a Claude SDK session started after (or alongside) a terminal (TTY) session came up with none of
  its MCP servers — cockpit-hosted and your own alike — and with no error to show for it. Two Claude
  processes share one `~/.claude.json`, and the cockpit rewrote that file non-atomically before each launch;
  a launch that landed in the split-second the file was being truncated read it as corrupt, reset it to
  defaults, and lost the session's workspace trust — which silently disables every injected MCP server. The
  cockpit now updates that file atomically, skips the write entirely when nothing needs changing, and never
  replaces an unreadable file with an empty one, so interleaving TTY and SDK sessions keep their MCP servers.
- fixed: reordering sessions by dragging them in the left sidebar no longer rearranges the panes in the
  Sessions workspace. The sidebar strip and the workspace grid now keep their own order — drag the strip to
  sort your list, drag a pane's grip to arrange the grid — so tidying one never disturbs the other.
- fixed: closing a session no longer leaves a gap in the workspace grid. When you close one of three or four
  tiled sessions, the panes that remain re-flow to the tightest layout — two left fall back to a side-by-side
  (or stacked) pair instead of sitting in a 2×2 with an empty cell.
- fixed: the per-session MCP-server checklist is now honoured by both session kinds. A terminal (TTY)
  session ignored it and loaded every configured server regardless of what you ticked, while an SDK
  session got none of your cockpit-configured servers at all. Both now start with exactly the servers
  selected for that session, and unticking the orchestrator also stops that session from delegating.
- fixed: a session opened without the New-session dialog — a workflow or shortcut that starts one on a
  profile, or a session restored on startup — now uses that profile's saved MCP-server selection instead
  of starting with none. Only the dialog carried the selection before, so these launches (Claude and
  local-model alike) came up with their MCP servers missing; each session now logs which servers it
  connected, and warns when a selection resolves to none, so a missing selection is visible rather than silent.
- fixed: an agent — whether coupled to a terminal or running as a delegated sub-agent — can no longer reach
  another session's terminal, delegated tasks, worktree, working directory, status line or sent images by
  naming that session's id; every in-process tool now acts on the verified calling session, closing a
  cross-session information-disclosure and tampering gap.
- fixed: the "agent connected" bar on a terminal now shows the session's name instead of an internal id, and
  clears when you close the session that was driving it — it used to stay stuck on after that session was gone.
- fixed: reading a terminal no longer doubles a command's first letter (showing "lls" for "ls") when the shell
  redraws its input line — the plain-text view now applies the redraw instead of concatenating both drafts.
- fixed: pasting an image into a Claude SDK chat session was rejected with "provider does not support
  image input" even though Claude accepts images — the paste now attaches and is sent to the model.
- fixed: in a chat session, text the assistant writes after running a tool now appears below that tool
  in the order it happened, instead of jumping up above the tools it just used.
- fixed: the "Thinking…" indicator above the message box no longer switches off the moment the model starts
  reasoning — it stays lit until the reply actually begins, so a session no longer briefly reads as idle while
  it is still working toward its answer.
- fixed: a long item in a bulleted or numbered list in an assistant reply no longer runs off the edge and gets
  cut off — list items now wrap onto the next line like ordinary paragraphs.

### Removed

- removed: the collapsible "Thinking…" step is no longer shown in the chat transcript. The pulsing indicator
  above the message box already shows the model is working, so a separate reasoning line in the transcript
  added little.
