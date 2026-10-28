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
