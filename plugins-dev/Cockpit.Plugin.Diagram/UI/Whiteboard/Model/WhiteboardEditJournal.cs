namespace Cockpit.Plugin.Diagram.Whiteboard.Model;

// AC-912: one operator handling, with its inverse computed against the board as it stands when Undo/Redo runs —
// never against a stored snapshot (a pasted screenshot is megabytes). Both return null when they landed, else why not.
internal sealed record WhiteboardEdit(string Id, string Summary, Guid ObjectId, DateTime When, Func<string?> Undo, Func<string?> Redo)
{
    public bool Reverted { get; set; }
}

// AC-912: the operator's own handlings on one board, beside the agent's journal in IWhiteboardAccessRegistry rather
// than in it — an inverse works on WhiteboardDocument, which the host-side registry knows nothing about.
internal sealed class WhiteboardEditJournal(string surfaceId)
{
    private readonly List<WhiteboardEdit> _entries = [];
    private readonly Stack<WhiteboardEdit> _redo = new();

    public event Action<string>? Changed;

    public IReadOnlyList<WhiteboardEdit> Entries => _entries;

    public void Record(string summary, Guid objectId, Func<string?> undo, Func<string?> redo)
    {
        _entries.Add(new WhiteboardEdit(Guid.NewGuid().ToString("N"), summary, objectId, DateTime.Now, undo, redo));
        _redo.Clear();
        Changed?.Invoke(surfaceId);
    }

    // Ctrl+Z takes back the operator's own last handling regardless of what the agent did since (AC-912's reading (a)),
    // which is why this walks its own list rather than the shared journal the activity strip shows. Nothing left to
    // undo is not a refusal — it returns null, and the key does what it does in every other drawing surface: nothing.
    public string? UndoLast() =>
        _entries.FindLast(entry => !entry.Reverted) is { } entry ? Undo(entry.Id) : null;

    public string? Undo(string entryId)
    {
        if (_entries.Find(candidate => candidate.Id == entryId) is not { } entry)
        {
            return "This handling was not found.";
        }

        if (entry.Reverted)
        {
            return "This handling has already been undone.";
        }

        if (entry.Undo() is { } refusal)
        {
            return refusal;
        }

        entry.Reverted = true;
        _redo.Push(entry);
        Changed?.Invoke(surfaceId);
        return null;
    }

    // Redo stays "what you just undid": Record clears the stack, so a new handling ends the branch instead of
    // leaving a tree to walk back into.
    public string? RedoLast()
    {
        if (!_redo.TryPop(out var entry))
        {
            return null;
        }

        if (entry.Redo() is { } refusal)
        {
            _redo.Push(entry);
            return refusal;
        }

        entry.Reverted = false;
        Changed?.Invoke(surfaceId);
        return null;
    }
}
