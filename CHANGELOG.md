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

- added: projects — a reusable answer to what a session works on. A project holds a folder (picked or cloned), the
  profile its sessions run under, whether they are isolated in a git worktree, which MCP servers they get, and
  optional instructions for how to behave on that work, so a second codebase no longer means a second nearly
  identical profile. It can carry a logo and a memory location too, and it is managed in a window of its own.
- added: starting a session from a project — a Projects section in the sidebar whose ▶ starts one on the project's
  own defaults without a dialog, and a right-click menu for the slower routes (a pre-filled New-session dialog, the
  project's folder, its settings). Pick a project at the top of the New-session dialog to fill folder, profile,
  worktree choice and MCP selection in one go; every field stays changeable, and the dialog is unchanged for anyone
  with no projects.
- added: a Projects workspace — "What do you want to work on?" over your projects as cards, most recently worked on
  first, each showing its logo, what it is, when you last opened it and one Start button, with Open folder, Edit and
  a new-project button alongside. Above them: how many projects there are, how many you have actually worked on, and
  how many sessions are open. It is always there, as its own tab, and cannot be closed or opened twice. Built for
  someone who would rather not know what a profile or an MCP server is.
- added: one MCP-server list everywhere it appears — the profile editor, the New-session dialog and the project
  editor — collapsed by default behind a live "MCP servers · 8 of 11 selected" count, so a dozen checkboxes stop
  filling three dialogs that are about something else.
- changed: a project can no longer switch a server back on that you had turned off in the global MCP configuration —
  a project narrows what its sessions get, it never widens it.
- changed: projects are managed in a window of their own, reached from the sidebar or the overview, instead of a tab
  inside Options — a project is the work the cockpit is pointed at, not a setting of it.
- changed: the sidebar lists the five most recently worked-on projects rather than all of them, with the rest one
  click away in the overview.
- added: a memory location per project — a folder, kept apart from the source folder. A session starting on the
  project is told where it is, so it can look things up instead of being told again.
- changed: a project card offers "Finish setting up" instead of "Start" while the project names no profile. Start
  would have fallen through to the same dialog as the button beside it, which made the two look identical.
- added: a logo per project, from a file or a link — SVG included, which is what most logos are; it is stored as the
  picture it draws to. The cockpit keeps a copy of its own, so moving or renaming the original does not lose it, and
  the card shows the project's initial while it has none.
- added: standing instructions per profile — who a session is and where its memory lives — appended to whatever the
  provider's own system prompt says, with a project's instructions added under them when a session starts on one.
  Both apply; the more specific one is read last.
- changed: the cockpit-session server (which lets a session report what it is working on) is mounted into every
  session instead of being an item to tick, and is no longer offered in the MCP checklists — a status line going
  missing because a box was left unticked was a cost with nothing to weigh against it.
- added: an hourly background update re-check while the app is open, so a window left running for a workday still
  learns about a build cut hours after it opened — not just at startup. It reuses the same toast/banner and dedup as
  the startup check (a release is announced once, a dismissed build stays quiet), is gated by the same "check on
  startup" setting, and never surfaces an error toast for a background poll that could not reach GitHub.
- added: Autopilot templates — reusable goal/brief starting points for a run. Manage them in the Autopilot settings
  (a Templates section: create your own, edit any, delete your own, and reset a built-in or plugin one to its default),
  with placeholder help for the tokens you can use ({{issue.title}}, {{issue.url}}, {{input.…}} and more). When you
  start a run you pick a template or plan free; a chosen template's text — with its placeholders filled from the
  triggering issue — becomes the CEO's kickoff. The YouTrack and GitHub Issues plugins ship "Bug fix" and "Feature"
  templates out of the box.
- added: a startup banner and a persistent badge on the "Plugin store" button for plugins sitting at
  awaiting-approval — new, or their bytes changed since you last approved them — so that state is visible from
  the main window instead of only as a row in Plugin store → Installed. Both clear once every such plugin is
  approved or disabled; the banner can also be dismissed on its own.
- added: a persistent "Needs you" badge on the Autopilot bar while any run is waiting for your answer, so you
  notice a waiting run even when you are looking at another run or the history — not just the moment's toast.
  It clears once you answer.
- added: a "CEO is working…" cue in the Plan-with-the-CEO dialog while the CEO is planning, so a long
  planning turn no longer looks like the dialog is stuck — shown only on the CEO session, the rest of the
  app's sessions are unaffected. It is a bar across the top of the chat, the same accent bar the run shows
  when work returns to the CEO for validation.
- added: Autopilot takes a code run all the way to a merge-ready pull request — it commits the run's work on
  its branch, pushes it, and opens the PR for you (you still do the merge). When it cannot — a plain folder,
  no git remote, or no GitHub CLI — it says so up front and leaves the work on its branch to publish by hand.
- added: an Epic template for a YouTrack epic — it reads the epic's child issues (its "parent for" links) and
  plans them as one coherent run that lands as a single pull request naming every issue it closes.
- added: extended thinking is shown again at the Developer reading level — a dimmed, collapsible "Thinking"
  section that streams the model's reasoning as it comes, and stays hidden at Focus and Simple so those levels
  keep calm.
- changed: an autonomous Autopilot run no longer stops for permission prompts it has no one to answer — its own
  control tools are pre-authorized, and a run isolated in a throwaway worktree runs its work tools (edits, shell,
  git) without prompting, with the worktree as the boundary. A step that is slow because it is working hard is no
  longer mistaken for a stuck one and failed: the stall timer only trips when a step makes no tool progress at all.
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
- added: SDK chat sessions now have a reading level — Developer, Focus or Simple — so one session can be read
  by a developer or handed to a non-technical viewer without changing what the agent does. Developer shows
  everything; Focus stays complete but calm (runs of auto-executed tool calls fold into one "N steps run" line
  you can expand, and the running cost moves onto the usage pill instead of a "$" figure); Simple drops the tool
  noise, the cost and the model chip and puts jargon in plain words. Tool calls that asked for your approval —
  waiting, or already allowed or denied — stay visible at every level, in human language at Simple ("✓ Changed a
  file — you approved this"). Pick the default per profile ("Default view"), override it when starting a session,
  or switch it live from the session header. Terminal (TTY) sessions are a raw terminal and have no reading level.
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

- changed: an Autopilot run started from a YouTrack or GitHub issue now moves that issue's stage itself as it
  progresses — to an in-progress stage when it starts, and a review stage when it reaches merge-ready —
  instead of relying on the CEO to move it by hand (which it did not always do, so a run could sit on the
  backlog while it worked). A blocked or stopped run is left where it is, and the final merge stage still
  stays yours. Each tracker maps these to its own stage names.
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

- fixed: a finished session's worktree is cleaned up again once its work has landed. The panel judged this by
  walking history — which a squash merge rewrites — so a worktree whose pull request had been squashed stayed
  behind forever, "Clean up finished" could never sweep it, and every finished session left another one on the
  pile. It now asks whether removing the folder could actually lose anything: work that is in the base branch,
  or pushed to a remote, or already in the base under a rewritten commit, is safe to remove. The pill says what
  is genuinely left, "N commit(s) only here", instead of counting commits that live somewhere else too. It also
  no longer measures against a base branch that has not been pulled since the merge landed. A released
  worktree's branch is deleted only when its work is in the base branch itself — a branch that is safe merely
  because it was pushed is kept, since a remote can be force-pushed or its branch deleted.
- fixed: an Autopilot run whose autonomy mode was left on a permission-bypassing setting no longer has its
  Claude steps refused for "the profile does not confine to the worktree". The run now coerces that setting
  back to the safe "acceptEdits" mode for every step — the implementation step and both review gates alike —
  so a run started from an older saved setting proceeds instead of blocking on its first Claude step. The
  refusal message, when it does appear, now names the fix: switch the autonomy mode to "acceptEdits", or route
  steps that need autonomous shell to a Codex profile.
- fixed: the CEO can no longer plan an Autopilot step on a model the chosen profile cannot run — a model that
  is not one the profile offers, or any model on a local profile that pins its own. The plan is turned down at
  emit with a clear message so the CEO corrects it before you approve, and a mismatched step is caught again
  just before it runs instead of failing later with a misleading isolation error.
- fixed: the Autopilot run queue no longer stops starting queued runs after one fails to start — a run that
  errored while starting used to permanently consume a concurrency slot.
- fixed: answering an Autopilot run's blockade with an empty reply, or a step that reports an empty summary,
  no longer leaves the run stalled or shows the CEO a blank block.
- fixed: the three internal Autopilot endpoints (autopilot-plan, autopilot-run, autopilot-ceo) no longer
  appear in the New-session MCP checklist or a profile's MCP pre-selection. They are the cockpit's own
  endpoints that only an Autopilot run's own agents use, so an ordinary session should never see or tick
  them — a run still mounts them internally.
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
