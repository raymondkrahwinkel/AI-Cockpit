namespace Cockpit.Plugins.Abstractions.Files;

/// <summary>
/// One text change: replace <see cref="Length"/> characters at <see cref="Offset"/> with <see cref="Text"/>. Offsets
/// count UTF-16 characters. In a list, each op applies to the text the previous op left behind.
/// </summary>
public sealed record TextOp(int Offset, int Length, string Text);

/// <summary>Where a live file stands, as the workspace shows it.</summary>
public enum LiveFileState
{
    /// <summary>The source is still joining; edits are accepted and sent once it is live.</summary>
    Connecting,
    /// <summary>Others' changes arrive as patches and the source keeps the file; there is nothing to save.</summary>
    Live,
    /// <summary>The source lost its own connection; edits stay with the source until it is back.</summary>
    Offline,
    /// <summary>The source needs the operator to sign in again before it is live.</summary>
    SignInNeeded,
    /// <summary>The file may be watched live but not edited; <see cref="LiveFileStatus.Reason"/> says why.</summary>
    ReadOnly,
    /// <summary>Live, but nobody is saving the shared text to the file yet.</summary>
    NoSaver,
    /// <summary>The source cannot express its changes as patches any more; the host reads the whole text again.</summary>
    Stale,
}

/// <summary>A <see cref="LiveFileState"/> with the operator-facing reason, when there is one.</summary>
public sealed record LiveFileStatus(LiveFileState State, string? Reason = null);

/// <summary>The whole text at revision <see cref="Rev"/>.</summary>
public sealed record LiveFileSnapshot(string Text, long Rev, LiveFileStatus Status);

/// <summary>
/// A change the host did not type: another editor's, or the result of <see cref="ILiveFileSession.UndoAsync"/>.
/// <see cref="Rev"/> is the revision after it; the ops apply to the text at <c>Rev - 1</c>.
/// </summary>
public sealed record LiveFilePatch(long Rev, IReadOnlyList<TextOp> Ops, string? Who = null);

/// <summary>Whether <see cref="ILiveFileSession.EditAsync"/> applied the ops.</summary>
public enum LiveEditStatus
{
    /// <summary>Applied; <see cref="LiveEditResult.Rev"/> is the new revision.</summary>
    Applied,
    /// <summary>Nothing applied: the base revision is behind <see cref="LiveEditResult.Rev"/>. The host sends again once it has those patches.</summary>
    Stale,
}

/// <summary>The answer to an edit.</summary>
public sealed record LiveEditResult(LiveEditStatus Status, long Rev);

/// <summary>
/// An optional extra on an <see cref="IFileSource"/>: the file is shared live, and the host's Markdown workspace
/// edits it as patches instead of saving snapshots. A source without it keeps the snapshot-and-save behaviour.
/// </summary>
public interface ILiveFileSource
{
    /// <summary>Opens a live session on the file. The host disposes it when the workspace closes.</summary>
    Task<ILiveFileSession> OpenLiveAsync(FileRef file, CancellationToken cancellationToken);
}

/// <summary>
/// A live session on one file. Every change, the host's or anyone else's, raises the revision by exactly one.
/// The host's own edits are never sent back to it as patches; undo and redo are the source's, so they only touch
/// the host's own edits and arrive as patches. Edits are accepted in every state but <see cref="LiveFileState.ReadOnly"/>,
/// where the source throws a <see cref="FileSourceException"/> with <see cref="FileSourceErrorKind.Forbidden"/>.
/// </summary>
public interface ILiveFileSession : IAsyncDisposable
{
    /// <summary>Raised for every change the host did not send through <see cref="EditAsync"/>, in revision order.</summary>
    event EventHandler<LiveFilePatch>? Patched;

    /// <summary>Raised when <see cref="LiveFileStatus"/> changes.</summary>
    event EventHandler<LiveFileStatus>? StatusChanged;

    /// <summary>The whole text and its revision, from one moment.</summary>
    Task<LiveFileSnapshot> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies the host's ops when <paramref name="baseRev"/> is the current revision; otherwise applies nothing
    /// and answers <see cref="LiveEditStatus.Stale"/> with the current revision. The host sends one edit at a time
    /// and repeats an edit whose answer it lost under the same <paramref name="editId"/>: when that id is the last
    /// one applied, answer <see cref="LiveEditStatus.Applied"/> with the revision it got and apply nothing.
    /// </summary>
    Task<LiveEditResult> EditAsync(string editId, long baseRev, IReadOnlyList<TextOp> ops, CancellationToken cancellationToken);

    /// <summary>
    /// Undoes (or with <paramref name="redo"/>, redoes) the host's own edits, grouped as the source sees fit; the
    /// result arrives as a patch. With nothing to undo it does nothing.
    /// </summary>
    Task UndoAsync(bool redo, CancellationToken cancellationToken);
}
