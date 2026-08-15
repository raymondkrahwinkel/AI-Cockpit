# Where a diagram lives, what it is called, and how it is versioned

*AC-812, sub of AC-525 (diagram builder). Decided by Raymond 2026-08-16. This note is the storage model the
diagram surface (AC-824), the diff gate (AC-825) and the diagrams list (AC-826) build against — not the UI
around it.*

## Decision (Raymond)

A diagram lives **in the project's memory**. There is no projectfolder-vs-Depot choice to make: the place follows
the project's own settings — whichever memory path or Depot binding that project already uses. A project with
**more than one** configured memory path means the UI **asks which one**, rather than picking for the operator.

## The mechanism already exists — no new field

Checked against `main`, not assumed:

| Need | What already carries it |
| --- | --- |
| Where a project's memory lives | `Project.Resources` — rows with `ProjectResourceRole.Memory` (AC-483) |
| A folder *or* something else | `ProjectResource.Reference`: a folder path, or `<scheme>:<value>` (`depot:cockpit`) |
| What `depot:` means | `IProjectMemorySourceRegistry`, filled by the Depot plugin per connection (AC-165/166) |
| More than one memory place at once | Already supported and already intended — "a local folder *and* a Depot project together" |
| Splitting a reference | `ProjectMemoryRef.TryParse` — the one parser both the picker and a starting session use |

`Project.MemoryRef` is a deprecated mirror of the *first* Memory row; read `Resources` instead (see its own remark).

**The one gap:** a plugin cannot *read* these rows. `ICockpitHost` only registers sources; `IWorkspaceContext`
exposes `WorkspaceId`, `Storage`, `Sessions`, `EmbedSession` — no project. `SessionStartDefaults` is the only
existing reader and it turns rows into *prose* for the standing instructions, not a writable destination.

Closing that gap belongs with the code that needs it (AC-824). `IDiagramAccessRegistry` (AC-810) is in `main`, but
it holds a surface's Mermaid text purely in memory — `SurfaceOpened`/`UpdateText`/`PeekText`, no project, no
persistence — so nothing saves a diagram anywhere yet, and a store with no saver would be built against a guess.

## Which home a given diagram gets

| Memory rows on the project | What happens |
| --- | --- |
| 0 | Saving is refused, pointing at the project editor. No fallback to `SourceDirectory` — that is deliberately *not* the memory place, and guessing writes the operator's work somewhere they never named. |
| 1 | That one. No question asked. |
| 2+ | Ask, once, at the moment of the first save. |

The answer is remembered **with the diagram**, not as a new project-wide default: the project settings are the
operator's to set in the project editor, and a diagram picking one is not the project changing its mind. The
diagrams list (AC-826) lists across *every* configured memory row, so choosing one home never hides a diagram.

## File form and name

**`<memory>/Diagrams/<slug>.md`** — one `# Title` heading, one ```` ```mermaid ```` fence, nothing else.

- Markdown rather than a bare `.mmd`: both homes take either (Depot's memory tree accepts `.mmd` — verified, not
  assumed), but markdown renders inline in git forges, editors and Depot's own viewer, and gives the title a place
  to live without a sidecar. `Diagrams/` alongside `Findings/`, `Plans/`, `Sessions/` is the convention a memory
  tree already uses.
- The Mermaid text stays the source of truth (AC-811). One save is one readable diff, because the file is text.
- The `# Title` is the display name — free text, renamable, free to collide. The filename is its slug; a collision
  in the same home gets `-2`. Renaming the title does not rename the file: a stable path is what a link, a git
  history and a Depot version chain all hang on.

**When the file first appears:** on the first save, not on the first sentence. This settles the tension AC-816
flagged — the quick-start button's name field is a **working title** that seeds the H1 and the slug, it does not
mean the diagram is already stored. A memory home keeps history forever (a git commit, a Depot version), so a file
per abandoned conversation is noise in exactly the place that never forgets, and the "which home?" question above
only has to be asked once there is something worth keeping.

## Versions: none of our own

Both homes already version, and neither needs help:

- **Project folder** — git, if the memory folder is in a repository. If it is not, it has no history, and that is
  the operator's choice of home rather than something to compensate for by building a second mechanism.
- **Depot** — `list_versions` / `restore_version` on the file's own overwrite history.

So: write the whole file per save (never a partial patch, which would fragment the history the home is keeping)
and build nothing else. A private version store on top of two working ones is the double work AC-812 warned about
from the start.

## The same diagram open in two sessions

**Detect, never lock, never silently overwrite** — the rule AC-247 already set for shared project definitions, and
the idiom both homes already speak:

1. Opening a diagram keeps the checksum it was read at.
2. Saving sends that checksum as the baseline — Depot's `write` takes `baseChecksum` and fails on a mismatch; a
   folder home compares a hash of the file as it is now.
3. A mismatch is a **conflict, not a retry**: the save does not land, and what is on disk now is shown against the
   local text through the diff gate AC-825 is building anyway (`SharedProjectWriteBackOutcome.ChecksumConflict` +
   `ProjectDefinitionConflictDialog` are the existing shape of this).

No locking, no file watcher, no merge: the second writer finds out at the only moment it matters, and the operator
decides. Two sessions may freely have the same diagram *open* — only saving is ever contended.
