# Changelog

All notable changes to Wispslate Cockpit are recorded here, newest first. The format follows
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

- added: a diagram now opens in its own window beside the cockpit — drag it, resize it, park it on a second screen —
  hooked up to the session you are already talking to instead of starting a fresh one of its own. One window per
  diagram: opening the same one again brings that window forward, and two diagrams from one session are two windows
  side by side. The Mermaid source behind the render is always one click away, collapsed under it behind a "toon
  bron" toggle rather than hidden entirely.

- added: a diagram window says which session it is working with, and keeps standing when that session ends — it
  tells you the session is gone and offers the open sessions to pick a new one from, rather than closing on you or
  quietly staying attached to something that is no longer there. The same way back out after you disconnect.

- added: an agent can now ask to read or edit a diagram you have open, gated behind Options → Security (off by
  default) and a separate Approve/Deny for reading and for editing — reading never quietly comes with editing.
  Reading always hands over the diagram exactly as it stands, including anything already in it, and an editing
  request shows how many lines change before you approve it, never a description the agent wrote itself. The panel
  shows which agent is connected and which of the two it holds; a diagram it is merely connected to, with neither
  granted yet, shows that too rather than looking untouched.

- added: a "Nieuw diagram" quick action next to Depot servers and Docker on the workspace tab strip — one screen
  asks for a name (prefilled, Enter is enough) and, optionally, lets you couple the session you already have open.
  Coupling here is not access: the session still has to ask for read or edit separately, the same as connecting
  any other way.

- added: the sessions running on a Cockpit you are paired with now show up under Options → Security, one card per
  node, and you can start and stop them from there — within the profiles and projects that machine's operator has
  ticked for you, and never outside them. They are kept in their own card, headed by the node's name, rather than
  mixed into your own session list, so a Stop can only ever land on the machine you are looking at. The assistant
  can do the same through its own separate tools, so it never confuses a session here with one over there.
  **What you start on a node keeps running there**: closing your Cockpit, losing the network or unpairing does not
  stop it — this is offloading work to another machine, not remote-controlling it.

- added: a paired Cockpit now starts able to use none of your profiles or projects — Options → Security shows a
  checklist under the pairing status where you tick exactly which ones it may reach. Unticking one takes effect
  immediately, without unpairing.

- added: a "Discover nodes on this network" button in Options → Security finds other Cockpits with the node
  switch on and lets you pair with one by picking it from the list instead of typing its address — same pairing
  handshake either way. A node is only found on its own local network by default; seeing it from further away
  needs an explicit CIDR whitelist entered on that node's Security tab, which also gates who may send it a
  pairing request at all.

- added: two Cockpits on the same network can now pair with each other instead of you copying an address and a key
  by hand. On the machine you want to reach, Options → Security shows a pairing address; type it on the other
  machine and both screens show the same six digits. Confirm on both sides and the pairing goes live — the
  controlling Cockpit adds that machine's MCP endpoints for you and remembers its certificate, so nothing else can
  take its place at that address later. Either side can unpair, which immediately invalidates the key. A Cockpit
  that is already paired refuses a second one and says who it is paired with.

- added: a Cockpit instance can now optionally accept MCP connections from a second Cockpit on the same network,
  over TLS with a shared key — off by default, enable it under Options → Security. Turning it on takes effect the
  next time Cockpit starts.

- added: opencode as a session provider plugin, driven over the Agent Client Protocol via `opencode acp` — a
  second real agent alongside Claude, not just a chat window: real tool calls, permission prompts routed
  through the same consent card every session uses, and live usage/cost figures. Requires the opencode CLI
  installed (opencode.ai/docs), authenticated for most models.

