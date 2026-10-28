# File sources (AC-1783)

Requires host 0.136.0 and `Cockpit.Plugins.Abstractions` 3.45.0, namespace
`Cockpit.Plugins.Abstractions.Files`. Abstractions.UI remains 3.16.0; this feature adds no public UI API.

Register one `IFileSourceProvider` with `host.AddFileSource(provider)` during backend initialization.
Keep and dispose its returned handle when unloading, like a channel handler. Connections belong to this
plugin's own channel; IDs from another plugin cannot select its provider. A provider returns current
connections, raises `ConnectionsChanged` after settings change, and opens a source by connection ID.

`FileRef(ConnectionId, Id, Path)` contains an opaque source identity and a display path. The source
validates both again; the host never constructs native or remote paths. A source's `ReadAsync` returns a
stream and the version of exactly those bytes. Its caller disposes the stream. Versions are opaque:
Depot uses its checksum; other sources must document their guarantees.

`WriteAsync(target, content, ifVersion, mustNotExist, ct)` has exactly two valid modes:

| Mode | ifVersion | mustNotExist | Behavior |
|---|---|---|---|
| Save existing | nonempty original read version | false | Save only if the version still matches |
| Create | null | true | Save only if the target does not exist |

The source must atomically enforce the condition with the write. A changed or deleted save target and
an existing create target return `FileWriteResult(Conflict, currentVersion)` without writing. Success
returns `Written` with the version of the committed bytes. Neither caller nor adapter may retry without
the original condition. Sources leave the caller's input stream open. Cancellation before commit leaves
the previous content intact; a transport failure after commit requires a new read before retrying.

Depot implements create by sending `baseChecksum: ""`: on Depot dev `d9d5a756`,
`src/Depot.Infrastructure/FileStore/DiskFileStore.cs:107-125` checks the checksum under its project write
gate. Only an absent file has an empty checksum, including when an existing file is empty. Sync
`WriteManyCommand.cs:72` forwards the supplied checksum unchanged. Saves send the original read checksum,
not one fetched immediately before writing.

The host registers `files-connections`, `files-read`, `files-write` and the `files-changed` event on the
existing backend channel. Requests and answers use the public Files wire records and web JSON settings.
Reads and writes carry base64 bytes with a maximum of 1 MiB decoded content; larger content returns
`TooLarge`, never a partial read or write. The host checks the connection, read-only setting, write
capability and payload. The source still authorizes every operation, including the individual Depot
project role. Wire failures carry a typed error and a safe fixed message; cancellation is propagated.

`FileSourceChannelClient` in the host uses `IPluginUiChannel`, so local and server-admin operations use
the same transport. A client remains attached to its original plugin/backend channel. The source runs
where that backend runs: a future local-file source on a server addresses that server's disk.

AC-1782 can add listing, metadata, watches and streaming later. A future local/SFTP source must supply
the same conditional-write guarantee before advertising `CanWrite`; stat followed by rename alone
does not implement compare-and-swap. No live Depot is used by the contract tests.

## Host file workspaces (AC-1754)

Requires host 0.137.0, Abstractions 3.45.0 and Abstractions.UI 3.17.0.
`FileReadResult.CanWrite` and `FileSourceReadAnswer.CanWrite` are default-false init properties;
the existing constructors remain binary compatible. Set the hint from the same authorization used for
the snapshot. The channel also restricts it by the connection and source write capability. An older
answer is read-only. Every write still needs authorization at the source.

During UI initialization call `host.AddFileWorkspaceType("plugin.markdown", "Markdown file")`.
Open a document with `host.OpenFileWorkspaceAsync(typeId, new FileWorkspaceOpenOptions(file, key,
title, location, Edit: true))`. Omit `Edit` for a viewer; use `CreateNew: true` and `InitialText` for a
draft. A draft is written only by explicit Save or Ctrl+S. The host owns the editor, conditional save,
unsaved marker and discard confirmation. A repeated key focuses the same document and undo history.
Pop out and Dock back move that same body. Server-admin copies use a server-scoped workspace type
and the original remote channel, including after moving the window.

The host persists the file descriptor in local workspace metadata. It never persists editor text,
dirty state or draft contents. Restart loads the source again. Tab, floating-window and application
close ask before discarding unsaved text; hiding to the tray leaves it open. Generic plugin workspaces
can use `context.SetCloseGuard(Func<Task<bool>>)` to veto closure asynchronously, or null to clear it.
The guard runs before session teardown, and is not called for Pop out or Dock back.

Successful writes publish `FileSourceChannel.Saved` (`files-saved`) with the `FileRef` on the original
plugin channel. The Depot browser refreshes that project after a local save. The current remote channel
supports invocations only, so use its Refresh action to see an updated listing. Conflicts retain local
text and the original version; comparison and resolution belong to AC-1755.

## Live file sources (AC-1898)

Requires host 0.143.0 and `Cockpit.Plugins.Abstractions` 3.46.0. A plugin that implements it sets
`minHostVersion` 0.143.0; an older host never asks for a live session and keeps saving snapshots.

A source that shares a file live also implements `ILiveFileSource`. The host then opens a session with
`OpenLiveAsync(file, ct)` when the Markdown workspace loads the file, and disposes it when the workspace
closes. Without it, or when opening fails, the workspace reads and saves snapshots as above.

`ILiveFileSession` keeps one revision counter. Every change, the host's or anyone else's, raises it by
exactly one.

