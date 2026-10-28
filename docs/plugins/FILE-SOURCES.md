# File sources (AC-1783)

Requires host 0.136.0 and `Cockpit.Plugins.Abstractions` 3.42.0, namespace
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

Requires host 0.137.0, Abstractions 3.43.0 and Abstractions.UI 3.17.0.
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
