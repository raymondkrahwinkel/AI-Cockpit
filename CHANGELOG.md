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
  running in it reads your clipboard itself when it sees a paste — so the capture goes on the clipboard and the
  terminal is asked to perform its own paste, which is exactly what you would do by hand. Note that it does replace
  whatever you had copied; there is no private way to hand a terminal an image. A session whose provider cannot see
  images at all still says so on the button and in a notice, rather than taking the screenshot and losing it.
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