| Member | Contract |
|---|---|
| `ReadAsync` | The whole text, its revision and status, from one moment. Used on open and to resync. |
| `EditAsync(editId, baseRev, ops)` | Applies the host's ops only when `baseRev` is the current revision and answers `Applied` with `baseRev + 1`. Otherwise it applies nothing and answers `Stale` with the current revision; the host sends again after the patches it missed. The source never transforms an edit. The host repeats an edit whose answer it lost under the same id; if that id is the last one applied, answer `Applied` with its revision and apply nothing. Under `ReadOnly` throw `FileSourceException(Forbidden)`; in every other state accept the edit. |
| `Patched` | Every change the host did not send, in revision order: `LiveFilePatch(Rev, Ops, Who)`, ops on the text at `Rev - 1`. The host's own edits are never echoed. |
| `UndoAsync(redo)` | Undoes or redoes the host's own edits, the way `Y.UndoManager` tracks one origin; how edits are grouped is up to the source. The result arrives as a patch. With nothing to undo it does nothing. |
| `StatusChanged` | `Connecting`, `Live`, `Offline`, `SignInNeeded`, `ReadOnly`, `NoSaver` or `Stale`, with an optional reason. |

A `TextOp(Offset, Length, Text)` replaces `Length` UTF-16 code units at `Offset` (not UTF-8 bytes, which yrs
uses by default); in a list each op applies to the text the previous one left. The host sends edits and undos in
the order they happened, one call at a time. It shifts a patch past the ops the source has not applied yet, so the
caret, the selection and those ops stay on their characters; text typed inside a range someone else deletes
survives. Patches land through `TextDocument.Replace`, never by replacing the whole text. When a revision is
missing, a patch does not fit, or the status is `Stale`, the host reads the whole text again and merges what was
typed here per line. An edit whose answer was lost goes again under its own id first, so it never lands twice.

Undo belongs to the source. AvaloniaEdit's own undo stack is off for a live file, because it would also take
back other people's patches. Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z call `UndoAsync`.

While live, the workspace has no Save button, Ctrl+S does nothing, and the footer leaves out "Ctrl+S save". The
tab gets a green dot (`WorkspaceMarker.Live`) for `Live`, `ReadOnly` and `NoSaver`. It gets the amber
unsaved dot for `Offline`, `SignInNeeded`, or when an edit could not be sent. Closing waits up to five seconds
until every local edit has reached the source; if that fails, it asks before discarding. A session that ends on
the source side (a plugin reload) is not reopened: the text stays in the tab, marked as not sent.

The host subscribes to the events before it opens the session, so no patch is lost in between. The channel adds the actions `files-live-open`, `files-live-read`, `files-live-edit`, `files-live-undo` and
`files-live-close`, and the events `files-live-patch` and `files-live-state`, keyed by a session id. The whole
text travels as base64 under the same 1 MiB cap as `files-read`. Op text is plain JSON and limited to the same
size per edit. `FileSourceReadAnswer.Live` tells the host to open a session. Presence is the next section.

## Presence in live files (AC-1900)

Requires host 0.144.0 and `Cockpit.Plugins.Abstractions` 3.47.0. A plugin that implements `ILivePresence` sets
`minHostVersion` 0.144.0. An older host shows nobody.

A live session that also implements `ILivePresence` shows who else is in the file. `Peers` holds everyone, this host included, and
`PeersChanged` fires with the whole list whenever it changes. The source owns the list, its rhythm and every field.

| `LivePeer` | Meaning |
|---|---|
| `Who`, `Name` | A stable person id, and the name to show. The caret flag shows the first word of `Name`. |
| `Color` | `#rrggbb`, fixed per person. The host draws it as given and never works one out; a peer without a valid colour has no caret. |
| `Source`, `Role`, `Activity` | `Cockpit`, `web` or `agent`; for example `Editor`; `editing`, `selecting`, `reading` or `watching`. A Viewer is `watching`. |
| `You` | This host's own entry. It is listed ("(you)") but not drawn as a remote caret. |
| `Away` | The source lost them (offline). The avatar, caret and selection fade to 35%. |
| `Caret`, `Selection` | UTF-16 offsets in the current text, or null. `Selection` is `LiveRange(Start, End)` with `Start <= End` and is set only when something is selected. |

`SetPresenceAsync(LivePresenceState(Activity, Caret, Selection))` is the other way: the host tells the source where its own cursor is,
about 150 ms after the caret or selection settles. A source that cannot send it ignores the call. The host draws what it received on
the text it has now: offsets follow later edits, so a peer's caret stays on its character until the next list arrives.

The channel adds the action `files-live-presence-set` and the event `files-live-presence`, keyed by the session id like the live
events. `FileSourceLiveSnapshot.Peers` carries the first list; null means the source has no presence, and the host sends nothing.
`FileSourceLiveSnapshot.OpenUrl` is an optional `http(s)` address; with it the avatar popover offers "Open in {source} ↗".

In the header, left of the status chip, the avatars (22 px, a ring in the person's colour, an agent as a rounded square) open the
list "IN THIS NOTE · N": name, `Role · activity`, and the source as a pill. Its foot shows "Saved by {name} · {hh:mm}" when the
read told who saved last, and the link.

The Depot plugin keeps its awareness codec (y-protocols v1) and presence state in `Notes/Live`: a beat every five seconds with a clock
that only goes up (the hub ignores an update that does not), someone gone on `left` or after fifteen seconds of silence, and the hub's
own colour passed on. The mapping between a Markdown offset and a Yjs RelativePosition comes from the live bridge (AC-1899).
