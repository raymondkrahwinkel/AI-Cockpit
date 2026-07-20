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

### Changed

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

### Fixed

- fixed: pasting an image into a Claude SDK chat session was rejected with "provider does not support
  image input" even though Claude accepts images — the paste now attaches and is sent to the model.
- fixed: in a chat session, text the assistant writes after running a tool now appears below that tool
  in the order it happened, instead of jumping up above the tools it just used.

### Removed
