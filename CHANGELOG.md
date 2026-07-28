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

### Changed

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

### Removed

- removed: the collapsible "Thinking…" step is no longer shown in the chat transcript. The pulsing indicator
  above the message box already shows the model is working, so a separate reasoning line in the transcript
  added little.