- added: Grok (xAI) as a selectable session provider plugin — configure an xAI API key and a model id (e.g.
  `grok-4.6`) per profile in Manage profiles. Chat-only: no tool calls, file access or permission prompts, and no
  context/usage pill (Grok's endpoint reports neither).

- added: the first-run wizard's work-kind step now recommends plugins by the store index's new `audience` field
  instead of a placeholder — picking "Development" pre-ticks the plugins tagged for it plus every generic
  (untagged) plugin, and the AI-provider plugins no longer appear here since they are already chosen a step
  earlier.

- added: OpenRouter as a selectable session provider plugin — configure an OpenRouter API key and a
  vendor/model id (e.g. `anthropic/claude-sonnet-4.5`) per profile in Manage profiles. Chat-only: no tool
  calls, file access or permission prompts, and no context/usage pill (OpenRouter's endpoint reports neither).

- added: the context-usage warning threshold can now be set separately for the voice assistant, in Options next to
  the per-provider thresholds — a session running under the same profile the assistant uses keeps warning at the
  old number, so lowering the assistant's own threshold no longer changes anything for ordinary sessions.

- added: the voice assistant can now add a project your team shares to this machine, the same step the "Add to my
  projects…" card does on the Projects page — you tell it which folder here holds the project and which profile its
  sessions run under, and the name, behaviour, MCP choice and memory come with the project as they always did. It
  asks first, on a card that shows the folder, and it will not clone: the folder has to be there already. A project
  you have added once is not added a second time.

- added: the voice assistant can now create a brand-new local project itself, the same step "New project" on the
  Projects page does — a name, and optionally the folder its sessions run in, the profile, a behaviour prompt, which
  MCP servers it sees and whether it isolates in a worktree by default. It asks first, on a card that shows those
  choices, and it checks whether your team already shares a project under that name before adding a duplicate.

- added: the Depot plugin now contributes a "Depot settings" quick-action button to the global toolbar strip,
  alongside Docker's and Kubernetes's — it opens the same settings view the project editor's "Servers…" button does,
  reachable now from every workspace instead of only from a project's memory-source picker.

- added: the quick-action buttons plugins contribute now sit on the workspace tab strip, so they are reachable from
  every screen — the Projects page and a dashboard included, and from a Sessions workspace before it has a session in
  it. They used to appear only above an open session grid, which is what made a plugin's own settings unreachable
  from the screen you land on. A cockpit without plugins shows no strip at all and loses no height to it, and an
  action that fails now says so in the plugin diagnostics instead of doing nothing.

- added: the Projects page can be shown as cards or as a wide list — pick it on the page itself, and it is remembered
  for next time. The list fits about twice as many projects on a screen; the cards keep the bigger logo you find a
  project by.

- added: projects your team shares that you have not added yet now appear under "From your team" on the Projects page
  in either layout, marked "Ready to add", with the same wording and the same card the Manage-projects window shows.

- added: typing `@` in a session's prompt box, or the assistant's, now opens a fuzzy file-/folder-picker over that
  session's working directory — arrow keys to move, Tab or Enter to insert, Esc to close. The inserted path is
  always relative and forward-slash-separated, with a trailing slash on a folder. Nothing is scanned until the
  first `@`, and a session (or the assistant, before it has started one) without a known working directory yet
  simply leaves the picker closed rather than guessing.

- added: your own messages in a session's transcript now have a copy button too, matching the one that was already
  there under an assistant's reply — hover or focus the message to see it.

- added: a new "Log diagnostic snapshots" option under Debug settings writes one line to the log every ten
  seconds — memory, GC, handles, threads — so a slow leak or crash leaves a trail to look back on. Off by
  default. Separately, and always on regardless of that setting: if the app's UI freezes for more than a few
  seconds, that now gets logged too, along with a line once it recovers and how long it was stuck — previously a
  freeze left no trace at all.

- added: Local CI's settings page now has a "run without asking every time" option, off by default. On, a session's
  `run_local_checks` runs straight away instead of stopping for your approval on every single run — it still runs
  whatever the project's workflow says, in a container with this machine's Docker, so that part stays what it always
  was.

- added: when an agent stops to ask you something, the question and the answers it offers now appear as a card in
  the transcript with the choices as buttons — pick one (or several, where it allows that) and press Send, or type
  your own answer under "Other". Before, the same question arrived as a generic Allow/Deny prompt with the options
  buried in raw JSON, and allowing it approved the question without ever answering it.

- added: Autopilot's CEO can now validate each finished step on a different profile/model than it planned with —
  planning stays the strong reasoning model, validation (the part of a run that grows fastest and runs most often)
  can move to a cheaper one. Blank keeps today's behaviour: validation follows planning until you set it separately
  in the Autopilot settings.

- added: every session — including the voice assistant — now gets an active turn as soon as mail addressed to it is
  waiting, instead of only picking it up on its own next turn or tool call. On by default for everyone, and it costs
  nothing when nothing is waiting: a message no longer has to be marked urgent, and the recipient no longer has to
  have opted in to being woken, for it to be delivered promptly. The voice assistant in particular can now be reached
  this way at all — a spawned session that finishes, gets stuck or has something to report gives the assistant a
  turn on its own, without the operator having to ask for a status update to get it a turn.

- added: the voice assistant can start a session at settings the profile does not carry — "that profile, but at low
  effort" — instead of only ever getting the profile's own. Only the options it names change; everything else stays
  exactly what the profile was configured with. An option the profile's provider does not understand is refused with a
  reason rather than quietly passed to a command line that has no such flag, and so is a value that provider does not
  take. What a session is allowed to do to the machine is never part of this: the permission mode, and Codex's sandbox,
  stay whatever the profile says and are refused outright when asked for. A session that needs to run with different
  access runs under a profile that says so.

- added: the voice assistant is told when a session it started finishes, gets stuck or falls over, instead of asking
  again and again whether it has. It can now put a watch on a session and be messaged when that session stops
  working, when it is stopped on a permission nobody has clicked, when its pane disappears without ever having said
  anything, when it has written nothing for a while, or when a line matching something it asked about shows up. Each
  message carries the last few lines the session wrote, so "it stopped" arrives already saying whether that means
  finished or waiting on an answer. Nothing is watched unless the assistant asks for it, and a watch on a session
  that is quietly working costs nothing.

- added: a session provider now states which options it understands and what values they take — Claude its permission
  mode, model and effort levels, Codex its own sandbox modes. Until now only the New-session dialog knew Claude's
  vocabulary and nothing knew Codex's, so anything else asking "what can I set for this provider?" had to guess. A
  provider that reads no options states none, instead of being handed another provider's.

- added: the voice assistant can see what a profile is actually set to run at before it starts one — its permission
  mode, model and effort for a Claude profile, its sandbox for a Codex one, each with a readable label and a note of
  whether the profile chose it or the provider's default applies. It also names the provider a profile really runs on
  ("Claude", "Codex") instead of calling every plugin-backed profile "Plugin". Until now the only way to find out that
  a profile ran with permissions bypassed on the costliest model was to open `cockpit.json` yourself, which is not
  something the assistant can do at all.

- added: a pull request that has gone green and is still sitting there gets mentioned, once. The cockpit already told
  you when a branch went red; it stayed quiet about the opposite case, so a branch that passed everything and had
  nothing blocking it could sit unmerged all afternoon because whoever was watching it moved on. It says so only when
  the checks are all in, nothing is pending, no changes are requested and the merge itself is clear — and it says it
  once, not every five minutes until you get to it.
- added: a claim left behind by an agent session that crashed is cleaned up on its own. Agents mark the worktree or
  branch they are working on so the others keep off it, and that mark is dropped when a session closes normally.
  A session that never closed normally — a crash, a killed process — used to leave its mark standing, warning
  everybody off work nobody was doing. The cockpit now notices the owner is gone, drops the mark and tells the
  assistant it did, rather than quietly. A mark whose owner is still running is never touched, however old it is.

- added: the voice assistant can rename a session and a desk. It could already name a session at the moment it
  started one, but not afterwards — so "call that one Luna" meant leaving the conversation and doing it yourself in
  the sidebar. Both renames ask for your approval first, the same as starting or stopping a session does, and a name
  the assistant sets counts as one you chose: nothing later relabels it behind your back.
- added: the voice assistant remembers things you tell it. Say "remember that I'm called Raymond" or "prod means the
  release desk" and it writes that down, so the next conversation starts knowing it instead of starting from nothing.
  What it kept lives in a plain text file next to your settings — open it any time to read it, correct it, or throw
  a line out.
- added: the assistant hands itself over and starts again before its conversation gets too long to send. It runs all
  day and never used to let go of anything, so the exchange grew until the model would eventually refuse a question
  mid-sentence. It now keeps a short note of where things stand and, once its context is nearly full, picks that note
  up in a fresh conversation. You keep talking; it stops carrying the whole morning around.
- added: you hear that the assistant is doing something. It used to go quiet the moment it went looking — nothing
  between your question and the answer, which is indistinguishable from not having been heard. Now it says it is
  going to have a look, and on a long one it says it is still at it, less often as the wait goes on.
- added: the speech models load the moment you press the key, not when you have finished talking. Both the one that
  hears you and the one that answers, and for dictation into a session as well as for the assistant. Pressing the
  key is the promise that a transcription is coming, and the sentence you speak into it used to be time spent
  waiting afterwards.
- added: the voice assistant can hand a worktree it made for itself over to a running session, so that session owns
  it from then on and it is cleaned up when that session closes — for a worktree it made ahead of starting a
  session in it, or for handing one to a session that is already running.

- added: the CLIs the cockpit manages for you (Claude, Codex) now keep themselves up to date — the background check
  installs a newer version instead of only telling you one exists, and says afterwards what it moved. Each CLI has
  its own "Update automatically" tick-box in its managed-CLI block, on by default. A finished install now also
  leaves only the newest version behind rather than stacking old ones up on disk, the manual Update button
  included.

- added: the voice assistant can now start a session against a known project by its id, instead of relying only on
  the folder it happens to run in — the project's own working directory, default profile, worktree isolation,
  behaviour prompt, memory/resources and MCP selection are all applied the same way starting from the project's
  folder already did. Which profile actually ran is reported back, so a start that used the project's default is
  never a silent guess.

- added: the "+N image" label on a message you pasted images into is now clickable, opening a small preview
  window with previous/next navigation and a fit-to-window/actual-size toggle — the images used to be gone from
  view the moment you sent the message. Works the same in a session and in the assistant chat. The images stay
  available for as long as the cockpit keeps running; after a restart the label is shown but no longer opens
  anything, rather than failing.

- added: a "Compact" button next to Dismiss on the context-window warning, for a provider that supports it — click
  it to ask the provider to summarise the conversation and carry on, instead of waiting for it to happen on its own
  once the window is nearly full. Disabled while the session is already working, and it disappears again on its own
  once the summarise brings the window back under the warning line.

- added: the voice assistant can now see the projects your team shares that this machine has not added yet — the
  same list the Projects page shows under "From your team" — so it can point you at binding one instead of
  creating a duplicate when you ask it to set a project up. One connection being unreachable or signed out never
  hides what the others have to offer.

### Changed

- changed: the whiteboard now comes with the diagram plugin instead of being a plugin of its own — one install,
  one update, and both surfaces stay exactly what they were: a diagram is still Mermaid text an agent may be given
  permission to edit, a whiteboard is still a drawing an agent can only ever be shown. Whiteboards you already have
  open keep working. If you had both plugins installed, the cockpit offers once, after the update, to remove the
  old whiteboard plugin; keep it and nothing is taken away.

- changed: a second Cockpit reaching this one over the network now counts as one remote caller rather than as
  nothing in particular. It is never treated as one of your sessions: it cannot set a session's statusline, read an
  agent's mail, answer a permission prompt or reuse one you granted a session on this machine, and the assistant's own servers
  are no longer offered on the network at all. A connection that is turned away now says so with a reason, so the
  other end can tell "refused" from "no answer" instead of guessing.

- changed: the node cards under Options → Security now update on their own every 20 seconds, so a paired machine
  dropping out mid-task — or coming back — shows up without pressing Refresh. The message also says more than
  "could not reach it": a node that refuses the connection outright reads as looking stopped, one that answers
  with a certificate you never pinned is flagged as untrusted rather than treated as merely offline, and one that
  simply never responds says so as a possible timeout instead of guessing which of the three it was.

- changed: the Codex provider plugin is called "Codex (ChatGPT)" in the store instead of "CLI Agent Provider
  (Codex)" — it is now findable under the name of the thing you are installing rather than under how it happens to
  be built. Nothing else moves: an installed copy keeps working and updates as usual, and sessions still start the
  same `codex` command line tool.

- changed: a project card is quieter. One button carries on with the project, sharing stays visible beside it, and
  everything else — open folder, edit, start with something changed, the project's own links — moved under a single
  `⋯`. A project that still needs an assistant picked now says "Pick how it runs" in an ordinary button instead of
  announcing itself in the same loud blue as Start, which read as something being broken.

- changed: plainer wording on the Projects page and in the Manage-projects window. "Shared via …" is now "From your
  team", "Not set up yet" is "Ready to add", and a shared project you have not added yet offers "Add to my
  projects…" rather than the "Finish setting up…" a half-configured local project also used. The page no longer
  claims everything is already set up while a card underneath says otherwise.

- changed: the Manage-projects window and the Projects page now draw a project the same way, from one shared
  building block — so wording and layout stay in step instead of drifting apart between the two screens.

- changed: the Assistant Profile's instruction box adds to the assistant's own instructions rather than replacing
  them. Typing "your name is Zyra" used to silently switch off everything else it knows about talking to you — that
  it answers in your language, that it is being listened to rather than read, that it never treats a spoken "yes" as
  permission. If you do want to write its instructions from scratch, there is now an advanced tick-box for it that
  spells out what goes.
- changed: Autopilot's per-step opening brief — resent in full to every fresh step session — dropped a sentence
  repeated verbatim between its execution mandate and its closing instruction, and two filler transitions, without
  touching the guidance that keeps lighter models from analysing a step instead of building it.

### Removed

- removed: the counts across the top of the Projects page (projects / worked on / sessions open). They sat in front
  of the projects you came for and answered no question that screen raises.

### Fixed

- fixed: closing a session left the "agent connected" bar standing on any diagram or whiteboard it was working
  with, for an agent that no longer existed. Worse, that stale connection held the surface: no other session could
  be connected to it, and there was no agent left to disconnect. Closing a session now releases what it held on a
  diagram or a whiteboard, the way it already did for a terminal pane.
- fixed: on Linux, reopening the cockpit maximized restored the maximized flag but not the actual window state —
  it looked like a normal window and the maximize button needed two clicks before it would fill the screen. The
  window now comes up genuinely maximized from the start.
- fixed: an image preview could not be dragged around once zoomed in, whether by scrolling with Ctrl held or by
  switching to 1:1 — zooming grew the picture but the window never noticed it had grown, so there was nothing to
  pan to. Dragging now moves the picture under the cursor, stops at its edges, and the pointer shows a hand while
  it's possible and a fist while you're actually dragging. Fit-to-window is unaffected: at its normal size it
  still shows no scrollbars and can't be dragged.
- fixed: local CI kept saying Docker or act was missing after you had just installed it, until you reopened the
  local-CI settings screen or restarted the cockpit. It now checks again the next time it needs to know, without
  needing either — a working answer is still remembered so nothing gets re-checked once it's found.
- fixed: scrolling a session at the Focus reading level was jerky and used far more memory than the same session at
  Developer — the tool steps a fold collapses were still being built as rows, invisibly, so the transcript kept
  around nine hidden rows in memory for every one you can see. They are no longer built at all: measured over 600
  rows, that is 8 live rows instead of 71, 30 MB instead of 121 MB, and a scroll that no longer drifts under the
  scrollbar. Nothing changes on screen; expanding a run still brings its steps back exactly where they were.
- fixed: launching a new TTY session, and closing the main window, could freeze the whole app for a few seconds —
  both were doing blocking work (an MCP token renewal, a settings write) directly on the UI thread. Both now run
  off it, so neither can stall the interface anymore.
- fixed: a closed session pane's memory (its whole rendered view, not just its data) could be kept alive forever —
  the pane stayed subscribed to its own session for events it never dropped on close. Closing a session now lets
  it, and everything it drew, actually be freed.
- fixed: a signed-in MCP server no longer refuses calls for a couple of minutes near the end of every access
  token's life. The cockpit renews a token while it still has a margin left, but the renewal itself only ran
  once the token was dead to the second — so in the window between the two, an ordinary tool call came back as
  "the cockpit could not renew its sign-in", twice in a row, and then worked again minutes later once the token
  had properly expired. Sessions writing results to a server like Depot could lose that write to it.
- fixed: a signed-in MCP server now rides out a token endpoint that is briefly unavailable. The cockpit starts
  renewing ten minutes before a token runs out and keeps serving calls from the one it holds while it tries, so a
  server restarting or having a slow minute no longer reaches an agent at all — only an outage lasting the whole
  ten minutes does. A session start is deliberately stricter: it holds its credential for hours, so it still
  refuses one that will not last, rather than starting and losing the server later on.
- fixed: a server that hands out short-lived access tokens is no longer renewed on every single call. Renewing
  early only makes sense while the server's own tokens outlive the head start; where they do not, the cockpit
  falls back to renewing just before a call needs it. The head start returns on its own if that server starts
  issuing longer tokens.
- fixed: the assistant, a project-less session and the profile checklists can now be offered a project-bound
  server (a Depot connection, say) at all — previously it was only ever offered to sessions on the specific
  project it was bound to, so the assistant (which has no project) could never see or mount it, no matter how
  it was configured.
- fixed: a project with a Depot connection (or any other project-bound server) now shows it as a row in the
  project editor's server checklist, ticked by default — it used to be invisible there no matter how the
  project was configured, so there was no way to see it, let alone turn it off for that project. Unticking it
  and saving now sticks: reopening the project, or starting a session on it, keeps it off, while a project that
  is never resaved keeps mounting it exactly as before.

- fixed: once memory usage grew past a safety ceiling meant to skip a background compact under extreme load, the
  compactor stopped ever compacting again and repeated the same warning in the log several times a second,
  indefinitely. It now retries a compact periodically instead of giving up for good, and logs a single clear
  error — rather than an endless stream of warnings — if usage stays elevated.

- fixed: clicking the assistant button again while its chat window was already open, but minimized or sitting
  behind other windows, no longer left it there — it now comes to the front.

- fixed: the assistant no longer announces that an approval is waiting on your screen after that approval has
  already been handled, including when approvals are turned off altogether.

- fixed: the assistant no longer tells you a click is coming for starting/stopping a session, sending a message
  or a prompt into one, or exporting/importing its own memory, when you have switched that particular asking off.
  Each of those calls now also reports, after the fact, whether it actually asked, went through because you had
  switched it off, or went through because you had told it to remember your answer.

- fixed: a shared project's logo can now be changed by anyone with Editor access or better, instead of always
  showing as read-only. Picking a new logo (or removing it) now saves it to Depot too, so everyone bound to that
  project sees the same picture — a logo already in Depot also shows up automatically the moment you bind to that
  project, and sharing a project for the first time now takes its logo along.

- fixed: a project you shared to Depot now shows its "Shared" badge and "Stop sharing…" button right away on
  restart, instead of looking unshared until you open Manage projects. The badge now remembers the last known
  publish, so a slow, unreachable, or not-yet-signed-in connection no longer makes a genuinely shared project look
  local. Sharing the same project a second time no longer leaves a stale connection behind that made "Stop sharing"
  silently do nothing.

- fixed: the session header's usage indicator (context/5-hour/weekly) no longer drops a figure it already knew.
  A session that received an incomplete usage reading — before its first turn, right after a compaction, or
  simply because the reply arrived a beat late — used to have that missing figure blanked out instead of kept;
  it now keeps showing the last known value until a fresh one replaces it. The two requests behind the figures
  also now go out together instead of one after the other, and a session sitting idle gets a light 30-second
  catch-up so a late reply is not stuck showing nothing until the next turn.

- fixed: starting a terminal-route session with an opening message no longer sometimes leaves that message sitting
  typed but unsent, especially on a profile whose startup hooks take a few seconds. The message used to be handed
  to the terminal the instant its process existed, before the hosted CLI was actually reading input; now it waits
  until the CLI itself signals it is ready (falling back to a short fixed wait for one that never does), so the
  message always arrives as a submitted turn instead of text left in the composer.

- fixed: typing in the cockpit — the assistant's chat box most visibly — no longer stutters. The memory-reclaiming
  compact added recently decided purely on how large the managed heap was, so once a cockpit's ordinary working
  set stayed above that size it compacted again on every check: measured on a live instance, 133 pauses a minute of
  roughly a quarter-second each, freeing under a megabyte, which left the interface frozen more than half the time.
  A compact now waits until the heap has actually grown past what the previous one settled at, so it still catches
  a heap growing fast while it is cheap to compact, and stops repeating itself over memory that is genuinely in use.

- fixed: typing a file mention is no longer noticeably slower than typing anything else. Every keystroke inside an
  `@…` ranked the working directory twice over, and then threw away and rebuilt every row of the open suggestion
  list — measured at roughly 8.7 ms a keystroke against 1 ms for ordinary typing. It now ranks once and rewrites
  the list in place, leaving a row that has not changed alone: about 3.3 ms a keystroke on the same measurement.

- fixed: windows on macOS can be resized again. A recent change removed the border every window used to have and
  replaced the resizing it carried with the cockpit's own — which macOS supports in neither half, so every window
  there was stuck at the size it opened at. macOS keeps the platform's own resize border again; Windows and Linux
  are unaffected and keep the borderless look.

- fixed: the diagnostics report under Options → Debug scrolls sideways, so lines longer than the dialog is wide —
  paths, OS build strings — can be read instead of being cut off.

- fixed: the cockpit no longer lets resident memory climb into the gigabytes and stay there. On a workstation with
  plenty of free RAM, .NET only hands freed memory back to the operating system when it detects real memory
  pressure — which rarely happens here — so memory the garbage collector had already marked as dead kept being
  held onto (measured: resident memory reaching 10.3 GB, with only 3.5 GB of it actually live; separately measured
  climbing to 6.5 GB within about three hours of normal use). The cockpit now asks the runtime to conserve memory
  more aggressively, and checks its own memory use roughly five times a second — a check cheap enough not to
  matter — reclaiming memory the moment there is enough of it worth reclaiming, before it ever grows large enough
  for that to cause a noticeable pause. An earlier version of this checked far less often and could pause the whole
  app for minutes on a heap that had been allowed to grow unchecked; catching it early avoids that entirely.
- fixed: the "While you're talking" heading on the Assistant voice settings page no longer shows with nothing
  underneath it. Its rows were tied to push-to-talk dictation being on, a toggle that lives on the Transcribe page,
  so an operator with the assistant enabled but dictation off saw an empty heading; the heading and its rows now
  show and hide together, correctly gated on the assistant being enabled.
- fixed: a prompt over 62 characters — sent by the operator, a voice transcript, a scheduled resume, or a spawned
  session's opening message — could type into the running `claude` session but never submit, leaving it sitting in
  the composer looking sent. The CLI treats any stdin chunk of 64 bytes or more as a paste, so the Enter riding
  along inside that same write landed as a literal newline instead of registering as a key. The text now goes
  through the terminal's own paste mechanism (the same one already used for pasting a screenshot's path), with
  Enter written on its own right after.
- fixed: making a backup (or restoring one) no longer freezes the cockpit for as long as the archive takes to
  build or unpack. The window stays responsive — draggable, other tabs and sessions usable — while a backup with a
  realistic amount of stored data runs in the background.
- fixed: the "Share…" button for a project that was never actually published no longer opens the "Stop sharing?"
  confirmation instead of the publish flow. A project whose memory connection happens to start with the same prefix
  as a shared source (without ever having gone through Share) now correctly shows "Share…" and opens that flow when
  clicked, matching what the projects list already showed for it.
- fixed: the floating voice pill no longer shows "Reading aloud"/"Preparing" while the assistant reads out its own
  reply. That state already appeared on the assistant's own indicator chip, so the pill said the same thing twice —
  it now stays hidden for the assistant specifically, while read-aloud started from an ordinary session (F9 or the
  read-aloud button) still shows on the pill exactly as before.
- fixed: a link in your own chat message is now clickable, opening in your default browser like a link in the
  assistant's reply already did. It used to just sit there as plain text — your own bubble never had link detection
  at all, only the assistant's replies did.
- fixed: "Install on next start" now really waits for your next start. It used to launch the updater there and then,
  which gave your running cockpit sixty seconds before the updater stopped waiting and killed it — no warning, no
  message, the window simply gone, and on Windows sometimes no executable left for the Start-menu or taskbar shortcut
  to point at. The download now sits untouched until you next open the cockpit, which applies it before the window
  comes up. If the request cannot be saved you are told so instead of being promised an update that will not happen.
- fixed: the auth-expiry bar no longer flashes back after you've just signed in again through it. Signing in fed the
  bar's own status straight away, but the login gate behind it kept answering "logged out" from a reading taken
  before the sign-in, so the bar reappeared for a moment on the next check before correcting itself. A successful
  sign-in now updates that same reading immediately, so the bar stays gone.
- fixed: a proactive sign-in ("your login is about to expire") no longer loses the "open this link" button before you
  get a chance to click it. The button disappeared the moment the CLI printed its next line, even though the link
  itself was still what the sign-in needed — it now stays up for the whole attempt, and clicking it now also leaves a
  visible "Opened in your browser." note rather than only a hover tooltip.
- fixed: the placeholder text in the "paste the code here" field of an in-app sign-in was sitting off-centre instead
  of lining up with the field and the Submit button beside it.
- fixed: a session on a project whose memory lives in a plugin — a Depot project, say — now starts with that plugin's
  MCP server ticked, so it can actually reach the project knowledge the project points at. The server was offered to
  such a session but arrived unticked, and because the project editor's server checklist is the same for every project
  it had no row there either, so there was no way to switch it on other than per session, every session.
- fixed: "Move to workspace" in a session's right-click menu now actually opens its submenu and moves the session.
  It used to do nothing on click, with no submenu and no error. With only one Sessions workspace it now shows as
  disabled instead of silently doing nothing.
- fixed: the voice assistant no longer forgets the conversation when its context fills up. It used to start over from
  scratch at that point — everything you had said to it was gone, and all that carried across was its standing
  instruction, its memory file and whatever note it last wrote itself. On a provider that can summarise its own
  conversation (Claude), it now asks for exactly that instead, and carries on in the same conversation with the
  transcript intact. Starting over stays as the fallback: for a provider with no such mechanism, and for the case
  where summarising did not free enough room.
- fixed: a newly started session's usage pill no longer stays empty until its first turn finishes. Asking for the
  figures as the session starts was scoped to reopened sessions, on the assumption a fresh one has nothing to report
  yet — it does: the allowances are account-wide and the context window is in use from the system prompt onwards. A
  session you start and then leave working for half an hour now shows its pill from the outset instead of nothing.
- fixed: a resumed session's usage pill no longer sits empty until you send it a fresh message. The header only ever
  pulled the context/allowance figures at the end of a turn, so a reopened session — which already has real figures
  the moment it reconnects — showed nothing at all until you actually prompted it, indistinguishable from a provider
  that does not report usage. It now asks for those figures once as it reconnects, so the pill reflects the resumed
  conversation right away.
- fixed: dictation that produces nothing now says so instead of leaving you looking at a cockpit where nothing
  happened. Hold the key, talk, let go, and if the transcription failed or no speech was heard, the floating voice pill
  says which for a few seconds and then goes away on its own — where before it simply vanished the moment the answer
  came back empty, whichever of the two ways that happened. The same is true whichever key route you use: the in-window
  key had no way to report anything at all, so a first dictation that spent minutes downloading its speech model showed
  nothing while it did, and neither did one that failed at the end of it. Releasing the key without having started a
  recording — voice switched off for that session — no longer tries to end a hold that never began.
- fixed: a worktree is no longer emptied out from under the agent working in it. The periodic cleanup asks which
  sessions are alive and clears up the worktrees of the ones that are not — but it asked only the session grid, and a
  worktree the voice assistant made, or one belonging to a delegated task that runs without a pane, is owned by
  something the grid has never listed. Every one of those read as abandoned on every quarter-hour sweep, so a worktree
  with nothing uncommitted in it was handed back to git while an agent was still working there: the folder emptied, and
  its branch deleted if the work had already landed on the main branch. Uncommitted work was kept — that part of the
  rule always held — but committed and pushed work vanished from disk with no warning and nothing in the log naming a
  removal, because none was asked for.
- fixed: an agent could remove a worktree the voice assistant was actively working in by asking a different session to
  clean it up. Asking to remove a worktree checks whether its owner is still running before letting a session other
  than that owner take it — but the check asked the session grid, which never lists the voice assistant, so its
  worktrees always read as ownerless and any session's cleanup request could take them regardless of whether the
  assistant was still using it.
- fixed: a session running the Claude terminal no longer sits on "Idle" while it is plainly working. The cockpit
  worked out which conversation file such a session was writing by taking whichever one appeared on disk after the
  launch — and every other Claude on the machine writes one too, several of which are written once and never again.
  Pick one of those and the session goes quiet for the rest of its life: nothing arrives, so nothing ever corrects
  the status, and the dot stays on the value it had before it started. The session now says which file is its own
  and the cockpit reads that one, including after a `/clear` starts a fresh conversation in the same pane.
- fixed: the voice assistant can read the transcript of a session running in a terminal. It could only ever read the
  headless kind, so asking about any other session got "no AI session is running on that pane" — for a pane it had
  just listed as live, with a status line of its own. Two answers that cannot both be true, and no way to tell from
  the outside which one was wrong. A shell pane, which has no agent behind it, still answers that it has nothing.
- fixed: a local CI run that falls over before your code is reached no longer reports itself as a failed build. When
  the container engine or the network cannot hand over one of the actions a job sets itself up with, every job on the
  machine goes red in seconds without compiling a line — and "build failed" sends you looking through a change that
  was never built. Such a run now comes back as one that reached no verdict, says so in a sentence, and brings the
  engine's own message along. A step of your own that fails is still a plain failure.
- fixed: a keyboard shortcut you set to a combination another action already uses now actually fires. Nothing
  stopped two actions holding the same keys, and the one that ran was decided by where it happened to sit in the
  list — so the shortcut you had just set did nothing, with no sign anywhere that it had lost. A combination now
  belongs to one action: assign it somewhere else and the row that held it before gives it up, in front of you.
- fixed: warming up the speech model on a key press no longer costs you the wait it was meant to save. Two presses
  in quick succession, or a short sentence whose release arrived while the model was still loading, made the second
  one throw away the first and start the load again — so a cold start landed exactly where it hurts, after you had
  stopped speaking. A press also counts as using the transcriber now, so the housekeeping that unloads an idle one
  cannot reclaim the model you just warmed. The cockpit
  swallowed the click itself but not the release that belongs to it, and the terminal forwards a release to the
  running program whether or not it saw the press — so the agent in the terminal received a stray click over the
  same link and opened it a second time, on top of the tab the cockpit had already opened.
- fixed: "Create backup…" no longer fails with "the process cannot access the file … because it is being used by
  another process". The backup is built as a temporary file and then moved to the place you picked, and on Windows
  that move happened the instant the archive was closed — the same instant a virus scanner opens a newly written
  .zip to look inside it. The bigger the backup, the longer that scan, and the more reliably the move lost the
  race. It now waits for the file to be released instead of giving up on it. If something really is holding a file
  after several seconds, the message says which one and what to do about it, rather than naming an internal
  temporary file you have never seen.
- fixed: a long reply from a headless Claude session no longer eats the machine's memory. With a session streaming,
  the cockpit's memory use climbed by tens of megabytes a second — one report reached 25 GB and was about a minute
  from taking the machine down — and on Windows the window froze for seconds at a time. The reply's text was redrawn
  from scratch on every fragment that arrived, so the longer the answer got the more work each new fragment caused,
  and it kept accelerating instead of settling. Replies now repaint at a steady rate while they stream, the way
  terminal sessions already did, and each repaint only redraws the part of the reply that actually changed instead
  of the whole thing — so a long answer costs no more per fragment than a short one. This also silences a flood of
  internal warnings — thousands a second — that a session pane was writing to the system log the whole time.
- fixed: a headless Claude session now keeps following the newest message while it streams. Scrolling up left the
  view where you put it but never resumed on its own once you scrolled back down — the jump-to-newest button had to
  be clicked, and sometimes the view was pulled back down while you were still reading. The newest message also kept
  ending up partly hidden behind the composer. All three came from the same place: the transcript only keeps the
  messages you can see in memory and estimated the length of the rest, and the bottom of that estimate is a point
  the view can never actually reach. It now follows the last message itself rather than a computed position.
- fixed: a headless Claude session now shows the five-hour and weekly allowances in its usage pill, the same as a
  terminal session. It could only ever show the context window: the figures a terminal session reads out of Claude's
  status line have no equivalent for a headless one, and the usage events it does receive name the window but leave
  out how full it is until you are nearly at the limit. Cockpit now asks Claude for its own usage summary — which it
  answers locally, costing nothing and no tokens — and reads the figures from that. A reading older than fifteen
  minutes is dropped rather than shown, so the bar is never a stale number wearing a current face.
- fixed: a ticked usage window that no figure has arrived for now says so in the usage detail, instead of leaving the
  setting looking broken. The pill itself still stays empty — a window nobody reported is not "0% used".
- fixed: Ctrl+double-clicking a link in a terminal session opened the page twice.
- fixed: an MCP server that refuses a single call no longer reports your sign-in as expired. The cockpit renews the
  credential and sends the call once more; if that is refused too, it used to tell you the sign-in had run out and to
  authorize again from Settings — advice that cannot be right, because the token it just tried was issued seconds
  earlier. In practice the next call went through untouched, but a session reading "sign in again" stops and waits for
  something nobody needs to do. It now says the sign-in could not be confirmed and that sending the request again is
  the thing to try. A sign-in with nothing left to renew from is still reported as expired, because there it is.
- fixed: a worktree the voice assistant made for itself no longer stays "in use" forever once a session actually
  starts in it. The assistant is always shown as present, so handing such a worktree to the session running in it
  used to be refused the same way handing a live session's own worktree to someone else is — the folder just sat
  claimed by the assistant, unremovable and never cleaned up, however long ago the session it was made for had
  finished. It now changes hands the moment that session starts, so closing the session cleans the worktree up like
  any other one.
- fixed: the cockpit window (and every dialog, and the assistant's pop-out chat) no longer shows a thin margin
  around itself while not maximized. It came from the platform's own resize border, kept for edge/corner dragging
  but visible as an unwanted frame around the whole window; every window that used to have it now draws no
  decorations of its own at all and gets its resize edges and corners back through its own hit-testing instead.
  Maximized windows already looked right and are unchanged.

### Added

- added: a Claude session that runs headless (rather than as a terminal pane) can clear its context. A terminal
  session has `/clear`; a headless one had no way to reach it, so a session that filled up could only be closed and
  started again — which also cost you its name and its place in the workspace. "Clear context" in a session's
  right-click menu restarts that same pane on a new conversation: same name, same place, same profile, same folder
  and the same MCP servers, with an agent that remembers nothing from before. The transcript is kept, with a line
  across it marking where the agent's memory stops, and the context and token figures in the header start over
  rather than describing a conversation that is no longer running. It asks first, because it cannot be undone —
  and says that from there on the pane is a new conversation with a new id. Nothing is deleted: the conversation
  so far stays on disk and can still be resumed under its own id. Terminal sessions do not offer it; there you
  type `/clear`.
- added: a Claude session that runs headless (rather than as a terminal pane) now shows the usage pill in its header
  too — how full the context window is, plus the rolling five-hour and weekly allowances. Those figures used to
  appear only for terminal sessions, because they were read from Claude's status line, which a headless session
  never runs; they are now taken from the session's own output instead. They are the same numbers a terminal session
  shows, not an approximation. A window Claude does not report simply stays off the pill, so nothing ever reads as
  "0% used" when it really means "not reported".
- added: the Managed worktrees dialog can release a worktree from a session that only ever showed a restore offer
  and never actually started. A pane returning from a crash with nothing more than "resume or start fresh" used to
  hold its worktree indefinitely — no timeout, Remove and Reattach both greyed out until you started or closed that
  pane by hand. "Release" detaches the worktree from that offer (discarding it) without touching any files; Remove
  and Reattach become available right after, the same as for any other worktree a session has let go of.
- added: voice now leaves a trace you can read back. A dictation records how much audio it was, which backend ran
  it, whether the worker had to start up first and how long the transcription took; reading aloud records how long
  the model took to load once, and per turn how many sentences were spoken, in how long, and whether it was cut
  short. Until now "dictation feels slow" could not be answered with anything but an impression — the load that a
  cold start costs after an idle stretch is now visible as a number.

- added: the YouTrack dialog's status filter now lists every stage the project actually has, instead of only the ones
  that happened to appear in the first hundred issues it loaded. Picking a status also searches the whole project
  rather than filtering the loaded page, so a ticket that never made it into that page is still found. Stages that
  mean "finished" are left out, because the dialog only ever lists open work and offering one would always come back
  empty.
- added: searching in the YouTrack dialog reaches past the loaded page. When the list is capped, typing a search and
  pressing Enter (or clicking away) asks the server instead of filtering what is already on screen — so a ticket
  beyond the first hundred is findable rather than apparently missing.
- added: both the YouTrack and the GitHub Issues dialog now say when the list they show is capped, so "nothing found"
  cannot quietly mean "nothing found in the first hundred".
- added: the GitHub Issues dialog has a label filter, filled from the labels the repositories actually define rather
  than the labels that happen to appear in the loaded issues, and filtering by one searches all open issues instead
  of narrowing an already shortened list.
- added: the prompt-template and branch-name settings now say what each placeholder produces and show an example,
  in YouTrack, GitHub Issues, GitHub Pull Requests and Autopilot. The names were listed before; what they stand for
  was not — and two of them look alike and behave nothing alike.
- added: the open pull-request list keeps refreshing in the background, whether or not you are looking at it, and a
  view now draws the last known list immediately instead of waiting for a fetch. The list survives a restart, marked
  as older until the fresh one arrives.
- added: a plugin's left-menu button can carry a live counter, updated after registration without re-registering.
- added: Autopilot can now be started on an epic (an issue with subtasks), not just on a single issue. It reads the
  "depends on" order between the subtasks, picks the next one that's actually ready to work and not already merged,
  and runs the existing single-issue pipeline on just that one — stopping at a merge-ready PR exactly like before.
  You still merge every step yourself; the next subtask only opens once the previous one is actually on `main`. A
  subtask that isn't ready yet pauses the chain with a comment on the epic explaining which one and why, and
  progress is posted to the epic as each subtask settles, so you can follow the whole chain without opening every
  subtask.
- added: the Depot plugin now has a settings screen — connect one or more Depot instances by name and URL, with no
  auth fields to fill in: Depot has a single sign-in path, so each connection is handed to the cockpit's own MCP
  sign-in flow and the plugin never sees or stores a token. Renaming or removing a connection cleans up its old MCP
  registration so a stale entry never lingers, and every connection shows whether it's signed in right there in the
  row, with a Sign in button that opens the browser only when you click it.
- added: each Depot connection now offers its own memory source in the project editor's picker, so "Depot project —
  Wispslate" and "Depot project — Synvolution" show up as distinct choices instead of one shared "Depot project"
  entry that couldn't say which instance it meant. Adding, renaming or removing a connection takes effect
  immediately in the picker, no restart needed. Your first connection keeps working exactly as before under its
  existing project link.
- added: the project editor's "Choose…" button next to a Depot memory row now opens a picker of your Depot
  projects by name, instead of staying disabled and asking you to type a slug you can't see anywhere. Not signed in
  yet? One "Sign in" button, not an empty list. Couldn't reach Depot? It says so. Each entry also now says whether
  it's a Depot project or a Depot brain, so the two are no longer indistinguishable by name alone.
- added: a Memory row pointed at Depot now confirms the project you typed actually exists, the same way a broken
  file path is already flagged under a Reference row. Type a Depot project slug and, a moment later, you'll see a
  green confirmation naming what was found (with its kind), a red "could not be found" (also shown for one you
  don't have access to — Depot can't yet tell the two apart), an amber "not signed in" only when that's actually
  true, or a separate amber message when the check itself couldn't complete — network trouble, say — so you're
  never told to sign in again when you already are. Nothing is shown while the field is empty or while you're still
  typing.
- changed: a session on a project linked to a Depot connection is now offered only that connection's MCP server,
  not every configured Depot instance — and a project with no Depot connection gets none at all. It shows up
  ticked in the New-session checklist like any other plugin-offered server, so you still have the last word on
  whether it's used.
- added: an incompatible plugin now says so before you install it, not after. The plugin store's browse card shows
  a red "Incompatible" badge with the reason — a contract version it was built for, or a cockpit version it needs —
  and its Install/Update button is disabled; nothing is hidden from the catalogue, but a click that would only fail
  is. The host now also refuses the install itself when a plugin needs a newer cockpit, rather than letting it
  install and only refusing it the next time the cockpit starts.
- added: a project can now keep more than one memory location, plus standing instructions and reference material,
  each as its own row in the project editor — "Memory, instructions and reference" replaces the single Memory field.
  Add as many rows as you like, pick what each one is for, name it, and say whether a starting session is told
  about it ("Tell sessions" defaults on here, unlike the notes above it, because a memory or instructions row exists
  specifically to be handed to a session). Picking a file or folder from inside the project's own folder stores it
  relative to that folder, so the project definition still makes sense on a machine that has it checked out
  somewhere else; point it outside that folder and the row says plainly that the path is specific to this machine.
  A row whose reference cannot be found is shown as broken right there in the editor, rather than only failing
  silently when a session goes looking for it. Existing projects open with their memory location already in the
  list, in the same "Memory" role it always was. An instructions row can also be sent along in full rather than
  merely pointed at: tick "Send along" and the file's contents travel with the session, quoted and credited to where
  they came from, so a session obeys the conventions file instead of being told a path it may never open. Off unless
  you tick it, on instructions rows only — the cockpit never decides for itself that a file is safe to hand over,
  because a rule that judges that will eventually judge it wrong. Ticking it is a request rather than a promise:
  the contents still have to fit within what one project may contribute to a session's opening prompt, and the
  session is always told which of the two it actually got — the file, or only its location. A file too large, gone
  missing, or slow to read is reported as such instead of quietly arriving empty.
- added: terminal (TTY) sessions now show up in the usage history alongside SDK sessions — their token counts land
  in the same trail as every turn completes, so a session run straight in the terminal is no longer invisible to
  usage tracking. No cost figure is recorded for these, since the CLI's own transcript does not report one and the
  cockpit does not estimate one from tokens.
- added: an autonomous run now hands its reviewer proof instead of sending it off to look. When a step finishes, the
  cockpit asks git itself what changed in the run's worktree since that step started, and gives the reviewing session
  that account to judge the acceptance against — where before it was told to distrust the step's summary and go read
  the files, every single time. The step doing the work neither writes that account nor can alter it, which is the
  whole point: the side being checked no longer supplies the evidence it is checked on. It arrives fenced off and
  labelled as data, so a line sitting inside a diff cannot be read as an instruction to the reviewer. Alongside it the
  cockpit raises its own flags — a step reporting work while the worktree is unchanged, tests reported passing when
  nothing test-shaped was touched, a step already sent back once, a step whose earlier attempt ended without a verdict
  — and the reviewer is told to go and read the files whenever one of them fires. **Where there is nothing to observe,
  nothing changes:** a run in a plain folder with no repository, or a review gate whose deliverable is a judgement
  rather than a change, keeps the full inspection exactly as it was. The cheaper route is offered only where the
  cockpit can genuinely see what happened, and it says so rather than quietly settling for a summary. Tests still run
  where they always ran — there is no test run added per step.
- added: a Claude SDK session's sub-agents (the Agent/Task tool) are no longer a black box — their own tool calls,
  text and thinking now nest under the parent Task row instead of vanishing until the final result. Collapsed by
  default (a "N sub-agent events" line you expand), since a sub-agent can be chatty and that verbosity is not
  something you want dropped on you by default. A sub-agent's own narration never reaches read-aloud — only the
  session's own reply does. Requires a recent enough `claude` CLI to forward the extra detail; an older one simply
  starts the session without it, same as before.
- added: the update banner and the Options → Updates tab can now download and install a new build for you, on a
  copy the installer set up (a Windows install, or a Linux AppImage) — "Update now" downloads with a progress bar,
  confirms before restarting (naming any sessions still running, so nothing is cut off unannounced), and restarts
  straight into the new version; "Install on next start" downloads the same build but applies it quietly the next
  time you close the cockpit, so a running session is never interrupted. A copy that wasn't installed by the
  updater — a checkout, a tarball, a distribution's own package — keeps the plain "Open the release" link it always
  had; nothing is ever applied without an explicit click.
- added: the YouTrack plugin's "attach message images to an issue" tool can now attach an image file directly by
  path, not only images sent with the current message — so a screenshot pasted straight into a terminal pane, or
  an image an agent produced itself, can be attached even though it never rode along on a message. The path must
  point inside the terminal's own paste folder or the session's working directory, and the file must genuinely be
  an image (checked by its content, not by its name) — anything else is refused.
- added: an autonomous run now has a spending ceiling it cannot talk its way past. A session provider can state what
  its own models cost, cheapest first — the Claude provider now does, and any other provider can — and the planner is
  held to that ranking: unless you have set the cost strategy to quality-first, a step that is not a review gate has
  to run on the cheaper end of what its profile offers, and a plan that ignores it comes straight back to the planner
  naming the models that step may use instead. Review gates keep the full range, because a missed finding costs more
  than the tokens it would have saved. Prices are the provider's own **estimate** and are labelled as such wherever
  they appear: where a provider has no priced feed to read, the figures are compiled into it and can quietly go out of
  date — read them as proportions between models, never as a quote. What a session actually
  cost still comes from the provider itself, exactly as before. If you would rather the planner kept its free hand,
  set the cost strategy to quality-first and no ceiling applies.
- added: run history now shows which profile and model actually ran each step. While a run is live that sits on the
  step as a chip and vanishes when the run settles; now it survives, so you can open a finished run and see where its
  money went instead of inferring it from the plan you approved.
- added: your sessions come back after a crash. A cockpit that closed with agent sessions open — whether you
  closed it or it went down under you — starts again with those panes where you left them, on the right desk,
  under the right profile, in the right folder and worktree, each one carrying an offer to pick its conversation
  back up. **Nothing starts by itself.** A pane comes back idle and says so, with three buttons: resume the
  conversation, start a fresh one in the same place, or close the pane. That is deliberate — half a dozen agents
  quietly resuming the moment you open the cockpit would burn through tokens before you had looked at the screen,
  and would redo work you may already have merged. A restore is offered, never performed.
  When an earlier conversation cannot be picked up, the pane says which of the reasons it is instead of quietly
  starting something else: the provider keeps no conversation you can return to, the profile it ran under has
  been renamed or removed, its worktree is gone, or the provider was asked and no longer has that conversation —
  in which case it shows you what the provider actually said. A pane that fails to resume stays on screen with
  the reason instead of vanishing, which is what it used to do. Terminal panes are not restored yet.
- added: the cockpit now keeps a note of what each session pane was doing — its profile, its conversation, the
  folder and worktree it ran in, the permission mode it was on — in a `session-state.jsonl` next to your
  `cockpit.json`. Nothing reads it back yet, so nothing looks different; this is the record that makes bringing a
  session back after a crash possible at all. It is written the instant something changes, never on the way out:
  a note that is only saved when the cockpit closes cleanly is precisely the note that a crash never gets to
  write, and a crash is the whole reason it exists. It is a plain text file, one line per change, and a line that
  gets cut in half by a crash is skipped on the way back in rather than taking the rest of the file with it. On
  startup the log folds down to one line per pane so it does not grow forever. It drops nothing while doing so —
  panes themselves are not remembered across a restart yet, so there is no honest way to tell a pane that is gone
  from one the cockpit simply has not rebuilt, and guessing there would throw away the state this is for.
- added: a provider can now tell the cockpit which conversation a session is actually running under. On its own
  this changes nothing you can see — nothing is stored and nothing is reopened yet — but it closes the gap that
  made bringing a session back impossible: the cockpit could always ask a provider to pick up a named
  conversation, and had no way to learn that name in the first place unless you went and picked one out of the
  conversation search by hand. It works the same whether the cockpit drives the provider itself or the provider
  is a command-line agent running in a terminal, where the conversation has to be recognised rather than
  announced. A provider that has nothing you could return to now says so plainly instead of handing over an id
  that would fail the moment you used it — Gemini and GitHub Models keep a session's history only for as long as
  it is running, and admit it. On a terminal session the cockpit reports the conversation once, at startup, and
  stays quiet when it cannot tell two sessions' conversations apart: several agents can share one provider
  directory, and a confident guess there would be the wrong conversation attached to the wrong tab. Plugin
  authors: `IPluginSessionDriver.Conversation` falls back to the `SessionId` you already report, so a provider
  that is already built needs no change; a terminal provider receives a `ReportConversationId` callback on its
  launch context.
- added: the Local CI plugin now actually runs a workflow job, where it previously only told you whether it could.
  From a session's header you get the list of jobs in that project's `.github/workflows` — each with a Run button or
  the concrete reason it cannot run on this machine — and the one you pick runs in a container on your own Docker,
  against the checkout that session is working in, with the log filling while it happens and a Stop that leaves no
  container behind. The package cache and the .NET SDK live in volumes that survive between runs, which is what makes
  the second run worth doing: on this repository's own plugin job, 237 seconds cold against 131 warm. One run at a
  time, and the container gets half the machine's cores, because the machine it runs on is the one you are working on.

  A job runs whole or not at all. What the plugin will not run whole — a matrix, a runner that is not Linux, a job
  exchanging artifacts with another — it refuses with the reason instead of running the parts it understands, because
  a result that quietly skipped steps is more dangerous than no result: you would trust it. And nothing it reports
  ever says CI is green. act's own documentation warns that its images differ from GitHub's, so a pass here predicts
  the pull-request check rather than replacing it, and the wording says so.

- added: a session can run those checks on its own project and read the verdict back, so an agent can see whether its
  work stands up before it pushes. It cannot ask for a run anywhere else: the project is the calling session's, taken
  from the cockpit rather than from anything the agent supplies, and there is no path to pass. Every run asks you
  first, showing the exact command that will run — and on a failure the answer carries the tail of the log where the
  failing test is, not the whole of it. The last local run shows in that session's header.

- added: a project can be set to hold back its pull requests until a local run has passed on the commit that is
  checked out. Off everywhere until you turn it on, per checkout. A run that failed, never happened, could not happen
  because Docker was not there, or passed on an earlier commit all count as "not run" — never as a pass, which is the
  failure this kind of guard usually has. You can still open the pull request: the cockpit asks, shows the reason, and
  records your answer in the consent trail. Both places that open one — the workflow step and an Autopilot run at
  merge-ready — ask before they do, and an Autopilot run held back still pushes its branch and says why no pull
  request followed.

- added: an agent session sharing a tab with others can now agree to be woken, and a neighbour with something that
  cannot wait can ask for it. Waking is off for every session until that session turns it on for itself, and nothing
  a sender does can override that — the agreement is the permission. When it is on, a message a neighbour marks
  urgent starts a turn on that session instead of waiting for one, which is what makes "leave that branch alone"
  arrive in time rather than after the damage. Whether a session has agreed shows on its row when an agent lists who
  else is on the tab, so a sender can tell beforehand whether urgent means anything for that addressee.

  A wake never interrupts. A session that is working, or whose sub-agents are still running, is left alone and the
  message simply waits. A session with a permission question open in front of you is left alone too: nothing an agent
  calls urgent comes ahead of a decision you are standing at. The question is still there afterwards, and still
  answerable. Neither can a wake cross tabs — the cockpit re-checks that the two sessions still share one tab at the
  moment of waking, not just when the message was sent.

  A woken session is told plainly what happened: the turn opens with a block naming the session that caused it and
  saying that the cockpit started this turn, that you did not type it, and that being called urgent is the sender's
  opinion and not permission for anything. Every wake, and every wake that was refused with the reason it was
  refused, goes on the same trail the messages themselves are recorded on — a wake spends a turn you are paying for
  without you having asked, so it leaves a mark either way.

- added: the Plugin manager now tells you, per plugin, whether it actually loaded, its last error, and whether
  its MCP server contribution is still standing — three separate facts instead of one vague status line. A
  contribution that fails after a plugin has already started (its MCP server registration, say) is caught and
  attributed to that plugin instead of vanishing on a background task, and it no longer gets reported as "failed
  to load" — that wording is now reserved for a plugin that never actually started. The Plugin manager and the
  startup banner read the same underlying record, so the two can no longer tell a different story about the same
  plugin.
- added: a message from another agent now reaches a session on its own, instead of sitting there until that session
  happens to go and look. Whenever the session next takes a turn — because you typed something, or because a scheduled
  resume woke it — what is waiting for it rides out with that turn, a few messages at a time, in a block that says
  plainly that it came from another agent and not from you, and names the session it came from. Your own transcript
  says so too, next to what you typed, so an answer never arrives without a visible reason for it. A message rides on
  a turn that was going to happen anyway — the one thing that can start one is described further down, and only for a
  session that asked to be reachable that way.

  This works for the sessions the cockpit composes turns for. A session that is a command-line agent running inside a
  terminal cannot have it, and that is a real limit rather than an oversight: there the cockpit writes keystrokes and
  the program on the other side decides when a turn begins, so text nobody typed must never carry the Enter that would
  send it. Rather than leave that difference to be discovered, the roster now says per session which of the two it is,
  and so does the reply when one agent messages another — so a sender can tell "waiting to be collected" apart from
  "will be seen", instead of reading "delivered" and waiting for an answer that was never coming.

  At most five messages ride along on any one turn, and no more than about twelve thousand characters of them — both
  well under what a session gets when it asks for its own mail. This is text arriving on a turn you started and are
  paying for, so neither a backlog nor one very long message can bury what you actually typed; the character limit is
  counted on the text as it is really sent, since a message can take up several times its own length once it is marked
  up. The rest stay waiting, and the block says how many there are. A session with nothing waiting adds nothing
  whatsoever to its turns. And a message counts as read only once the turn carrying it has really gone out — if that
  send fails it goes back to waiting, rather than vanishing with its sender having been told it arrived.

- added: a fan-out workspace — one task, several agents working on it at once. You type the task, set up two to five
  arms (each an agent profile and, if you want, the angle that arm should take) and press Start. Every arm runs as its
  own session in its own git worktree, tiled side by side so you can watch them diverge. Vary the profile to put
  different providers on the same brief; vary the angle to get different takes out of one provider. It is the same run
  either way — the arms differ only in which field you filled in.

  The separate worktrees are what make the takes comparable afterwards: no two arms touch the same checkout, so none
  of them can spoil another's work. Closing the workspace ends every session it started, and those sessions never
  appear in the ordinary session grid — they live only on the fan-out's tiles. Comparing the arms side by side,
  picking a winner and cleaning up the ones you did not take is not here yet: for now a run is something you read and
  act on yourself.

- added: a project's memory can now point somewhere other than a folder. A plugin that keeps its own store of
  project knowledge can offer itself in the project editor's Memory row, next to "Folder" — pick it and the box asks
  for an identifier instead of a path, "Choose…" steps aside, and a session started on that project is told in
  plain language where its memory is and how to reach it, instead of just being handed a bare reference to make
  sense of on its own. A project pointing at a source whose plugin is not installed keeps its reference untouched;
  saving it does not lose or garble what it already pointed at.

- fixed: a delegated task that fails now tells you why. One failure finishes a task twice — first the refusal that
  knows the reason, then the turn's own ending, which reports failure without a reason of its own — and the second
  was overwriting the first, so every failed delegation came back with an empty error however plainly the provider
  had explained itself. A task that goes on to answer on a later turn still clears the failure it recovered from.

- fixed: a build published as a folder rather than a single file now carries the example workspace and Autopilot with
  it. Both were copied next to the executable but never into a publish, so that route handed over a cockpit missing
  two of the plugins it ships with — the single-file build was unaffected, which is why it went unnoticed.

- added: a Local CI plugin that answers, honestly, whether this machine could run your GitHub workflow jobs before
  anything tries. Its settings page reports Docker in three states rather than two — not installed, installed but the
  engine is not answering, and ready — because "Docker Desktop is not running" is the usual one and what you do about
  it is nothing like what you do about a missing install. It also checks the engine runs Linux containers, since a
  Windows-container engine is perfectly healthy and still cannot run a workflow image, and whether the act runtime is
  on PATH, naming the command to install it rather than failing at the first run. The cockpit does not ship act: it is
  a per-platform binary of tens of megabytes that is released far more often than the cockpit, so a bundled copy would
  be out of date between releases.

  It also reads the project's workflows and says, per job, either that it can run locally or the concrete reason it
  cannot — it uses a matrix, it needs a macos-latest runner, it exchanges artifacts with another job, it uses an action
  that only means something on GitHub. Only `actions/checkout` and `actions/setup-dotnet` are treated as free, because
  the working tree already is the checkout and the SDK is in the image. Anything the check does not recognise makes a
  job unrunnable rather than being ignored: a job that runs half of itself and comes out green is worse than one that
  never ran. This release only tells you; nothing is executed yet.

- added: Autopilot's history now says how many runs in a row settled merge-ready without anything having to be put
  right — the one figure that says whether a run can be left alone, rather than how much work it did. It shows above the
  history list and again on the toast when a run settles, so it reaches you at the moment it changes instead of only in
  a panel you would have to go and open.

  The count is strict on purpose, because a lenient one flatters itself. A step the review sent back and a step that ran
  out of attempts each count as a correction; a run that ended blocked or stopped, or that reached the end without the
  pull request it promised, is never counted as clean at all. A question the run raised and you answered does not count
  against it — that is the run doing what it is meant to do, and it is counted separately. Neither does merging it
  yourself; that is the gate, not a repair.

  Some corrections cannot be seen from the inside. If you changed the work yourself before merging it, right-click the
  step in the history and say so — a classification you set stays marked as yours, so a number that was adjusted by hand
  never reads as one the run arrived at on its own. Cost, tokens and duration are deliberately not repeated here; they
  are already recorded per run, and measuring the same thing twice only produces two figures that drift apart.

  One thing the count cannot see from where it stands: a review finding an agent repairs inside its own step, and then
  passes, looks exactly like a step that never needed anything. The figure is therefore a floor, not a verdict — which
  is why it can be corrected by hand. Update the Autopilot plugin from the store to get it.

- added: agent sessions sharing a tab can now say what they are working on. An agent claims a worktree, a branch or a
  file, and the next agent that reaches for the same one is told it is taken, by which session, and for how long — so
  two agents on one working tree find that out before the first edit instead of when it fails to compile. What is
  claimed also shows on each session's row when an agent lists who else is on the tab.

  It signals, it does not lock. Nothing stops an agent from working on a claimed resource, and nothing needs cleaning
  up afterwards: a claim is only its holder's to release, and everything a session holds is dropped when that session
  closes — including whatever the agent never got round to releasing. Resources are matched exactly as written, so agents have to
  agree on the spelling — the same worktree written two ways is two claims. Claims stay inside one tab: a session on
  another tab neither sees them nor is blocked by them, which also means two agents on different tabs reaching for the
  same folder still do not see each other.

- added: a line between a session's transcript and the box you type in. The transcript scrolls under that edge and its
  bottom row is cut off mid-letter, and with the same background on both sides and nothing drawn in between, that cut
  row read as a bar running underneath the pulsing "Thinking…" indicator rather than as a message scrolled out of
  view. The line makes the edge an edge, so what is scrolled away looks scrolled away.

- added: starting a new session from a YouTrack issue or a GitHub issue now opens the dialog with the cockpit project
  that issue belongs to already selected. It uses the link you set in the project editor under "Where it is tracked" —
  the YouTrack project, or the repository — so a dialog that already knows which ticket it is for no longer asks you to
  name the project by hand every time. The project brings its folder, profile, worktree default and MCP servers along,
  exactly as picking it yourself would, and all of it stays editable until you press Start.

  Nothing is guessed. If no project claims that tracker project or repository, the picker opens on "No project" as it
  always did, and it does the same when two projects claim the same one: a preselection you would stop reading is worse
  than none at all. Update the YouTrack and GitHub Issues plugins from the store to get it.

- added: the product icons now exist as a square set that can actually be used as an app icon and a favicon. The two
  files that came out of the logo sheet were separate renders — different canvases, a W drawn at different
  proportions in each — so neither was square and the pair did not read as one mark in two colours. They now share
  one geometry down to the pixel: lay the blue one over the teal one and only the colour differs. Each comes as a
  1024 master, a ladder from 16 to 512, and a `.ico`, in `brand/`.

  Two blemishes left over from cutting the icons out of that sheet are gone with them: a thin line above the W and
  a loose speck off to the right, both of which you would have seen once the mark was drawn large.

  The icon on the cockpit's own window is unchanged — it is drawn separately, from the same mark, and still shows
  those two blemishes. Moving it onto this set is a step of its own.

- added: every release and nightly now carries an installer and a portable build that an in-app update will be able
  to read — `AI-Cockpit-win-stable-Setup.exe`, `AI-Cockpit-linux-nightly-Portable.zip` and so on, with the platform
  and the channel in the name. They sit next to the downloads that were already there, which keep working exactly as
  before; nothing updates itself yet, and which of the two sets is the one to keep is still to be decided. If you are
  installing fresh today, the existing files remain the ones to use.

  The Windows download is no longer a single self-extracting .exe internally: an updater replaces an application
  folder, and a self-extracting file has nothing for it to replace. The portable .exe on the release page is
  unchanged and still a single file.

- added: a session can now see the other agent sessions sharing its desk. Until now an agent had no way of knowing it
  was not alone: two of them would end up on the same working tree and only find out when an edit stopped compiling.
  A new `cockpit-agents` server answers who else is on the workspace — their name, the profile they run under and the
  status line they last set — so an agent can look before it touches something shared.

  It only ever answers about the desk you are on. There is no argument naming a session, deliberately: the cockpit
  works out which pane asked from the connection itself, so an agent cannot reach another workspace by naming one,
  and a request the cockpit cannot attribute to a session is refused rather than guessed at.

  A pane that is on the desk but has never called in is listed as such instead of being left out. The difference
  matters: a server that silently failed to reach a session would otherwise look exactly like an empty desk, and
  nothing about that looks wrong. What it cannot tell you is *why* it never called — it may simply not have looked
  yet, or the server may not be mounted for it — so it says that rather than picking a cause.

- added: agents on the same desk can now send each other a message. An agent can notify another session it can see,
  with a short label and a body, and collect what was sent to it — so "I have the parser, leave it alone" is
  something one session can actually say to another instead of the two of them finding out by colliding. A message
  never interrupts anyone and never starts a turn by itself; how it reaches the other session, and how you can tell
  which way it will, is described above. The sender is stamped by the cockpit from the connection the request came in on, so an agent cannot send
  as someone else, cannot send to a session on another desk, and cannot send to itself. What arrives is marked as
  what it is — a note from another agent, not an instruction from you — and every attempt, delivered or refused, is
  written to an append-only log next to your settings that nothing in the app can erase. A message is capped at 2000
  characters and stripped of the terminal escape codes that could otherwise repaint or overwrite what the cockpit
  printed around it, and no session hands over more than 25 messages at a time — so a chatty or hostile neighbour
  cannot spend a session's whole context window, or its memory, on mail it never asked for.

- added: you can see whether you are signed in to an MCP server, and sign in from the servers dialog instead of
  having to start a session first. Each server that uses a browser sign-in now says "signed in" or "sign-in needed"
  in the list, with a button for each, and one for withdrawing the access again — which removes the token from the
  one place it is kept. Reading the status never goes near the network: it answers from what is stored, because a
  status is drawn for every server in the list and opening a dialog should not become an event on somebody else's
  server.

- changed: signing in to an MCP server is offered once that server is saved, and while its name is the one it is
  saved under. A sign-in is filed under the server's name, so a server that is not in the list yet — or whose name
  you are in the middle of retyping — has no name for it to be filed under; the dialog says which of the two it is
  rather than leaving a button that does nothing useful. Save the server, then sign in.

- changed: a saved server hidden because the cockpit already runs one by that name now says so when you open the
  dialog, instead of quietly disappearing from your settings the next time you save.

- changed: two MCP servers can no longer be saved under one name, and adding a server picks one that is free. A name
  is not a label here — it is how a server is identified to the agents and how its sign-in is filed — so a repeat
  used to mean one of them quietly did not exist: configured, ticked, and absent. A name already used by one of the
  cockpit's own servers is refused for the same reason.

- added: when you start a session with a server ticked that nobody has signed in to, the New-session dialog says so
  before the session begins rather than leaving you to find out at the first tool call. It says it and no more —
  starting anyway stays your call. The tool count beside such a server stays blank on purpose, since counting a
  server's tools would mean connecting to it and that must not open a browser; the hover text now says that is the
  reason, where it used to offer "offline, needs a sign-in, or its plugin isn't loaded" and leave you guessing.
- added: Autopilot starts an issue only once someone has put it on the stage that means "this is ready to be worked
  on" — `Ready` on YouTrack, the `ready` label on GitHub Issues — and refuses anything else with a note on the issue
  saying why. The reason it does not simply read the ticket and judge for itself: the ticket is the thing that gets
  out of date. Items sit in a backlog claiming a fix is impossible where the guard is already in the code, or calling
  a decision open that was taken weeks ago, and an agent reading that text has no way of knowing. A stage a person
  moved the item onto is a different kind of evidence, so that is what it keys on; its own judgement of whether the
  work fits one run still follows, and planning an epic now leaves out the children nobody has marked ready. An issue
  still marked `[Brainstorm]` is refused whatever stage it is on. Run safety in Autopilot's settings has a box per
  tracker for what that stage is called — empty one and that tracker starts from any stage, as before.
- added: that same "ready to be worked on" check now also reaches an epic's children. Planning an epic reads its
  child issues to fold them into one run, and a child the CEO names is checked against the tracker itself — its real
  title and stage, not the CEO's own description of it — before the plan is accepted; one still on `Backlog` or
  marked `[Brainstorm]` gets the plan turned down with the reason, the same as if you had clicked it yourself.
- added: a YouTrack project that calls its status field `Kanban State` has its status read like any other. The issue
  list already knew that name; the read behind it did not, so those projects showed no status at all.

- added: the Updates tab now says when this copy of AI-Cockpit cannot replace itself. Not every copy can: one
  unpacked from the tarball, run from a checkout, or installed by your distribution has no installer behind it to
  hand the new version to, and the honest answer is a sentence and the release page rather than a button that would
  quietly do nothing. A copy that was installed normally says nothing new — it simply does not show the line.

  Groundwork for in-app updates: the update machinery is now in place and asked, at startup, what kind of copy it is
  running as. Fetching and installing a newer build still comes later.

- added: an MCP server that wants a header of its own now works. Some do not take `Authorization: Bearer` at all —
  they want `X-Api-Key`, or a scheme other than Bearer — and until now there was no way to configure that, not even
  by pasting the value in by hand. An HTTP server in the MCP servers dialog has a small list of custom headers you
  can fill in, and they reach every session route: your own tool loop, and a Claude, Codex or Kimi session alike.
  The value is masked as you type and stored under the same protection as an API key, because a header like that is
  a credential in all but name — which is the whole reason the field exists.

  A token in a query parameter stays unsupported. The specification is explicit that a credential must not travel
  there, and a URL ends up in logs and proxies in a way a header does not.

  On the Codex route the value travels through the environment rather than the command line, the same way its bearer
  token already did: a process argument is readable by every other account on the machine.

- added: the cockpit keeps what your sessions spend. The token and cost figures beside a session's status used to live
  only as long as the session did — close it, or close the app, and what yesterday cost was simply gone. They are now
  written to `usage-history.jsonl` next to your settings as each turn finishes, so they survive a crash as well as an
  ordinary close. Every line carries the tokens split by kind (input, output, cache read, cache write), the cost, the
  turns, how long the session had been working, the model in effect and the profile it ran under. Sessions an agent
  started on your behalf are marked as such, and when the plugin driving them names its run — Autopilot does — every
  session that run opened carries the same name. That last part is what makes "what did that run cost" answerable at
  all: a run takes a fresh session per step, so the figure was never on any one of them.

- added: an MCP server that asks you to sign in through your browser now reaches every kind of session, not only the
  ones the cockpit drives itself. Until now such a server was handed to Claude, Codex or Kimi as a bare address with
  no credential on it: in a terminal session you could at least tell the agent to sign in again on its own account,
  and a scripted session had no way at all. The cockpit now keeps the token from that sign-in and hands it to
  whichever agent the session runs, the same way it has always handed over an API key. One sign-in, one place it is
  kept, and it survives closing the app — while the server's refresh still holds, it is renewed without asking you
  anything. Worth knowing where it now lives: the token sits in your settings next to the API keys and is covered by
  the same protection they are, which means encrypted once you have turned settings protection on, and readable in
  `cockpit.json` until you do. It is tied to the address it was obtained for, so pointing a server at a different
  host — or letting a project supply its own server under a name the registry already uses — asks you to sign in
  again rather than quietly sending one host's credential to another.

- added: a session that starts against a server nobody has signed in to says so before its first tool call, rather
  than the agent meeting a refusal later with nothing to act on. Starting a session never opens a browser by itself:
  if a token cannot be renewed quietly, the session starts without that server and tells you, and asking for the
  sign-in stays your move.

- added: you can pick the colour and the line weight a mark is drawn in. Under the marking tools there is a row of
  five inks — the accent, red, yellow, green and white — and three line weights. They apply to the next mark you
  place, not to what is already on the capture. The weight changes frames, arrows and freehand lines; a note's
  letters keep their size, because a label is there to be read and "thin" there is not a style but an unreadable
  one.

- added: the tools sit on two panels you can drag where you want them — what you are taking with, and what you are
  marking with. A panel you move stays where you put it instead of following your pointer to the screen it is on,
  which is what they do until you touch them.

- added: you can type a note onto a screenshot. Press T on the selection surface, click where the note should go
  and type — "expected 12 here", a name for the thing an arrow points at. Enter or Escape finishes the note and a
  further Escape cancels the capture, so the key that ends your typing is not the one that throws it away. While a
  note is open the surface's shortcuts stand down: typing the word "Window" types the word rather than picking a
  window, blanking your region and taking the shot. The note is drawn on a plate in the opposite shade, which is
  what keeps it readable wherever it lands. A note you typed nothing into leaves nothing behind.

- added: you can draw on a screenshot freehand. Press D on the selection surface and draw — round a thing, through
  a thing, along a path the boxes and arrows cannot describe. One line per press, and Ctrl+Z takes back the whole
  of it rather than the last few inches. What you get is the curve your hand made: a quick drag leaves the pointer's
  positions tens of pixels apart, and joining those with straight lines turns a circle into a polygon. It is drawn
  in whichever ink you picked, so what it has to stand out against is your call.

- added: you can highlight part of a screenshot without hiding it. Press H on the selection surface and drag a band
  over what should be read rather than skimmed. It works like a marker pen — the colour goes into the page and the
  text on it stays where it was, rather than being painted over at half strength, which costs most of what makes
  text readable. Over a dark terminal it works the other way up, lifting the band out of the background instead of
  pressing it in, because ink over paper and ink over a terminal are not the same operation. The surface reads what
  is under the band when you draw it and decides which of the two that is.

- added: you can point at one thing on a screenshot. Press P on the selection surface and drag from where the arrow
  should start to the thing it should point at — the head lands where you let go and turns to face that way, so an
  arrow can come in from an empty corner instead of lying across what it indicates. The whole arrow scales with its
  length, so a short one and a long one are the same shape at two sizes.
  Ctrl+Z takes the last one back, on the same list as everything else you have put on the capture.

- added: the selection surface tells you what it can do, instead of expecting you to know. A small panel sits at the
  top of it with the four tools on it — dragging a region, taking a whole window, taking everything, painting over
  what should not be sent — each showing its key, with the one you are in lit up, and each of them clickable: the
  mouse is already in your hand, because you are dragging with it. R is the way back to dragging a region from any
  of the others. The panel puts itself on the screen your pointer is on rather than in the middle of a desktop that
  spans three of them, and it stays where you left it: a row of tools that steps aside while you are reaching for it
  costs more than one that sits over a picture which is frozen anyway. The one thing it does take from you is that a
  drag cannot be started on the strip it occupies — through it and past it is fine.

- added: you can draw a frame around the part of a screenshot the agent should be looking at. Press O on the
  selection surface and drag one out; Ctrl+Z takes it back. It is drawn into the picture itself when you confirm,
  not laid over it — what reaches the model is one image, and a mark that is not in it is a mark it cannot see.

- changed: hiding something and framing something are now the same kind of thing, on one list with one Ctrl+Z.
  Before, a redaction box had its own undo that only worked while you were in redaction mode; now the last mark
  comes back whichever tool made it, and you no longer have to remember which one you were in to know what Ctrl+Z
  will take. Boxes still do exactly what they did — applied to the pixels, no copy underneath, nothing that can
  travel separately from the image. There is no redo: a mark is one drag, and a redo that mistakenly brought back
  a box you removed would be a leak rather than an inconvenience.

- added: you can paint over what should not leave your machine. Press B on the selection surface and drag a box over
  anything you would rather the model did not see — a token in a terminal, a mail address, a password manager open
  behind the window — and Ctrl+Z takes the last one back. The blocks are applied to the picture itself, not drawn on
  top of it, so what is sent is the only version there is: there is no copy underneath and nothing that could travel
  separately from it. It goes to a terminal session the same way it goes to a chat one.

- added: you can take a whole window instead of dragging its edges. On the selection surface, press W and the window
  under your pointer lights up; click it and that is what you get, cropped out of the capture already taken — nothing
  is asked of the window itself, and it is never brought to the front. Where two windows overlap you get the one on
  top. On Wayland the surface says plainly that this is not something the desktop will allow, rather than offering a
  key that does nothing: telling one application where another's windows are is exactly what Wayland was designed not
  to do.

- added: the screenshot tool now has a selection surface of its own, the same on every platform. Pressing the key
  freezes the screen and puts it in front of you: drag out the region you want, nudge its edges with the arrow keys
  when it needs to be exact (hold Shift to resize instead of move, Ctrl for larger steps), press A for everything,
  Enter to take it and Escape to change your mind. The region you took last time is waiting for you the next time,
  because the same panel tends to get grabbed over and over. Where your screens do not line up into a rectangle,
  the area between them is not offered — those pixels were never on any screen.

- added: a session can now name itself. An agent that picks up a ticket can propose the ticket as its session's name
  at the same moment it sets its status line, so the row in the sidebar reads "AC-312" instead of "default - 3" without
  you touching it. It is a proposal, not a claim: a session you named yourself keeps the name you gave it, and the
  agent is told the name stood rather than being left to think it renamed something.
- added: a flow can name the session it starts. The Start session step grows a "Session name" field, so a flow opening a
  session on a ticket opens it already called after that ticket instead of opening "Claude — 14:22" and renaming it a
  step later. Leave it empty and the profile and the clock name it as before.

- added: a plugin can now hand a session something as it starts — environment variables its process runs with, asked
  for per session so the answer can depend on the project that session belongs to. It reaches every provider alike, so
  a plugin contributes once instead of once per CLI. What a plugin sets sits on top of your profile's own variables
  and underneath the cockpit's and the provider's, and a variable on a key the cockpit owns (an Anthropic credential,
  a nested-agent marker) is refused and logged by name — the same rule your profile's variables already meet.
- added: the GitHub Issues plugin uses it first. A session started under a project you linked to a repository now runs
  with `GH_REPO` set to that repository, so a `gh` command the agent runs inside the session is about the repository
  the project is tracked in rather than whichever one its folder happens to be. Nothing changes for a session without
  a project, or for a project you never linked to a repository.
- added: an agent can work in a terminal you already have open — the one where you logged into that server by hand, or
  set up the environment it needs. Tell it to use `zsh-5` and it asks for that pane; you get an Approve/Deny prompt on
  the pane itself, and only after you approve can it read what the shell prints and type into it. The point is that you
  watch it happen: every keystroke lands in the visible terminal instead of a headless shell you only see the result of,
  and you can type alongside it or press Disconnect — which interrupts whatever is running and cuts the access there and
  then. It reads only what is printed from the moment you approved, never the scrollback above it, so a token you echoed
  earlier stays yours. One agent at a time per pane, and only the shells you opened are on offer — a pane holding
  another agent session is never one of them, whatever you call it. Off until you switch it on in Options: while it is
  off the tools are not handed to sessions at all, so for an agent the whole thing simply does not exist.
- added: reading a terminal and typing into it are asked separately. An agent that wants to watch a build finish gets
  a prompt asking exactly that, and cannot type; if it later wants to run something, you get a second prompt that says
  it is a widening. So "let it look" is a thing you can say, and the bar on the pane says which of the two you granted
  — "Agent reading" or "Agent connected". Disconnect on a watching agent no longer sends a Ctrl-C, which would have
  landed on whatever you were running yourself.
- added: an agent can run one command and wait for it, rather than typing and guessing when it is done. It only works
  where your shell publishes the standard shell-integration marks — fish 4 has them, bash, zsh and PowerShell need the
  small snippet your terminal ships, on the remote host too if you are over SSH — because those marks are how the shell
  says "finished, and this is the exit code". They are invisible, so nothing appears in your terminal that you did not
  run. Where they are missing, or where a full-screen program like an editor or a pager has the pane, it refuses and
  says why instead of typing into what is open or guessing from a lull in the output. It is still your terminal: if you
  run something yourself at the same moment, the agent may be told your command's result — which you can see happen.
- fixed: what an agent reads from a terminal now says when it has been cut short. A pane keeps a bounded amount of
  output, so a long or noisy command can push its own earlier lines out of reach; the agent is now told that happened
  instead of quietly reporting a build as clean when the errors scrolled away.
- fixed: your keystrokes and an agent's can no longer interleave into a garbled command line. Both go into the same
  terminal from different threads, and nothing kept them apart.
- added: a plugin's settings dialog can have sections, navigated from a rail down its left side — the same one the
  cockpit's own Options dialog has. Autopilot's settings use it first: its four groups (CEO, Cost & tokens, Run safety,
  Templates) are now four pages you pick between instead of one scroll several screens long. Nothing moved and nothing
  was renamed, Save still saves the lot at once, and a plugin that does not offer sections keeps the dialog it had.
- added: tying a ticket to a session you already have open now labels it. The session says what it is working on
  under its name, and takes the ticket as its name if you never gave it one of your own — so the row you are looking
  for in the sidebar reads "AC-310" instead of "default - 3". A name you chose stays yours, whether you typed it when
  you started the session, renamed it later, or a flow set it. Works from the issues dialog, from the picker in a
  session's own header, and for both YouTrack and GitHub issues.
- added: a project can say where it is tracked. The project editor grows a "Where it is tracked" section with a field
  per installed tracker plugin — YouTrack offers the projects on your configured instances, GitHub Issues offers the
  repositories `gh` can see — so you pick from a real list instead of typing a tag and finding out later it was
  misspelled. Typing still works for a repository you have no read access to, the lists load without holding up the
  editor, and a project stays linked to something a plugin you removed used to understand.
- added: the YouTrack and GitHub Issues dialogs open on the project or repository the session's project is linked to,
  instead of on everything you have. Change the filter and it stays changed — the link decides where you start, not
  where you have to stay.
- added: plugins can put a field on the project editor and read back what the operator picked
  (`ICockpitHost.AddProjectField` / `GetProjectFieldValueAsync`). The plugin describes the field and supplies the
  choices; the cockpit draws the row and stores the answer, so every tracker looks the same in the editor. Two plugins
  may share one key where they mean the same thing, which is how the GitHub plugins both offer "which repository".
- added: a project information row can hold a credential. Tick "Secret" and the value is stored encrypted and
  scrubbed from backups the same way a profile's secret environment variables are, masked in the editor and shown as
  dots wherever the project appears — and never told to a session, whatever the sharing tick says. So the repository
  URL, the customer, and the deploy token for that customer can finally live in one place.
- added: a project's information rows can be handed to its sessions. Tick "Tell sessions" on a row and a session
  started on that project is told it as it begins — the repository it lives in, who the customer is, whatever you
  decided is worth its knowing — under your own labels, so nothing is rephrased for you. Off per row and off for every
  row you already have: a row stays yours to read unless you say otherwise, and a project that keeps notes does not
  quietly make every session's instructions longer.
- added: a project can carry whatever else belongs with it — rows you name yourself, with the value beside them: the
  repository it lives in, the customer's website, who to ask. No field per kind of information, so a new kind costs
  nothing: add a row in the project editor, type a label, type a value. The rows show on the project's card and in
  the projects window; a web address there is a link you can click straight through to, and a long value is shown
  shortened with the whole of it on hover, so one project cannot leave a card towering over its neighbours.
  Passwords and tokens still belong in a profile, which stores them encrypted — these rows are plain text.
- added: Kimi Code as a session provider, installable from the store. One `kimi` process runs for the whole
  session and speaks its protocol to the cockpit rather than drawing a terminal, so streaming text and
  thinking, tool calls with real Allow/Deny prompts, switching model mid-session, cancelling a turn and
  resuming an earlier one all behave the way they do elsewhere — and the MCP servers the cockpit hosts are
  handed to it like any other provider gets them. You install and sign in to the `kimi` CLI yourself: put an
  API key in the provider's settings, or use its login button; the cockpit finds the CLI on your PATH or at a
  path you pin. Three things it cannot do, and says so rather than pretending: a turn that failed looks
  exactly like one that succeeded (Kimi reports no difference), there is no allowance or cost to show so you
  get a context percentage and nothing else, and a profile's system prompt or a project's instructions cannot
  be handed to it — a session started with one says so in the transcript instead of quietly dropping it.
- added: take a screenshot straight into a session. A button beside the composer — and, if you switch it on,
  a key that works while the cockpit has no focus — opens your desktop's own screenshot picker (Spectacle or
  your shell's on Linux, the Snip overlay on Windows, `screencapture` on macOS), so dragging a region, picking
  a window and grabbing the whole screen all work the way you already know them. What comes back is attached
  to the composer as a thumbnail, not sent: you type the sentence that goes with it and send when you mean to,
  and you can attach several or remove one before sending. macOS has no desktop-wide key at all, so the button
  is the way there; the settings say that too instead of offering a key that will not fire.
- added: screenshots work in a terminal session too. A terminal carries text and nothing else, but the agent
  running in it reads a file perfectly well — so the capture is written to one and its path is typed into the
  prompt, ready for you to add the sentence that goes with it. Your clipboard is left alone: whatever you had
  copied is still there afterwards. A session whose provider cannot see images at all still says so on the button
  and in a notice, rather than taking the screenshot and losing it.
- added: a warning when two desktop-wide keys want the same key. Push-to-talk and the screenshot key are
  registered together, so one of them would otherwise simply stop working the moment you gave them the same
  key — with nothing anywhere connecting the two. Options names both features and the key while you are
  typing it, not after saving.
- added: the plugin SDK is downloadable. Every release and nightly now carries the plugin contract as a NuGet
  package plus a zip of the bare assembly, so a plugin can be built in its own repository instead of inside
  this one — the guide's "Getting the SDK outside the repo" has the two lines of project setup that takes.
- added: the cockpit now says when a session is running out of something — its context window filling up, or a
  usage allowance nearly spent — in a bar above the session, once when it crosses the line rather than on every
  refresh. Each provider decides what its sessions can run out of and when it is worth mentioning, so the numbers
  come from whoever knows what the window means; you can override them per provider, or per profile for one you
  use differently.
- added: pick a session up again later. When an allowance is spent, the warning offers to continue the moment it
  rolls over — the time is taken from the provider's own reset, and the prompt is yours to edit before it is set.
  Any session can also be scheduled by hand from its context menu. It is one prompt at one moment, nothing that
  runs on by itself, and a waiting resume says so on the session until it fires or you cancel it. A resume whose
  moment passed while the cockpit was closed is reported rather than fired hours late, and one whose session has
  since been closed is reported too rather than sent somewhere it does not belong.
- changed: the bundled Claude provider needs this version of AI-Cockpit — it reports what a session is running out
  of through host abstractions that older builds do not have.

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
- added: "New session" on an issue in the YouTrack and GitHub Issues dialogs. It opens the ordinary New-session
  dialog with the issue's prompt and its number already filled in, so you still pick the profile and the folder
  yourself; the issue is tracked against the session the moment it starts. Cancelling starts and tracks nothing.
- added: an issue's description is now shown the way the cockpit shows any other text — headings, lists, links
  and code as they are meant to look, instead of the raw `##` and `**` the tracker stores.

- added: a plugin can say which of its windows there should only ever be one of, so asking for it again brings
  the open one forward instead of opening a second. Needed now that these windows no longer hold the cockpit and
  two of them can be up at once. The plugin decides, not the cockpit: all the cockpit is handed is a title, and
  two plugins can title different windows the same — YouTrack and GitHub Issues both call theirs "Track an issue
  in this session" while meaning different sessions. Plugin authors: `ShowDialogAsync` takes a
  `singleInstanceKey` overload, and a plugin using it needs a cockpit of 0.9.0 or newer.
- added: a scheduled resume whose session has since closed can now bring it back instead of only reporting it
  undelivered. If a crash restart brought the pane back as a restore offer that nobody had accepted yet, the
  resume that comes due reopens that earlier conversation itself and sends its prompt — silently, because a
  resume is scheduled precisely to run while you are away, and a toast says so when it happens. When there is
  nothing to reopen — the pane was closed on purpose, the provider keeps no resumable conversation, or nothing
  was ever brought back this run — the resume reports itself undelivered exactly as it always has. Terminal
  sessions are not covered yet: a terminal's connection comes up too late for this to trust safely, so a due
  resume against a terminal pane still reports undelivered rather than risk starting one for nothing.
- added: a plugin author running a DEBUG build from a dev checkout now gets a one-click reload after rebuilding
  a plugin under `plugins-dev` — a toast offers **Reload**, which brings the new build in and restarts, instead
  of a manual zip/install round-trip for every change. It only refreshes a plugin you already installed, and only
  on a dev checkout; a release build behaves exactly as before.
- added: a profile can now set its own default session Kind (SDK or TTY), so the New-session dialog's Kind toggle
  opens on the route you actually use it for instead of always starting on TTY. Only offered for a profile whose
  provider can run both routes; a provider with none (a local model, or a plugin that never registered a terminal
  route) shows "SDK-only" instead of a choice that could never take effect, and always starts SDK regardless of
  what is set here. Per session, the toggle in the New-session dialog still has the final word.
- added: a plugin can now contribute an MCP server that requires an OAuth sign-in, and one that only exists for a
  particular project — both previously only possible for a server added through the MCP-servers dialog itself. A
  plugin's OAuth-protected server now goes through the same loopback-browser sign-in and token refresh a
  dialog-configured one gets, instead of silently connecting with no authentication and no tools; and a project-
  scoped contribution shows up only for sessions started on that project, the same way the dialog's own per-project
  servers already do.
- added: a Memory row now asks two separate questions instead of one. The first dropdown is the kind of place the
  memory lives — a folder, or Depot — and when you pick a plugin's entry a second line appears asking which of its
  servers you mean, with a "Servers…" button beside it that opens that plugin's own settings. Have no servers set up
  yet? The line says so ("No Depot server configured yet") and offers the same button, so the way to add one is in
  front of you instead of on a screen the dialog never named.
- added: the spoken turn-start acknowledgement now plays in a terminal (TTY) session too, not only in a chat
  session — say something with auto-submit on and the cockpit answers back before the agent starts working,
  instead of leaving the conversation silent. It stays quiet under the same conditions as before: read-aloud off,
  or the acknowledgement mode set to Off.

### Changed

- changed: a profile whose Default kind is TTY no longer shows the "Default view" picker. The reading level only
  applies to an SDK session, so on a TTY profile it read as a setting that did nothing — the New-session dialog
  already hid the same row for a TTY session. Flip the profile to SDK and the picker comes back straight away. A
  provider that has no TTY route at all (Ollama, LM Studio, a plugin that registered none) always starts SDK, so
  it keeps showing the picker.
- changed: the actions on a worktree row are icons now instead of labels, leaving the row room for the branch,
  its state and its owner. Each name moved to the front of the button's tooltip. Release comes first, and only
  appears on a row where releasing is actually possible — the other two stay put while disabled, because their
  tooltip tells you why they cannot run right now.
- changed: the Managed worktrees dialog now names the session that claims a worktree — "in use · claimed by
  DEP-158" instead of the anonymous "in use · claimed by a pane" — and says whose it was after that session
  closes or crashes ("session gone · was DEP-158"). Falls back to the previous, unnamed wording when no name is
  known.
- changed: the GitHub Pull Requests plugin's always-visible list under the session list is now a left-menu button
  with a live badge — your own open PR count next to how many are waiting on your review ("3 / 2"). Clicking it
  opens the same dialog listing every open PR as before; the always-visible list still exists as the Dashboard
  widget, for a workspace given over to it. The "how many pull requests inline" setting is gone with the list it
  configured — the widget already has its own, per-pane count.
- changed: the usage-history trail no longer grows without bound. Once it reaches 8 MB it rolls over to a single
  backup file, so a long-lived install no longer accumulates hundreds of MB of usage records over time. Recent-usage
  views still read seamlessly across a rollover; the consent and delegation trails are unaffected and keep every
  record forever, as before.
- changed: a text field or dropdown you just clicked into no longer draws the same bright accent ring as one you
  tabbed to with the keyboard. The full ring is now reserved for keyboard focus, so it marks where the keyboard
  actually is rather than the last thing the pointer touched; a clicked field still gets its own quieter edge so
  it doesn't look untouched, just not as loud as the keyboard signal.
- changed: the accent colour is a darker blue. The white button label it carries measured under the accessibility
  floor for readable text against the previous shade; the new one, and its hover and pressed tints, all clear it.
  This is a deliberate move away from the accent shown in the app's earlier mockups, made for readability. The
  quieter ring on a field you clicked into is a tint of that accent, so it moved with it — it had been left behind
  on the old hue, which made the two blues on a focused field disagree by a shade nobody chose.
- changed: signing in to an MCP server in the MCP servers dialog no longer requires saving it by hand first. Sign in
  is offered as soon as the row itself is filled in — a name, plus a command or a URL — and clicking it now saves
  the whole dialog before it opens the browser, so a server you just added, or one you just renamed, signs in on
  the first click. That save is real: Cancel afterward will not undo it, and every other server you have changed in
  the dialog goes out with it too. Signing out is unaffected by an unsaved rename — it withdraws whatever access is
  already on file, under the name the server was last actually saved as, regardless of what you are mid-typing over
  it.

- changed: the Autopilot side-menu entry and workspace title now read "Autopilot" instead of "Autopilot (CEO)" —
  the suffix didn't distinguish anything and only added noise.
- changed: Autopilot now plans its review gates to spend verification where the verdict is. A gate reviews, its
  findings get fixed, and it reviews again until a round finds nothing — the rounds in between are asked to build
  incrementally and run the tests around the change, and only the round that finds nothing does the whole-project
  build with warnings as errors and the complete test suite. That last round is deliberately unchanged: it is what
  catches a fix that broke something outside an earlier round's test selection. In the first measured run a single
  item cost eight full build-and-test cycles, two of which carried a verdict.

  This lives in the plan the run's CEO writes rather than in a check the cockpit enforces. Each round is asked to
  report what it actually built and ran, and the same requirement goes into the gate's acceptance — so what a gate
  claims about its final round is something you can read back instead of assume.

- changed: Autopilot's code-review and security-review gates now read a finished diff at the same time instead of
  one after another, each on its own throwaway copy of the work so the two can never write over each other. When
  either finds something, one shared step applies both gates' findings before they check again; a gate that comes
  back clean the first time is done and never waits on the other.

- changed: the release page now tells you what your own machine is about to do about an unsigned download, for all
  three platforms rather than only macOS. Windows SmartScreen calls the publisher unknown, a downloaded AppImage has
  no executable bit, and Gatekeeper reports a perfectly good app as damaged — each refusal looks like a broken
  download and none of them say what to do next.

  **If you install the cockpit on Windows, there is a one-time step.** The old installer put it in `Program Files`;
  the new one installs per-user and does not adopt that copy. They are two separate installations, so the old one
  would go on running and never update — quietly, since it goes on checking and finds nothing it can reach. Run the
  new Setup once and remove the old entry from Settings → Apps. Nothing of yours moves: settings, plugins, projects
  and logs all live in `%APPDATA%\Cockpit`, beside neither installation.

- changed: the cockpit now looks for a newer build in the same place an update would come from, instead of asking
  GitHub's release list separately. Those were two answers to one question and free to disagree — a banner offering a
  build the updater could not see, or the other way round. There is one now.

  What it looks for is a feed named for your platform and your channel together, so a release carrying Windows, macOS
  and Linux packages side by side can never hand you somebody else's.

  Two things you may notice. The status line no longer prints the date a build was published: the feed is a list of
  packages and does not carry one, and an invented date is worse than none. And a copy that was not put there by the
  cockpit's installer — a checkout, a tarball, your distribution's own package — can no longer look for updates at
  all, where before it could look but not install. It now says so instead of quietly reporting nothing, and the
  Updates tab no longer promises to tell you about new builds it cannot see.

- changed: a nightly build stays on nightlies. If you had never picked a channel, the cockpit assumed stable — so a
  nightly you had downloaded on purpose, started for the first time, would offer you the latest stable as its "update".
  That is a downgrade wearing an upgrade's clothes. With no choice on record the channel now follows the build you are
  actually running, and a nightly looks for nightlies.

  Picking a channel yourself still wins, and now it sticks: once you touch that setting it is your decision and the
  build no longer overrides it. Changing anything else in the Updates tab leaves the channel alone — including in the
  first moments after startup, while the settings file is still being read.

  **One-time note if you have been running the cockpit already.** The old setting wrote a channel back on every start
  whether or not you had ever chosen one, so a stored value proved nothing about what you wanted — and treating it as
  a choice would have left the problem above in place for everyone who already had a config. It is read once as "not
  chosen", and the build decides again. If you had deliberately opted into nightlies on a stable build, tick that box
  one more time; it is permanent from then on.

- changed: while the plugin store is installing something, everything that changes what is installed is switched
  off rather than merely looking that way — enabling, disabling, removing, moving a plugin up or down the left
  menu, and adding or removing a workflow template. The layer the store draws over its catalogue while it works
  never stopped the keyboard, so those buttons were out of reach of the mouse and one Tab and a space bar away
  from doing exactly what they always did, halfway through an install.

  That layer has stopped blocking the mouse in turn. It says what the store is doing and no longer pretends to
  be a lock, so everything that changes nothing keeps working while you wait: reading the catalogue, opening a
  plugin's settings, switching between the lists in the sidebar, following a plugin's homepage or repository.

- changed: the header every dialog wears is lighter. Its name sat at heading size with a lot of room around it, which
  on a short dialog like Set status took two fifths of the window before you reached the first control, and on About
  left the header shouting over the dialog's own content. The name, the line under it and the room around them have
  all come down; the band and the hairline under it are unchanged, so a dialog still reads as having a head and a
  foot. Nothing was removed: every dialog keeps its name and its close button.

  One header, so this lands on all of them at once — including the ones a plugin puts up.

- changed: the cockpit is called **Wispslate Cockpit**, and now carries its own mark. The title bar shows the mark
  and the name — the maker's half at full strength, the product's half a step behind it — where an accent dot and
  the old name used to stand. That bar is the only place the main window states the name at all; the window title
  your taskbar reads, the tray, the About dialog and the app icon follow the same one.

  Everywhere else the cockpit goes back to calling itself "the cockpit", the way it already did in a few places:
  a settings page that names a product in every second sentence is advertising at someone who has already installed
  it. So "Restart the cockpit to activate it", not the brand again.

  Nothing you have configured moves. The settings directory, the config keys, your profiles, the worktree and
  container names and the repository all keep the names they have, so an existing install carries over untouched —
  the change is what you read, not what the machine reads.

- changed: the shortcut that zooms a session pane to full width is now Ctrl+Shift+M. It was Ctrl+B, which never
  arrived while a terminal had the keyboard — the shell claimed it first, as its tmux prefix or as
  backward-char — and a zoomed pane is exactly the moment a terminal has the keyboard, so the shortcut was
  unusable where it was meant to be used. Two modifiers get it past a focused terminal, the way the session and
  workspace switches already do. Not Ctrl+Shift+Z, which would read better: that is the platform's second Redo
  chord, and a two-modifier shortcut is taken before a text field sees it, so it would have eaten Redo in the
  prompt box. Not Ctrl+Alt+a-letter either — AltGr arrives as Ctrl+Alt, so on an ISO layout that combination is
  how you type a character (the Ctrl+Alt+arrows that move between panes are unaffected: an arrow types nothing).
  If you had ever saved your shortcuts, the old Ctrl+B is carried over for you; any other gesture
  you set is left as it is. Note that the carry-over goes by the gesture, not by when you set it, so binding zoom
  back to Ctrl+B does not stick — it is moved again on the next start, which is deliberate: Ctrl+B is a gesture
  that cannot reach the cockpit from a focused terminal.

- changed: the model list offered when you start a Claude session names a family rather than a release — "Opus", not
  "Opus 4.8". The value behind it is the CLI's own alias, which follows whatever that alias resolves to today, so a
  label carrying a version number could only ever go stale while the session it started ran on something else. The
  field stays free text, so a specific model or snapshot can still be pinned by typing it.

- changed: **Fable** can be picked from that list. It was reachable before only by typing it in by hand.

- changed: scroll bars follow the theme. The track, the thumb and the square where two bars meet were still drawn in
  the greys the underlying toolkit ships with, on nearly every scrollable surface in the app. The thumb sits several
  steps lighter than the groove it slides on, so it stays visible rather than merely correct.

- changed: the plugins are painted in the same colours as the rest of the cockpit. The repaint reached the app but
  not the plugins that draw their own surfaces, so several were still finished in the old orange: the prompt
  palette's search spark, the thin progress line above an issue list, the stripe down a workflow step. Buttons,
  labels and boxes inside a plugin now take their shape from the same place the app's do, so a plugin's dialog no
  longer reads as a window borrowed from another program.

- changed: the workflow canvas is retuned. A step's leading stripe says what kind of step it is, and the plain one
  used to be a muted blue — fine beside an orange accent, and a near-copy of the accent once that turned blue, so a
  trigger stopped standing out among ordinary steps. It is a neutral slate now. The dotted background, the wire
  labels and the ✕ that removes a connection follow the theme as well, instead of each holding a colour of its own.

- changed: buttons, text fields and dropdowns are drawn to one shape. A field and the picker beside it are now the
  same height with their text on the same line, so a form of labels and controls lines up instead of stepping. A
  field also sits a shade lighter than the window rather than cut into it, which reads as something you type in.
  Buttons answer the pointer by brightening their edge and only move their surface when you actually press them, so
  a row of them stays still while you cross it. A dismissing button — Cancel next to a confirm — carries no chrome
  until you hover it, which leaves one obvious answer in a dialog's footer.

- changed: a setting that is on or off is now a switch you can see the state of at a glance. "Isolate in a git
  worktree", in both the new-session dialog and the project editor, is a track the knob slides along instead of a
  tick box. Lists of options you pick from are still checkboxes, because a list is not a set of switches.

- changed: every dialog now says its name once, at the top, in a bar of its own. The name is at heading size with —
  where the dialog has one — a line under it saying what the dialog is for, so opening one tells you what you are
  looking at before you read a single field. Several dialogs used to print their name a second time inside their
  content, at a different size, directly under the bar that already said it; two others, the project editor and
  "Resume later", showed the operating system's title bar while every other window showed the cockpit's own. The
  main window keeps its single compact line, now with the accent dot in front of it.

- changed: the cockpit is blue. The accent that runs through buttons, links, focus rings and the active session's
  border moved from the old orange to a blue, and the surfaces behind it went a shade deeper, so a panel now reads as
  sitting on the window rather than beside it. A session that is busy is marked in cyan instead of blue, because blue
  is now the colour of everything you can click — the other status colours (amber waiting, green done, purple working
  in the background, red failed) are unchanged and still mean what they meant.
- changed: colours that had been written into individual screens — the markdown in a reply, the microphone level
  meter, the badge on an isolated session, the green and amber status lines in a provider's settings — are now taken
  from the theme like everything else. They were the places that would have stayed orange while the rest of the
  window turned blue.

- changed: taking a screenshot on macOS no longer opens the system crosshair. It reads every display straight away
  instead, so what comes back is the whole desktop rather than a region you had to drag first — the selection
  becomes the cockpit's own. Screen Recording permission is still asked for once by macOS; until you grant it,
  nothing is captured, and the cockpit now says that is what may have happened rather than assuming you cancelled.
- changed: taking a screenshot on Windows no longer opens the Snip overlay and no longer touches your clipboard.
  The cockpit used to launch Windows' own snipping tool and then watch the clipboard for a picture that had not been
  there before, because the overlay reports nothing back. That guesswork is gone, and with it the two-minute wait, the
  clipboard you had something else on being overwritten, and a snip that happened to match what you already copied
  being read as a cancel. It now reads the screen directly, in a moment.
- changed: taking a screenshot on Linux no longer opens your desktop's own screenshot dialog. Pressing the key used
  to hand you whichever form your desktop ships — on KDE an Area dropdown, a Delay spinner and a Take button, three
  clicks before anything was captured, and a different UI on every desktop. The cockpit now asks the desktop for the
  screen itself and gets it in a moment, with your consent asked once and remembered. A desktop that has no
  screenshot support at all now says so on the button instead of failing when you press it.
- changed: every link the cockpit opens for you — in a reply, in the terminal, on the About screen, in the plugin
  store, and the release page behind the update banner — now goes through one place, so all of them apply the same
  rule: only a plain web address is handed to your browser, and a browser that will not start never takes the
  cockpit down with it. The release page was the one that had no such check.
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
- changed: the YouTrack and GitHub Issues dialogs open wider, with a divider you can drag between the list and
  the details. The details side now leads with the issue itself — title, its state and repository as chips, a
  link button, and the description — with the actions in one fixed row that no longer rearranges itself
  depending on what happens to be installed or running. The prompt that will be handed to a session sits under a
  "Prompt preview" you open when you want it, rather than taking up half the panel on every issue you click.

### Fixed

- fixed: "Track an issue in this session" now shows only the issues from the YouTrack (or GitHub) project your
  project is actually linked to, instead of every project on the instance once that instance had no default
  project of its own. The full YouTrack and GitHub Issues dialogs already respected the link; the session picker
  quietly did not.
- fixed: clicking the tray icon after the cockpit's window had really closed took the whole app down. A closed
  window cannot be shown again, and the tray's Show did it anyway — from an event handler with nothing to catch
  the failure, so the process simply died. The cockpit now forgets a window once it is closed, which is what the
  tray click, the tray menu's Show and the screen lock each already checked for.
- fixed: the log no longer throws away the run you want to read. It is emptied at every start so the live one
  stays readable, which also meant starting the cockpit again to find out why it had vanished was itself what
  destroyed the evidence. The run before this one is now kept beside it as `cockpit.log.previous` — exactly one,
  overwritten each start, so nothing accumulates.
- fixed: a cockpit that is simply gone the next time you look now says so in its log. Every way the app itself
  ends — the window closing, Quit from the tray, Windows ending the session, the teardown that follows — writes a
  line as it goes, and the run starts with one naming its version, process id and arguments. A log that ends in
  ordinary activity with none of those lines after it means nothing inside the app asked to stop, which is the
  difference between the cockpit closing and the cockpit being ended from outside.
- fixed: a row in the Managed worktrees dialog no longer draws its owner and path underneath the buttons on its
  right — both lines now shrink and trim to the room the row actually has, with the full text on hover, so a long
  session name or a deep path cannot push anything out of view. The dialog is also wider, and keeps a minimum size
  so its buttons stay reachable when you make it smaller.
- fixed: an agent can now remove a worktree it made for itself, through its own "isolate this task" tool, even while
  its own session is still open — the guard that (rightly) refuses to remove the worktree a session is actually
  running in was refusing that case too, which made the tool's own "clean up when a task is done" description
  impossible to follow. The worktree a session runs in stays off limits either way, including to that session's own
  agent.
- fixed: a worktree an agent created for itself mid-session (through its own "isolate this task" tool) is now
  released when that session's pane closes, the same as a worktree the New-session dialog made. It used to sit on
  disk until the next cockpit restart, because closing a pane only ever released a worktree the pane itself had
  created.
- fixed: removing a worktree in Cockpit now really means it is gone from disk, not just off the list. A folder git
  can no longer recognise as a working tree — its own administration corrupted or pruned out from under it — is now
  deleted for real once Cockpit can prove nothing is lost: no uncommitted or untracked files, and no commit that
  exists only there. When that cannot be shown, the folder is left in place exactly as before, with the same notice
  explaining why.
- fixed: an agent can now clean up a worktree left behind by a session that has since crashed or closed, instead of
  only ever being able to touch worktrees from its own session. A worktree whose owning session is still running is
  still refused, as is one whose liveness cannot be determined at all.
- fixed: the log no longer claims every minute that the dictation worker was killed for being idle. It said so
  whether or not a worker was still running, so a single quiet stretch filled the log with the same line over and
  over — and the "5 min" in it stopped being true after the first one. The message now appears once per actual
  shutdown and states how long the worker really sat idle.
- fixed: when the dictation worker dies, the failure now carries the last lines the worker wrote about itself
  instead of only "the process exited unexpectedly". Model, runtime and out-of-memory complaints reach the log
  rather than a pipe nobody read, so a dictation that falls back to the slower CPU backend can be explained
  afterwards instead of guessed at. The same goes for the calibration run.
- fixed: a transcript cleanup that was cancelled because the recording itself was thrown away no longer reads as
  a warning. It is a normal outcome, and dressing it as a fault made real failures harder to spot.

- fixed: the project editor's server dropdown no longer requires closing and reopening the dialog to notice a
  server you just created (or removed) through the "Servers…" button. It now shows the new server immediately,
  keeps a selection you already made when other servers change around it, and falls back to no selection rather
  than a silently stale one if the server you had picked disappears.
- fixed: an MCP server you sign in to with a browser (Depot) no longer disappears from a running session when its
  access token expires. The session is now pointed at a local address the cockpit answers, and the cockpit puts a
  freshly renewed token on each call as it passes through — so the token's lifetime is no longer the session's
  lifetime, and a sign-in that has genuinely run out answers the call with a readable reason instead of taking the
  server and all its tools out of the session for good. As a side effect no access token is written into a session's
  config file any more.
- fixed: a session could start on a token that was minutes from expiring and lose that server minutes later. A
  session now starts on a token that will outlast the sitting, renewing it first when it will not. If a server's
  tokens are simply too short-lived for that and the local address could not be set up either, it is left out with
  the reason rather than added and lost while you are working.
- fixed: a server rejecting the cockpit's credential — a sign-in revoked at the other end, or two sessions racing to
  refresh — used to leave that credential in place, so every later call presented it again and the server was gone
  for the rest of the session. A rejection now renews the credential and sends the call once more.
- changed: when a server cannot be used, the cockpit now shows you a notification saying what is wrong and what to
  do about it — once, when it happens, rather than repeating on every call. A server that is simply unreachable no
  longer sends you through a sign-in that cannot help, a server you never signed in to is not described as one whose
  sign-in expired, and a server whose tokens are too short-lived says exactly that instead of asking you to sign in
  again for another one just like it.
- fixed: several sessions starting at once could each renew the same sign-in, which on a server that issues one-use
  refresh tokens can invalidate the authorization outright. One renewal now runs at a time and the others use its
  result.

- fixed: a session no longer reads as finished while work it started is still running. A sub-agent that outlives the
  turn keeps the session showing that it is working in the background, and a backgrounded shell holds the "session
  finished" notification back until it ends. The status used to flip to done the moment the main turn did, then
  flicker between working and done for the rest of it.

- fixed: opening the GitHub Issues dialog from a project that is linked to a repository now actually opens on that
  repository. It always fell back to showing every repository instead, so the link made no difference to what you saw.
  Narrowing the list by label no longer hides that repository either, so the preselection cannot be sidestepped.

- fixed: filtering GitHub issues by a label whose name contains a comma returned nothing. GitHub reads that
  parameter as a list, so the name was split into two labels that do not exist. Such a label is now matched locally
  instead, which cannot look beyond the first page of results — the list says so when it is capped.

- fixed: the YouTrack dialog built a broken query for a status field whose name contains a space, such as a board
  using "Kanban State". Only the value was quoted, so the field name fell apart and the filter quietly returned the
  wrong set.

- fixed: renaming an MCP server — or a Depot connection — no longer costs you its sign-in. The token was filed
  under the server's name, so saving a new name left the old sign-in behind: unreachable, still holding a refresh
  token, and the row reporting "sign-in needed" over a credential that was sitting right there. A server is now
  identified by something you can't edit, so the name is back to being just a label. Two servers pointing at the
  same host are covered too: swapping their names used to hand each one the other's credential, which meant a token
  could be presented to an endpoint it was never issued for. Nobody has to sign in again after updating — sign-ins
  you already have are carried over to the new arrangement on first launch.

- fixed: command output in a Linux terminal pane no longer comes out as a staircase. `ls`, `git status` and anything
  else that ends its lines with a plain newline was drawn one row down but never back at the left edge, so each line
  started where the previous one ended and long ones broke mid-word. The pane's terminal was being created without the
  line disciplines a terminal is supposed to have — echo, line editing, Ctrl-C as a signal and the newline translation
  were all switched off, and interactive shells only masked it by configuring the terminal for themselves. Windows was
  never affected.

- fixed: a pane's conversation no longer goes missing when the cockpit restarts. Panes came back after a crash, but
  resuming one was rarely on offer — starting a session wrote a blank conversation over the saved one, so the thread
  was still on disk yet unreachable, and every ordinary restart cost you the same thing. A saved conversation now
  survives any restart, crash or clean, and keeps surviving until the newly started session reports one of its own,
  so reaching for "Start fresh" by mistake no longer throws the old thread away. It is let go only when the new
  session runs under a different profile or working directory, where resuming it would have meant reopening a
  conversation somewhere it never ran.
- fixed: clicking "Choose…" next to a Depot memory row to pick a project could fail with `No enabled MCP server
  named "Depot: Depot"` even while signed in and with connections configured — picking a project (and the same
  reachability check a typed slug already got) now reaches a plugin's own server the same way the picker's
  acceptance check already did, without waiting for the project to be saved first.
- fixed: a Codex TTY session's status dot no longer gets stuck reading "idle" for the whole session — it now
  moves between busy and done as Codex actually works, the same as Claude's already did. The read-aloud feature
  also no longer speaks Codex's in-progress commentary, only its settled answer.
- fixed: a worktree whose repository folder had disappeared (a rare case, but one with no workaround once it
  happened) could no longer be removed from Managed worktrees — Remove failed with "Could not run 'git' — is it
  installed and on PATH?", a diagnosis that sent you checking your git install for a problem that was never there.
  Remove now drops it from the list; its folder is left on disk untouched and you're told so if it still held
  anything, so "removed" is never mistaken for "discarded". Reattaching such a worktree to a new session no longer
  fails either.
- fixed: an OAuth-protected MCP server (Depot, for example) used to need a fresh browser sign-in about once an
  hour, even at the start of a brand-new session — the server never handed out a refresh token, so there was
  nothing to renew with once the access token expired. The cockpit now asks for a refresh token whenever the
  server supports one, and one sign-in lasts until you sign out yourself. A server that never advertises the
  richer scope keeps working exactly as before. An optional per-server "OAuth scopes" field (next to the OAuth
  authority/client id fields) lets you override what gets requested for a server with its own requirements.
- fixed: two cockpit instances running at once (a development build alongside a packaged one, for example) no
  longer silently fight over the same global hotkey. Before this, both could report their push-to-talk or
  screenshot key as "armed" while only one of them — or neither, on some desktops — actually reacted to it, and
  closing the instance that did required restarting the other to get the key back. The cockpit now claims a
  hotkey for itself before arming it; an instance that loses that claim shows a toast saying another cockpit
  instance already has the key, rather than pretending it worked, and picks the key back up on its own the
  moment the other instance releases it — no restart needed.
- fixed: a delegated (headless) Claude session could end up with more MCP servers than intended — narrowing the
  request to fewer servers, or to one the profile advertised but could not actually mount, used to drop the
  `--mcp-config` flag entirely, and the `claude` CLI then fell back to its own full user/project configuration
  (including any claude.ai account connectors you have signed into, like mail or other write-capable services)
  instead of the empty set the narrowing actually produced. A headless session now always gets exactly the
  servers it resolved to, never more — an interactive session you drive yourself is unaffected and keeps
  layering the cockpit's servers on top of your own configuration as before. A profile that advertises a server
  it cannot actually reach now says so in the log instead of silently handing you fewer tools than promised.
- fixed: the window-picker test suite no longer fails depending on which windows happen to be open on the
  machine it runs on — it stopped asserting that no window is ever reported larger than the screen, which
  Windows never guaranteed in the first place (a window dragged partly off-screen legitimately has bounds
  sticking out), and relies instead on a deterministic check that an off-screen window is still cropped and
  offered correctly.
- fixed: several widgets (a markdown link, the mic level meter, a handful of plugin loading bars and canvas
  controls) had kept the accent's old, lighter shade in their own fallback colour instead of picking up the
  darker one the theme moved to — so they now draw the same accent as the rest of the cockpit again.
- fixed: a Claude SDK session started with no explicit model (Auto/default) showed an empty Model live-control,
  while effort and permission mode both showed theirs. The control now seeds itself from the model the session
  actually started under, reported once by the provider at connect time — it never overrides a model you
  picked yourself, and never talks a choice back to the provider that it did not make.
- fixed: an unticked checkbox now sits on the cockpit's own dark surface with its own hairline border, instead
  of a barely-there transparent box outlined in the operating system's default translucent white.
- fixed: a progress bar's groove — the plugin store's install/update bars and the voice overlay's model-download
  bar — now sits on the cockpit's own sunken-inset colour instead of a translucent white left over from the
  framework's default theme.
- fixed: a terminal session's and a local model's own tool loop now drop their per-session MCP credential when they
  end, the same way every other session already did. Left running for a long time without this, a cockpit would keep
  a growing pile of dead credentials in memory for panes that had long since closed.
- fixed: a plugin section's chevron, title and header border now fall back to the cockpit's own faint-text,
  primary-text and hairline colours if the theme lookup ever misses, instead of a plain grey or white that
  was never part of the palette.
- fixed: the Worktrees dialog's row buttons (Open folder, Reattach, Remove) and a code block's Copy button
  now actually dim to the theme's secondary/faint ink at rest and brighten on hover — three hover rules
  tried to set that colour and never reached the label, so all three looked like plain, undimmed text the
  whole time.
- fixed: the planning brief told the planner that a profile's model list ran from lighter and cheaper to heavier and
  more capable, while it actually ran the other way round. A planner following that instruction to "pick the cheapest
  model that can do the job" was reading the expensive end of the list — in a measured pilot run, 89% of every token
  spent went to the second-dearest of the four models on offer and the cheapest one was never reached for at all. The
  roster now follows the order the provider itself declares, and where a provider declares no order the brief says so
  rather than inventing one.
- fixed: the cost shown for a session was far too high, because the session's whole spend was counted again on
  every turn. The figure the CLI reports alongside each answer is what the session has cost so far, not what
  that one answer cost, so adding those figures up charged every earlier turn a second time — a two-turn
  session read about double its real cost, and a nine-turn one several times over. The meter beside the
  session status, its hover breakdown, and the usage trail written to disk now follow the newest reported
  total rather than a running sum. Token counts were never affected and are unchanged: those really are
  per-turn, which is precisely why a plausible token count could sit next to a wrong amount without anything
  looking odd. Figures already written to the usage trail stay as they are; the correction applies from here
  on, so a session you have open now will read differently from one recorded yesterday.

- fixed: a managed worktree whose folder was emptied but never deleted can be removed again. When a removal cleared
  the checkout but could not delete the folder itself — a handle Windows was still holding, say — what stayed behind
  was a row for a worktree that is no longer one. The panel called it "Uncommitted changes" about a folder holding
  nothing, "Clean up finished" skipped it because the folder was still there, and Remove failed every time with
  git's "is not a working tree", so the row could never be got rid of. Such a row now reads "No working copy", the
  sweep takes it along with the ones whose folder has gone, and removing it drops the entry and clears the empty
  folder out of the state directory. Closing a session no longer creates one either: teardown drops the record
  instead of keeping it for review. Anything still on disk in such a folder is left exactly where it is, and the
  branch survives the removal as it does every other one.

- fixed: the lock screen and an empty Sessions workspace still stood in for the brand with a tile carrying the
  letters "AI". Both now show the same mark the title bar and the About dialog carry.

- fixed: the working copy a delegated task is editing in is no longer treated as abandoned. A task you hand to
  another profile runs without a tab of its own, and the cockpit counted "which sessions are running" from the tabs
  alone — so a git worktree such a task made for itself looked like one whose owner had gone. Its row in the
  managed-worktrees panel showed as free: Remove was offered on it, "Clean up finished" swept it whenever it
  happened to be clean at that moment, and a new session started in that folder took it over. Each of those would
  have pulled the working directory out from under a sub-agent that was still running there. All three now see the
  task as running and leave the worktree alone, the same way they already did for a session with a tab.

  And once the task is done, the worktree is handed back there and then — when you stop it, when it runs past the
  time its profile allows, and once its session has sat unused long enough to close — instead of waiting for the
  next start of the cockpit to notice. What happens to it is what happens when you close a session tab yourself: a
  worktree with nothing left in it goes, taking its branch when that work has already landed on the branch it was
  forked from, and one that still holds uncommitted work is kept and listed for review.

  Two things follow from this that are worth knowing. A delegated agent can no longer delete its own worktree
  part-way through its task — the cockpit now counts that task as running, so worktrees it makes and finishes with
  along the way stay until the task ends, when they are cleaned up together. And a task the cockpit reports an
  error on keeps its worktree until the next start: that report does not always mean the session has ended, and
  removing a working copy out from under something still using it is the worse mistake of the two.

- fixed: a dialog can no longer put its own buttons out of reach. Configuring an MCP server whose name clashed with
  one the cockpit already runs left Cancel and Save off the right-hand edge of a window that could not be resized, so
  the server you had just filled in could not be saved. The cause was never the height it looked like: a message
  whose length comes from your data shared a column with the buttons, and a column does not clip, so the buttons
  were laid out past the edge and the window cut them off.

  Every dialog was then swept for the same shape, with each text it lays out driven far past any length it would
  really carry, and three more had it. Managing profiles pushed Cancel and Save out and squeezed Remove to
  nothing when a status message named several profiles, and did it again when asked to confirm removing one. The
  list of delegated tasks put Stop — the only way to end a running task — past the edge for a long enough task
  name. And a plugin asking you to confirm something could carry its own button off the dialog with a long
  enough label. Those texts now wrap and shorten, with the whole message on the tooltip, because the text is the
  one thing on such a row that can afford to give way.

- fixed: dialogs now fit the screen they open on. A dialog is centred on the cockpit with nothing to drag it back
  by, so one designed for a desktop opened with its buttons past the bottom edge of a 768-pixel laptop panel and
  there was no way to reach them. Three of them already shrank to fit — the plugin store, managing profiles and
  managing stores — while the rest, the project editor at 760 tall among them, did not. All of them do now,
  including the windows a plugin opens.

  A dialog that sizes itself to its content is capped rather than resized, since there is no size to shrink, and
  the form inside it scrolls. That settles what used to happen when something long arrived in one: a git error a
  page long in the clone dialog, a plugin's install path in the consent prompt, or a caller's explanation above
  the password boxes would grow the window off the bottom of the screen. A dialog with a size of its own is
  shrunk to fit and keeps no cap, so you can still drag it larger than the screen if that is what you want.
- fixed: Autopilot's "Needs you" state could leave you with no way to answer. With more than one run active, the
  pane could keep showing a still-running run's session while the badge lit up for a different run waiting on you —
  a notice with no path to the run it was about. The badge now takes you to the run that needs you, and clicking it
  again steps through the others if several do at once. The blockade panel also gets its own scrollbar, so a longer
  question — several numbered options plus advice — no longer overflows the pane and pushes the answer box out of
  reach.

- fixed: the transcript no longer stops following the newest message for no reason you did anything to cause. Showing
  the "Thinking…" indicator, the "starting" banner, a usage warning or a pending-resume notice all resize the
  transcript's own visible area without adding a single message to it — and that resize alone was read as if you had
  scrolled up by hand, since it moves the same numbers a real scroll does unless the box itself is checked too. A turn
  with several tool calls flips the "Thinking…" indicator on and off many times over, so the transcript could quietly
  give up on the newest message well before the turn had finished, with nothing you did to explain it. It keeps
  following now regardless of how many times those rows come and go; scrolling up on purpose still pauses it and
  scrolling back down still resumes it, exactly as before.

- fixed: the cockpit's audit trails are no longer readable by every account on the machine. The files recording
  which commands you approved, which prompts sub-agents were given, what one agent sent another, and what your
  sessions spent were created with whatever the system's default permissions said — on a stock Fedora, that means
  world-readable. They hold free text, so a command you approved or a prompt you sent could name a token, a path or
  a customer, and the value of such a record is exactly that nobody else can read it.

  New trails are created readable and writable by you alone. The trails already on disk are put right the next time
  the cockpit starts, so a machine that has been running it for a while does not stay open and does not need you to
  run `chmod` on our behalf. Only the cockpit's own trails are touched — a file it did not write keeps whatever
  permissions you gave it. On Windows this changes nothing: there are no permission bits of this kind, and the
  per-user application-data folder is the boundary that does the same job.

- fixed: an open window no longer stops the rest of the cockpit. Every session and pane lives in the one main
  window, so anything opened over it — projects, MCP servers, profiles, worktrees, the plugin store, options, a
  plugin's own issue list or workflow manager — took every running session down with it. An agent asking for
  permission could not be answered at all, because the place to answer it is a banner in the pane behind the
  window in front; the only way out was to close what you were working in and lose what you had typed into it.

  Those windows now open beside the cockpit instead of over it. You can read a running session, answer an agent,
  and go back to the window you left open with everything you had filled in still there. Asking for one that is
  already open brings that window to the front rather than stacking a copy that would save over the first — for
  the cockpit's own windows, and for a plugin's where the plugin says which of its windows are one of a kind.
  YouTrack's issue list is one; the issue picker it opens from a session's header is one *per session*, since
  two of them are about different sessions and folding them together would track the issue against the wrong
  one.

  What stays as it was, deliberately: confirming a removal, typing a password, trusting a plugin before it is
  installed, and choosing what a restore overwrites. Those are answered in seconds, and none of them may be left
  half-answered while something else carries on — so they still hold everything until you answer.

  Locking the screen still covers the whole application. The lock holds the main window, and these windows sit
  beside it rather than inside it, so they are taken off screen while it is up and put back — with their contents
  — once you have unlocked.

- fixed: a sign-in on an OAuth-protected MCP server no longer tells you to check a browser window that was never
  opened. When the server's own discovery went wrong, the cockpit stopped before it knew where to send you — and
  then reported it as a browser you had failed to finish with. The three ways a sign-in can stop now read
  differently: one that never reached a browser at all, one where the cockpit handed the sign-in over and nothing
  ever came back, and one where the browser did come back but no usable credential came with it — including when
  you decline the sign-in yourself. None of them shows what actually went wrong, which would risk putting the
  server's response on screen; each says where to find it.

  Each says only what the cockpit can see. Handing the address to your desktop is the last step it can observe —
  whether a window then appeared is not something it can know, and it no longer claims to. A sign-in that nothing
  takes now gives up and says so, instead of waiting indefinitely for a browser that will never answer.

  And the log is findable now. Several places in the cockpit send you to "the log" — a sign-in, a hotkey that could
  not be registered, a screenshot shortcut that did not take — and none of them, nor anything else in the app, ever
  said where it is. **System diagnostics** under Options now names the file, next to the crash logs it already
  listed, so the panel you were already meant to copy from carries the path too.

  The reason itself goes to the log, and the line that carries it has stopped saying the opposite of what happened:
  a sign-in you started and watched fail was recorded as routine background housekeeping, under a sentence stating
  the cockpit had not asked you — on the one path where asking is exactly what you did. It is a warning now, and it
  is written even when the sign-in fails quietly, so the log the message points you at is never empty.

- fixed: a plugin update could be applied while the cockpit was still running the plugin it replaces. An update is
  deliberately held back until the next start, because that is the one moment nothing is loaded — but the step that
  *reads* which plugins are installed was applying the held-back ones on its way past, and that read runs after
  every enable, disable or removal, and by itself every fifteen minutes while the cockpit looks for new versions.
  A plugin could therefore be swapped underneath itself, at a moment nobody asked for and with nothing said about
  it. Waiting updates and removals are now applied at startup and nowhere else, which is what they always claimed.

- fixed: removing a plugin that had an update waiting did not remove it. The waiting update was applied first, and
  applying it replaces the plugin's folder — the note recording the removal was inside that folder, so it went
  with it. The plugin came back at a version you had just decided not to keep, saying nothing. Removing a plugin
  now discards an update waiting for it.

  Changing your mind still works: installing a plugin again after asking for it to be removed cancels the removal.
  It comes back asking for your approval, the way anything whose bytes you have not approved does — it does not
  quietly return switched off, and it does not quietly return already trusted.

  A removal that could not be carried out also no longer undoes itself. Deleting the folder is best-effort — a file
  still open leaves it for the next start — and a plugin in that state was found and loaded again on every start
  after it. A plugin you removed now stays gone from the moment you remove it, whether or not its folder went.

- fixed: **Update all** ended a batch that lost a plugin with "the rest failed — see the message above", and there
  was no message above: the store shows one line at a time, so each failure had already been written over by the
  next plugin, then by the catalogue reloading, then by that summary. It names the plugins that failed instead.

- fixed: text boxes and dropdowns went back to the stock Fluent palette the moment you hovered or typed in them —
  a focused input turned black and grew a two-pixel ring, and a hovered picker took on a translucent dark fill,
  none of which are colours this theme uses. Hovering and focusing now leave a field's own fill and border alone.
  The most visible symptom was the Insert prompt search bar, which asks for no box at all and was drawn as one
  anyway.

- fixed: the flow name in the workflow editor answers to the pointer and to focus again. It is a text box you can
  type in rather than a label, and it had no way of saying so once the fix above took away the stock highlight it
  had been borrowing — so it now carries a border of its own that stays invisible until you reach for it. The
  toolbar it sits in is two pixels taller for that, and no longer changes height when you click into the name.

- fixed: the bottom row of a terminal pane could be drawn where the pane had no room to show it, so a session's
  last line was simply absent — and nothing said so. A task list that ends one entry early looks like a task list
  that ended. The terminal deliberately keeps its row count steady through the small height changes the surrounding
  chrome makes, instead of resizing the session every time a border thickens by a pixel; but it held steady in both
  directions, so a pane that had just become a fraction too short kept a row it no longer had the height to draw.
  It now only ever holds a row count the pane can actually show: growing into new space is still damped, losing
  space takes effect immediately.

  Zoomed all the way out, where a row of text is shorter than the margin being held steady, that margin now gives
  way to the row — otherwise the terminal would stop growing into space that opened up and leave a strip of the
  pane permanently unused.
- fixed: **Update all** in the plugin store offered to restart the cockpit as soon as the *first* plugin of the batch
  was done, while the rest were still downloading. Taking it up there restarted the app mid-batch, and the plugins
  that had not had their turn were silently left on their old version — with a banner that had just said the update
  was finished. The restart is now held back until the whole batch is over.

- fixed: a second plugin install could be started while one was already running — from the version picker in the
  detail panel, or from Install from zip, or from the catalogue on top of a zip install that was showing no sign
  of itself at all. They reach the same unpacking step, so two of them could unpack over each other into the same
  folder. Those buttons are now dead for as long as an install is in flight, and a zip install shows the same
  overlay as the rest instead of running invisibly.

  The last way in was the file picker. With **Install from zip** waiting on a file, nothing was running yet, so an
  install could still be started from the catalogue — and the two met when the picker came back with a path. The
  picker now holds the store dialog it was opened from, the way the picker for a store folder beside it already
  did, so the catalogue is out of reach behind it. Underneath that, unpacking is claimed by one install at a time,
  so whichever arrives second does not start rather than unpacking over the first.

  Underneath that, the "the store is working" signal used to be cleared by whichever step finished first. Every
  install ends by reloading the catalogue, and that reload cleared the signal while the install around it was
  still going — which let the restart offer and the install buttons come back mid-install. It now stays raised
  until the outermost piece of work is done.

- fixed: installing a plugin, or updating all of them, showed almost nothing while it happened — a line of text in
  the footer and a search box that stopped responding. The store now covers its catalogue while it works and says
  what it is doing: which plugin is being installed, and for **Update all**, how far through the batch it is. The
  footer line keeps working as before. There is no percentage for a single plugin, and deliberately so: the download
  arrives in one piece, so a bar claiming progress within it would be inventing it.

- fixed: the MCP servers dialog could put its Cancel and Save buttons out of reach, so a server you had just
  configured could not be saved. It happened whenever the dialog had something to tell you — the notice naming the
  servers it hid because the cockpit already runs one by that name — because that notice shared its space with the
  buttons and took as much width as its text wanted, pushing them off the edge and leaving the half-finished notice
  as the only thing still visible. The notice now wraps into the room that is left over and the buttons keep theirs,
  so neither the length of the message, the sign-in method a server uses, nor the number of custom headers you add
  can move them. The window can be resized now as well, since what it holds grows with the servers you add.

- fixed: in the New session dialog, a plugin's option label ran over the control beside it — "Permission mode" came
  out cut off and sitting on top of its own dropdown. The label column now sizes to the longest label the plugin
  declared instead of to a width chosen for a shorter one, and the rows still line up as a single column. A plugin
  that writes a sentence for a label gets an ellipsis and the full text on hover, rather than squeezing its own
  control off the row.

- fixed: radio buttons — the two that choose between a remote store and a local folder — drew in the system blue
  instead of the cockpit's accent. The two colours are close enough that it passed an eyeball test, and nothing
  connected them: the day the accent moves, that control would have stayed behind.

- fixed: the trails the cockpit keeps beside your settings — what you approved, what was delegated, what agents sent
  each other, what each session spent — are now created readable only by your own account. On Linux and macOS they
  were created at whatever the system default allowed, which on a stock Fedora means any account on the machine could
  read them, and those files hold the commands you approved and the prompts your agents were given.

- fixed: a release candidate could be published as if it were the release. Any tag beginning with a `v` started the
  full release build, so a `v1.2.3-rc.1` — or a typo like `v1.2` — produced a normal release that took over "latest"
  on GitHub, and consumed the pending release notes on its way out, leaving the real release that followed with
  nothing to show. Only a plain `v1.2.3` starts a release now; anything else stops the run before a release exists
  and before the notes are touched.

- fixed: a workflow run that failed was reported in the amber the cockpit uses for "waiting for you" rather than in
  red. A run that broke is not waiting for anybody, and in a list of runs the two looked the same.

- fixed: the file headers in a session's diff came out a flat grey instead of the theme's text colour. They asked
  for a colour that has never existed under that name, so the lookup could only ever miss and fall through to a
  value written beside it.

- fixed: the "needs you" badge, the "the CEO is working…" band and the number inside a step's dot were lettered in a
  near-black mixed for the old orange. They sit on bright fills, which is the one place the theme's white cannot be
  read — there is a colour for exactly that now, and it is the same one in all three places.

- fixed: the offer to pick a session up again when its allowance returns now actually appears. It was only ever made
  on the reading that first passed the warning line, and only if that reading already showed the allowance fully
  spent — but an allowance climbs to spent rather than arriving there, so by the time it read 100% the moment to
  offer had gone. In practice the button was reachable almost never. It is now offered as soon as the figure reads
  100%, however gradually it got there, and made once rather than on every poll so a prompt you are part-way through
  typing is not overwritten under your hands.

- fixed: the bar keeps the figure it is showing current. It used to quote whatever the number was at the moment the
  warning went up, so a week that went on filling from 91% to 100% still read 91% — understating exactly the thing
  you would be watching it for. Nothing reappears because of this: a bar you took down stays down.

- fixed: a warning that a newer one wrote over is no longer lost. The bar holds one sentence for every kind of usage
  it reports, so a context window filling up would take the place of a week that is nearly spent — and because each
  figure only speaks when it crosses its threshold, the week had already had its turn and said nothing more. Clear
  the context and the bar went quiet on both. It now keeps what each of them has to say for as long as each is still
  over its line, and falls back to the most recent one still standing rather than to nothing. Dismissing takes the
  whole bar down, including what it was covering — a bar that reappears by itself after you click it away reads as
  the click not having worked — and a figure that drops back and climbs again is news once more.

- fixed: a control you cannot use now looks like one. A disabled button or dropdown kept its label at full strength,
  so the only sign it was unavailable was that clicking did nothing — the theme was setting the faded colour on the
  control while the text was being coloured by a rule of its own that won. The same rule had quietly been undoing
  every other place a control tried to tint its own label.

- fixed: a dropdown you cannot change no longer stands out more than the fields you can. "Provider (fixed after
  creation)" in the profile editor was drawn lighter than the editable fields around it and stepped forward on the
  form; it now recedes behind them, which is what unavailable is supposed to look like.

- fixed: the grey prompt inside an empty field — "Send a message…", "No folder chosen yet…" — is the theme's own
  faint grey again. The rule meant to colour it named a part of the text box that no longer exists, so it had been
  doing nothing and every placeholder in the app was taking the default from the underlying toolkit.

- fixed: a ticked checkbox is drawn in the cockpit's blue rather than the operating system's. The two look alike
  today by coincidence, not by connection — the tick would have stayed behind the day the accent colour moved.

- fixed: a usage warning takes itself down once the thing it warned about is gone. Clearing a session empties the
  context and the very next reading says so, but the bar went on reading "Context is 50% used" until you dismissed
  it by hand — a notice about a window that no longer existed, and clicking it away was the only way out. A warning
  one signal raised is left standing when a different one goes quiet: a week that is nearly spent is exactly the
  warning you would want kept while the context bar comes and goes. Where a later warning had covered one that
  carries an offer to pick the session up again, the bar goes back to saying what that offer is for rather than
  emptying and taking its buttons off screen with it. The offer itself goes when its own allowance rolls over,
  since an allowance back at 5% has nothing to be picked up from; a resume you already scheduled stays, because
  that is a moment you committed to rather than one on offer, and a bar you dismissed stays dismissed.

- fixed: a resume you scheduled is actually sent when its moment arrives. The clock behind the feature was started on
  a thread that has no clock, so it never once ticked: the banner said "Resuming Mon 13:12", the moment came and went,
  and nothing was sent — for every scheduled resume since the feature shipped, not just some of them. Nothing said so
  either, which is why it could sit there unnoticed; a resume that is scheduled, sent, missed or cannot be delivered
  now writes a line you can go back and read.

- fixed: the "Resuming …" line on a session tells you what is actually waiting. It used to be written the moment you
  scheduled something and then never touched again, so it stayed up after the resume had fired and after it had been
  cancelled. It now follows the schedule itself, and appears on its own for a session that is handed a resume already
  waiting on it. Worth knowing what it still cannot do: a resume does not survive closing the cockpit, because the
  session it was aimed at is not reopened — one left over from a previous run reports that it could not be delivered.

- fixed: "Pick another moment", offered when a session has spent its allowance, opens on your own clock instead of on
  UTC. It was filling the pickers with the reset moment as the provider reported it, so anyone east of Greenwich saw a
  time hours too early — and taking the suggestion as it stood scheduled the resume before the allowance had actually
  returned, which is a resume that never fires, or fires straight away. A moment on one of the two nights the clocks
  change is now read at the time you picked rather than at midnight of that day, so an afternoon in late October is no
  longer an hour out.

- fixed: an agent that isolates itself in a worktree no longer moves the branch in the folder it pointed at. Starting
  a session yourself still brings your checkout forward with it — that is the point of it — but a session an agent
  opens against a folder it merely named now starts from the remote's tip and leaves that branch exactly where it
  was. The agent gets the same up-to-date base either way; what it no longer gets is the ability to rewrite a working
  tree in a repository nobody pointed it at. Where the branch holds commits that were never pushed, both kinds of
  session still start from those, because work that exists nowhere else belongs in what a session is built on.

- fixed: the message about a remote that could not be reached no longer repeats what your repository has written
  down as that remote. Git is happy to take a whole URL where a remote's name would go, and a URL can carry a token
  in it — which would have gone straight into a notification and into the answer an agent reads. Only the host part
  survives now, the same way git itself reports its own failures.

- fixed: a global shortcut you switched on that your desktop refused to register now says so in the settings
  screen. It used to read exactly like a shortcut you had never switched on — an empty line, no error — and the
  only sign anything was wrong was the key doing nothing when you pressed it.

- fixed: giving a project a new logo now shows the new one. Clearing the old picture and picking another left the
  card on the one you had just thrown away, because a logo is filed under its project's name and the cockpit went
  on showing the copy it had already read from that name.

- fixed: the picker for a project's logo now offers SVG files, and vector formats generally. It always accepted one
  — a logo that is a vector is drawn to a picture on the way in — but the dialog only listed photographs, so the
  file you wanted was greyed out unless you typed its path by hand.

- fixed: double-clicking while a marking tool is in hand no longer takes the shot. Two quick marks in the same spot
  are two marks; reading the second as "take it" handed over a screenshot you were still working on.

- fixed: pressing Region on the selection surface now puts down whichever marking tool you were holding, not only
  the one that paints over things. Framing something and then pressing Region left you still framing, with the row
  of tools quietly saying so.

- fixed: taking everything after you had picked up a marking tool now lights the Everything button again. What was
  marked out had not changed — it was already the whole capture — so nothing was said about it, and the button sat
  dark while everything was taken.

- fixed: a mark's thickness is now drawn at the size it will actually be. On a scaled display the preview drew
  frames heavier than what got burnt into the picture, so what you checked before sending was not quite what you
  sent.

- fixed: a run that asks for its agent's file tools to stay inside the folder it works in is now refused when the
  profile it runs on cannot promise that — not only when the run has a worktree of its own. Autopilot works this way
  when the folder is not a git repository, and the CEO that reviews a run's work does too: both asked for the folder
  to be the boundary and were answered yes, while nothing checked that the profile behind them honoured it. Three
  providers that ship — Kimi, Gemini and GitHub Models — never state that they keep file access inside the working
  directory, so a run pointed at one is now stopped and told which profile refused, where before it went ahead with an
  agent nobody was watching and the whole disk in reach. Claude and Codex do state it, and a local model states it once
  it is held to the folder, so runs on those are unaffected. A run that asks to be held to a folder but is given none
  is refused too: confinement to nothing is not confinement.
- fixed: the CEO that reviews an Autopilot run's work now runs on the run's own autonomy mode, rather than whatever
  permission mode its profile happened to have saved. A profile stored on the permission-bypassing mode used to hand
  that mode to the reviewer, which switched off the very confinement the run asks for it — so the reviewer read your
  work with more reach than the run it was reviewing.
- fixed: a run whose reviewing CEO never starts now fails the step and says why, instead of waiting for a verdict that
  can no longer come. The run would sit on its first step with the pipeline frozen and nothing on screen to explain it;
  it now reads like any other refusal, naming the profile that could not start.
- fixed: a session you isolate in its own worktree starts on the latest state of the branch it forks from, instead of
  on whatever your checkout last pulled. Nothing fetched before the worktree was made, so a folder you had not touched
  in a while quietly handed every session a base tens of commits old. The branch the session forks from is now fetched
  first and, where that is safe, fast-forwarded, so your own checkout comes along with it.
- fixed: your working tree is only ever fast-forwarded, and only when there is genuinely nothing in it to lose — never
  a merge, never a rebase. A branch with uncommitted changes, one holding commits that are not on the remote, or one
  where the update would land on something git is not keeping a copy of is left exactly as it was, and the session
  forks from it as it stands. That last case is worth spelling out: git declines to overwrite a file it does not
  track, but a file you have told it to *ignore* — the local config, the environment file, the one thing there is no
  second copy of anywhere — it replaces without a word. So the incoming paths are checked against what is actually
  sitting in your folder first, and anything in the way stops the update rather than being written over.
- fixed: you are told what happened to the branch rather than left to discover it. When your checkout was brought
  forward, a notification says so and by how much — it is your folder that moved, after all. When the session forked
  from something older than the remote instead, because the update was declined or the remote could not be reached, a
  notification names what it forked from and how far behind that is where that could be measured. It does not depend
  on the session actually starting: call the start off, or let it fail on a name already taken, and you are still told
  that your own branch is no longer where you left it. An agent that isolates itself through the worktree tool is
  handed the same sentence in the tool's answer. A branch that was already up to date, or that tracks nothing at all,
  stays silent.
- fixed: a worktree whose folder you deleted by hand can be removed from Managed worktrees again. git no longer knows
  such a tree, so it refused the removal and the row came straight back — with nothing on screen to say why. The row
  is now cleared away (the branch is kept, as always), "Clean up finished" sweeps one up too, and a removal git does
  refuse — a tree still on disk holding work — says what git said instead of looking like a button that does nothing.
  Closing a session whose folder went the same way no longer parks the entry in the panel either: with nothing left
  on disk to keep, it is let go at teardown rather than held for review that has nothing to review.
- fixed: a session a flow started could never be relabelled by a ticket you linked to it afterwards. Its name is put
  together from the profile and the clock — "Claude — 14:22" — which nobody chose, but it was treated as a name you
  had picked, so the link left it alone. It now stays open to being labelled, the same as a session that was never
  given a name at all.
- fixed: a session that starts without you opening the new-session window now knows which project it works on, so
  everything a project decides — the MCP servers it brings, what a plugin hands a session as it starts — reaches it
  too. Until now only sessions you started by hand had a project, which left it silent exactly where it mattered
  most: an agent running on its own. A delegated task takes the project of the session that delegated it; a run
  inside a workspace (an Autopilot step, a workflow) and a session a plugin starts take the project that owns the
  folder they run in, including when that folder is a worktree the cockpit cut for them. A folder no project claims
  still belongs to none, and a folder two projects claim equally is left alone rather than guessed at.
  Worth knowing: an autonomous run now inherits its project's environment as well, so a project linked to a
  repository other than the one its folder clones will point that run's `gh` commands at the linked one.
- fixed: starting a session from a GitHub issue names it after the repository the issue came from — `hello-world#42`
  rather than `#42`. Issue numbers only mean anything inside one repository, and the cross-repo view lists every repo
  you have, so two issues could put two identically named sessions in the sidebar with nothing on either to tell them
  apart. The name is still yours to change in the new-session dialog before anything starts.
- fixed: a session that produces events far faster than the cockpit can take them in no longer grows the cockpit's
  memory without limit. Each session now holds at most a few thousand unread events; past that, further ones are
  counted and the session's own transcript says how many went missing, so a gap is something you can see rather than
  something that quietly happened. Reaching this at all means something was already wrong — a normal turn stays far
  below it.
- fixed: right-clicking a session in the sidebar, or a workspace tab, no longer starts dragging it. Both strips
  armed their reorder on any mouse button, so opening the context menu and moving the pointer to what it opened —
  Rename, typically — quietly moved the session or the workspace to a different position on the way there. They
  now reorder on the left button only, and a drag lets go as soon as the button is up instead of staying stuck to
  the pointer until the next click lands somewhere.

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
- fixed: opening an issue in the YouTrack or GitHub Issues dialog no longer hangs the cockpit and eats memory
  until it is killed. The prompt preview now scrolls sideways for a long line instead of trying to re-wrap text
  whose own line breaks are part of it.
- fixed: the issue you were reading no longer disappears from under you. Typing in the filter, changing the
  state or repository filter, toggling "Assigned to me" and refreshing all keep the issue selected instead of
  dropping back to the placeholder.
- fixed: what an action reported on an issue — started, moved, added to the prompt — stays on screen instead of
  being wiped a moment later by the refresh the action itself triggered. It still clears when you move to
  another issue.
- fixed: "Add to prompt" now explains why it is greyed out when there is no session running, and comes back to
  life as soon as "New session" has started one — it used to stay inert until you clicked the issue again.
- fixed: pressing "New session" twice in a row no longer opens a second dialog, and a second session with it.
  The button goes inert until the dialog you already have is closed.
- fixed: starting a session from an issue now tells a workflow where that session works, so a flow that cuts a
  branch or a worktree when an issue is picked gets the folder instead of an empty path.
- fixed: "Open in browser" on a GitHub issue says so when the browser will not start, instead of silently doing
  nothing; the YouTrack side now checks the address it built from your instance URL is a web address at all
  before handing it to the desktop.
- fixed: a Memory row now always offers a place to choose from. Until now the whole picker was hidden unless some
  plugin had already registered a memory source, so a cockpit with none — or with the Depot plugin installed but no
  connection set up yet — showed a bare box and no way to see that anything other than a folder was possible. "Folder"
  is now always offered, and a plugin's entry appears beside it whether or not it has any servers configured yet.
- fixed: several sessions for the same OAuth-protected MCP server starting at once could occasionally redeem the same
  refresh token twice instead of sharing one renewal — risking the whole sign-in being revoked, since these servers
  invalidate a refresh token's previous grant the moment it rotates. The renewal and the bookkeeping that tracks it
  now happen as one step, closing the brief window where a session arriving at the wrong moment could miss that a
  renewal was already under way, or already done.

### Removed

- removed: the Git status button in the left menu and the dialog it opened, which tracked a hand-maintained list of
  repositories. The status indicator on each session — the one that follows the folder that session works in — stays
  exactly as it was, and the repository list in the plugin's settings is gone with the dialog it fed.
- removed: the collapsible "Thinking…" step is no longer shown in the chat transcript. The pulsing indicator
  above the message box already shows the model is working, so a separate reasoning line in the transcript
  added little.
- removed: two dropdown-picker theme rules that never painted anything — measured to have no effect in any
  state, so there is nothing to notice; pickers still look and behave exactly as before.
- removed: the legacy Inno Setup installer and the portable single-file `.exe` it was built from, along with the
  packaging scripts behind them — gone from both the release and nightly pages, so Windows now has exactly one
  installation form: the Velopack Setup and its Portable build. The release notes also gained a short section
  calling out the update-feed files (`RELEASES-*`, `releases.*.json`, `*-full.nupkg`) as machinery for the
  in-app updater, not something to download by hand.
