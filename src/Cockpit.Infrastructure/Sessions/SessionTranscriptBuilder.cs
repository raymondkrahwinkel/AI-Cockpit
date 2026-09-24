using Cockpit.Core.Markdown;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// The row one event landed on, as a snapshot, and whether it landed in a sub-agent's lane rather than the top level.
public readonly record struct TranscriptFold(TranscriptSnapshotEntry? Row, bool InSubAgentLane);

// AC-1377 (F1.5): the rows a session's events form, which `SessionViewModel.Apply` used to build straight into its view
// models. Moved unchanged in substance; `Ac1377_TranscriptRowCharacterizationTests` holds it to the rows main formed.
// One thread only — the consumer's (`ISessionTranscript`) — so nothing here locks.
internal sealed class SessionTranscriptBuilder(
    TimeProvider time,
    Func<PermissionRequested, bool> tryAutoAllow,
    Func<bool> interruptRequested,
    Action<int, TranscriptSnapshotEntry> changed)
{
    // Named as `TranscriptEntryKind` names them, which is what the row's `Kind` string has always been.
    private const string AssistantText = "AssistantText";
    private const string ToolUse = "ToolUse";
    private const string ToolResultKind = "ToolResult";
    private const string QuestionKind = "Question";
    private const string TurnCompletedKind = "TurnCompleted";
    private const string ErrorKind = "Error";
    private const string Thinking = "Thinking";

    // How many lines a row of one code block carries before the next starts a new row. Set against the spread
    // the transcript already has: the tallest prose row measured 419px in a 461px viewport (AC-1265), and this
    // many monospaced lines sits under that, so a code row is no longer the outlier the panel re-anchors on.
    private const int CodeBlockRowLines = 20;

    // AC-1272: a table row is one line, like a fenced code line, but far taller -- AC-1271 measured the
    // unsplit table at 1261px over 15 rendered rows, ~84px each. This keeps a continuation fragment (the
    // worst case: every counted line its own rendered row) under the smaller 382px viewport.
    private const int TableFragmentLines = 4;

    // How much markdown a row carries before the next one takes over, for a block with no blank line in it.
    // Set from the measurement rather than guessed: the shapes that broke ran 0,24-0,42px of row height per
    // character, so this stays under 260px in the 382px chat viewport and under 150px in the 461px pane.
    private const int UnbrokenBlockCharacters = 600;

    // One row as the host holds it: its current snapshot, the text it has really been given, and its nesting.
    private sealed class Row(TranscriptSnapshotEntry entry, int fullTextChars)
    {
        public TranscriptSnapshotEntry Entry { get; set; } = entry;

        public int FullTextChars { get; set; } = fullTextChars;

        public int Version { get; set; }

        public Row? Parent { get; set; }

        public List<Row> Children { get; } = [];
    }

    // One sub-agent's own streaming state (AC-146).
    private sealed class SubAgentLane(Row anchor)
    {
        public Row Anchor { get; } = anchor;
        public Row? CurrentAssistantRow { get; set; }
        public Row? CurrentThinkingRow { get; set; }
        public int CurrentThinkingBlockIndex { get; set; } = -1;
    }

    // Top-level rows in transcript order, and every row by id, nested ones included.
    private readonly List<Row> _rows = [];
    private readonly Dictionary<string, Row> _byId = new(StringComparer.Ordinal);

    private Row? _currentAssistantRow;

    // AC-1238: whether the row now open has already been finished by a blank line.
    private bool _assistantBlockSealed;
    private List<Row>? _codeSpanRows;
    private bool _nextRowContinuesCodeBlock;

    // AC-1272: the same shape as the fence pair above, for a table split by row count instead of by a blank line.
    private List<Row>? _tableSpanRows;
    private bool _nextRowContinuesTable;

    // The reasoning row being streamed into (AC-213); contiguous deltas of one block append onto one row.
    private Row? _currentThinkingRow;

    // A delta from a different block (e.g. Codex's raw reasoning vs. its summary) starts a fresh row.
    private int _currentThinkingBlockIndex = -1;

    // Live sub-agent lanes, keyed by the parent Task tool call's own tool_use_id. Cleared on every `TurnCompleted`: a
    // sub-agent does not outlive the turn that spawned it.
    private readonly Dictionary<string, SubAgentLane> _subAgentLanes = [];

    // Keep orphaned sub-agent text separate so it cannot merge into the top-level reply or be read aloud (AC-146).
    private Row? _currentOrphanedSubAgentTextRow;

    public TranscriptFold Apply(SessionEvent evt) => evt switch
    {
        AssistantTextDelta delta => _OnTextDelta(delta),
        AssistantTextCompleted completed => _OnTextCompleted(completed),
        ToolUseRequested toolUse => _OnToolUse(toolUse),
        ToolResult toolResult => _OnToolResult(toolResult),
        PermissionRequested permission => _OnPermission(permission),
        Question question => new(_Add(_NewRow(QuestionKind, question.Text), parent: null).Entry, false),
        TurnCompleted turn => _OnTurnCompleted(turn),
        SessionError error => _OnSessionError(error),
        AssistantThinkingDelta thinkingDelta => _OnThinking(thinkingDelta),
        _ => default,
    };

    // A row the consumer formed or changed itself. Adopted whole, nested rows included; a snapshot equal to the one
    // held changes nothing and is not published again.
    public void Record(TranscriptSnapshotEntry entry)
    {
        if (_byId.TryGetValue(entry.Id, out var row))
        {
            if (_Adopt(row, entry))
            {
                _Publish(row);
            }

            return;
        }

        _Publish(_Add(_Seed(entry), parent: null, publish: false));
    }

    // Rows repainted from the store: held so later changes version them, never published or written back. AC-1379:
    // counted as their first version, so a later change reads as a change (Version > 1) and not as a new row.
    public void Seed(IReadOnlyList<TranscriptSnapshotEntry> entries)
    {
        foreach (var entry in entries)
        {
            _Add(_Seed(entry), parent: null, publish: false).Version = 1;
        }
    }

    // A turn is about to be sent: prose it produces starts a fresh reply, and a fresh thinking row.
    public void EndReply()
    {
        _currentAssistantRow = null;
        _CloseThinkingRow();
    }

    // The context was cleared (AC-564). The thinking row is left as it was, as it always has been.
    public void ResetStreaming()
    {
        _subAgentLanes.Clear();
        _currentAssistantRow = null;
        _currentOrphanedSubAgentTextRow = null;
    }

    private TranscriptFold _OnTextDelta(AssistantTextDelta delta)
    {
        // AC-146: a sub-agent's own streaming text accumulates onto its lane's row, nested under its Task tool-use
        // anchor — the operator's own reply and a sub-agent's internal narration must never merge into one row.
        if (_ResolveSubAgentLane(delta.ParentToolUseId) is { } lane)
        {
            lane.CurrentThinkingRow = null;
            lane.CurrentAssistantRow ??= _Add(_NewRow(AssistantText, string.Empty), lane.Anchor);
            _Append(lane.CurrentAssistantRow, delta.Text);
            return new(lane.CurrentAssistantRow.Entry, true);
        }

        // AC-146: a parent id this pane never resolved to a lane is an orphan, not a top-level chunk — shown in its
        // own separate row so it can never merge into the genuine top-level reply.
        if (!string.IsNullOrEmpty(delta.ParentToolUseId))
        {
            _currentOrphanedSubAgentTextRow ??= _Add(_NewRow(AssistantText, string.Empty), parent: null);
            _Append(_currentOrphanedSubAgentTextRow, delta.Text);
            return new(_currentOrphanedSubAgentTextRow.Entry, false);
        }

        // Visible prose has started, so the reasoning block that preceded it is done (AC-213).
        _CloseThinkingRow();
        _AppendAssistantProse(delta.Text);
        return new(_currentAssistantRow?.Entry, false);
    }

    private TranscriptFold _OnTextCompleted(AssistantTextCompleted completed)
    {
        // A sub-agent's completed-text snapshot (some providers send one instead of streaming deltas) lands on its
        // own lane the same way the streaming path above does.
        if (_ResolveSubAgentLane(completed.ParentToolUseId) is { } lane)
        {
            lane.CurrentThinkingRow = null;
            if (lane.CurrentAssistantRow is not null)
            {
                lane.CurrentAssistantRow = null;
                return new(null, true);
            }

            return new(_Add(_NewRow(AssistantText, completed.Text), lane.Anchor).Entry, true);
        }

        if (!string.IsNullOrEmpty(completed.ParentToolUseId))
        {
            if (_currentOrphanedSubAgentTextRow is not null)
            {
                _currentOrphanedSubAgentTextRow = null;
                return default;
            }

            return new(_Add(_NewRow(AssistantText, completed.Text), parent: null).Entry, false);
        }

        _CloseThinkingRow();
        if (_currentAssistantRow is not null)
        {
            // Streaming deltas already built the text; nothing further to append.
            _currentAssistantRow = null;
            return default;
        }

        return new(_Add(_NewRow(AssistantText, completed.Text), parent: null).Entry, false);
    }

    private TranscriptFold _OnToolUse(ToolUseRequested toolUse)
    {
        // AC-146: a sub-agent's own tool call nests under its Task row instead of flattening into the top level.
        if (_ResolveSubAgentLane(toolUse.ParentToolUseId) is { } lane)
        {
            lane.CurrentAssistantRow = null;
            lane.CurrentThinkingRow = null;
            return new(_Add(_ToolUseRow(toolUse.ToolUseId, toolUse.ToolName, toolUse.InputJson), lane.Anchor).Entry, true);
        }

        // Prose that streams after this call starts a fresh row beneath it, in the order it happened — otherwise it
        // appends back onto the pre-tool row and the whole reply collapses above the tools it actually followed.
        _currentAssistantRow = null;
        _CloseThinkingRow();
        return new(_Add(_ToolUseRow(toolUse.ToolUseId, toolUse.ToolName, toolUse.InputJson), parent: null).Entry, false);
    }

    private TranscriptFold _OnToolResult(ToolResult toolResult)
    {
        // AC-146: a sub-agent's own result couples to its tool-use row inside that lane, by the same tool_use_id match.
        if (_ResolveSubAgentLane(toolResult.ParentToolUseId) is { } lane)
        {
            var nested = lane.Anchor.Children.LastOrDefault(row => _IsToolUseOf(row, toolResult.ToolUseId));
            return new((nested is null ? _Add(_ToolResultRow(toolResult), lane.Anchor) : _SetResult(nested, toolResult)).Entry, true);
        }

        // Couple the result to its tool-call row (L14), so it renders beneath the call it belongs to; with no matching
        // tool use in view (e.g. a result arriving first), fall back to a row of its own.
        var toolUseRow = _rows.LastOrDefault(row => _IsToolUseOf(row, toolResult.ToolUseId));
        return new((toolUseRow is null ? _Add(_ToolResultRow(toolResult), parent: null) : _SetResult(toolUseRow, toolResult)).Entry, false);
    }

    private TranscriptFold _OnPermission(PermissionRequested permission)
    {
        // AC-146: a sub-agent's call can need approval too, on a row nested under its Task anchor.
        var row = _rows.LastOrDefault(candidate => candidate.Entry.ToolUseId == permission.ToolUseId)
            ?? _ResolveSubAgentLane(permission.ParentToolUseId)?.Anchor.Children.LastOrDefault(candidate => candidate.Entry.ToolUseId == permission.ToolUseId);

        // AC-215: a pre-authorized tool of a self-driving run is allowed here rather than asked of nobody.
        if (tryAutoAllow(permission))
        {
            if (row is not null)
            {
                _Set(row, entry => entry with { PermissionDecision = "Allowed", IsPendingPermission = false });
            }

            return new(row?.Entry, false);
        }

        // AC-996: a permission whose tool-use row never arrived gets a row of its own; the event carries all it needs.
        row ??= _Add(_ToolUseRow(permission.ToolUseId, permission.ToolName, permission.InputJson), parent: null);
        _Set(row, entry => entry with { IsPendingPermission = true });
        return new(row.Entry, false);
    }

    private TranscriptFold _OnTurnCompleted(TurnCompleted turn)
    {
        Row? row = null;

        // Only a failed turn gets a row — a plain "Turn completed (success)" row is noise (T4). AC-1031: a stop the
        // operator asked for is not a driver failure, so it gets none of the failure card's severity.
        if (turn.IsError && interruptRequested())
        {
            row = _Add(_NewRow(TurnCompletedKind, "Interrupted."), parent: null);
        }
        else if (turn.IsError)
        {
            // AC-720/AC-939: the provider's own reason, classified like a driver error; the subtype is dropped from
            // the title once it contradicts the failure ("success") or a recognised reason already names it.
            var reason = turn.Errors is { Count: > 0 } errors ? string.Join('\n', errors) : null;
            var errorKind = reason is null ? SessionErrorKind.Unknown : SessionErrorClassifier.Classify(reason);
            var title = turn.Subtype == "success" || errorKind != SessionErrorKind.Unknown ? "Turn failed" : $"Turn failed ({turn.Subtype})";
            var failed = _NewRow(TurnCompletedKind, reason is null ? title : $"{title}: {reason}");
            failed.Entry = failed.Entry with { IsFailedTurnRow = true, ErrorKind = _Stored(errorKind) };
            row = _Add(failed, parent: null);
        }

        // A sub-agent does not outlive the turn that spawned it (AC-146): a fresh Task call next turn gets a fresh lane.
        _currentAssistantRow = null;
        _CloseThinkingRow();
        _subAgentLanes.Clear();
        _currentOrphanedSubAgentTextRow = null;
        return new(row?.Entry, false);
    }

    private TranscriptFold _OnSessionError(SessionError error)
    {
        // AC-720: trust a driver that classified itself; otherwise fall back to the text heuristic.
        var row = _NewRow(ErrorKind, error.Message);
        var kind = error.Kind == SessionErrorKind.Unknown ? SessionErrorClassifier.Classify(error.Message) : error.Kind;
        row.Entry = row.Entry with { ErrorKind = _Stored(kind), RetryAfter = error.RetryAfter };
        return new(_Add(row, parent: null).Entry, false);
    }

    private TranscriptFold _OnThinking(AssistantThinkingDelta thinkingDelta)
    {
        if (string.IsNullOrEmpty(thinkingDelta.Thinking))
        {
            return default;
        }

        // AC-146: a sub-agent's own reasoning stays in its lane, same rule as its text.
        if (_ResolveSubAgentLane(thinkingDelta.ParentToolUseId) is { } lane)
        {
            if (lane.CurrentThinkingRow is null || thinkingDelta.BlockIndex != lane.CurrentThinkingBlockIndex)
            {
                lane.CurrentThinkingRow = _Add(_NewRow(Thinking, string.Empty), lane.Anchor);
                lane.CurrentThinkingBlockIndex = thinkingDelta.BlockIndex;
            }

            _Append(lane.CurrentThinkingRow, thinkingDelta.Thinking);
            return new(lane.CurrentThinkingRow.Entry, true);
        }

        if (_currentThinkingRow is null || thinkingDelta.BlockIndex != _currentThinkingBlockIndex)
        {
            _currentThinkingRow = _Add(_NewRow(Thinking, string.Empty), parent: null);
            _currentThinkingBlockIndex = thinkingDelta.BlockIndex;
        }

        _Append(_currentThinkingRow, thinkingDelta.Thinking);
        return new(_currentThinkingRow.Entry, false);
    }

    // Ends the streaming reasoning row (AC-213) so the next thinking block, or the next turn, opens a fresh row.
    private void _CloseThinkingRow()
    {
        _currentThinkingRow = null;
        _currentThinkingBlockIndex = -1;
    }

    // Null for a top-level event (no parent id) or one naming a parent this pane never saw the tool-use row for (AC-146).
    private SubAgentLane? _ResolveSubAgentLane(string? parentToolUseId)
    {
        if (string.IsNullOrEmpty(parentToolUseId))
        {
            return null;
        }

        if (_subAgentLanes.TryGetValue(parentToolUseId, out var lane))
        {
            return lane;
        }

        if (_rows.LastOrDefault(row => _IsToolUseOf(row, parentToolUseId)) is not { } anchor)
        {
            return null;
        }

        lane = new SubAgentLane(anchor);
        _subAgentLanes[parentToolUseId] = lane;
        return lane;
    }

    private static bool _IsToolUseOf(Row row, string toolUseId) =>
        row.Entry.Kind == ToolUse && row.Entry.ToolUseId == toolUseId;

    // AC-1238: a row that grows while the virtualising panel has it realised makes that panel lose its own anchor,
    // so finished markdown blocks become their own rows and the row that grows is always the last and small.
    private void _AppendAssistantProse(string delta)
    {
        var pending = delta;
        while (pending.Length > 0)
        {
            var row = _OpenAssistantRow();
            var end = _FinishedBlockEnd(row.Entry.Text, pending);
            if (end < 0)
            {
                // AC-1265: a fence carries no blank line for the split above to find, so a code block used to grow
                // as one row well past the viewport — the one shape AC-1238's guarantee never covered.
                var fenceEnd = _OpenFenceLineEnd(row.Entry, pending);
                if (fenceEnd >= 0)
                {
                    _Append(row, pending[..fenceEnd]);
                    _SealCodeSpanRow(row);
                    pending = pending[fenceEnd..];
                    continue;
                }

                // AC-1272: nor does a table have a blank line, and its continuation carries neither header nor
                // separator -- bounded by row count like a fence, sharing the group's column widths (route A2).
                var tableEnd = _OpenTableLineEnd(row.Entry, pending);
                if (tableEnd >= 0)
                {
                    _Append(row, pending[..tableEnd]);
                    _SealTableSpanRow(row);
                    pending = pending[tableEnd..];
                    continue;
                }

                // AC-1271: and neither does a tight list or one unbroken paragraph. `_UnbrokenBlockEnd` still leaves
                // a table alone -- the row-bound above is the only thing allowed to cut one.
                var bound = _UnbrokenBlockEnd(row.Entry, pending);
                if (bound < 0)
                {
                    _Append(row, pending);
                    return;
                }

                _Append(row, pending[..bound]);
                _assistantBlockSealed = true;
                _codeSpanRows = null;
                _FinalizeTableSpan();
                pending = pending[bound..];
                continue;
            }

            _Append(row, pending[..end]);
            _assistantBlockSealed = true;
            _codeSpanRows = null;
            _FinalizeTableSpan();
            pending = pending[end..];
        }
    }

    // Ends this row inside the fence it is in and lines the next one up to carry on inside it. Both sides are
    // recorded outright: the row that opened the fence is not the row that closes it.
    private void _SealCodeSpanRow(Row row)
    {
        _Set(row, entry => entry with { EndsInsideCodeBlock = true });
        _assistantBlockSealed = true;
        _nextRowContinuesCodeBlock = true;
        _codeSpanRows ??= [row];
    }

    // The table equivalent of `_SealCodeSpanRow` above.
    private void _SealTableSpanRow(Row row)
    {
        _Set(row, entry => entry with { EndsInsideTable = true });
        _assistantBlockSealed = true;
        _nextRowContinuesTable = true;
        _tableSpanRows ??= [row];
    }

    // A table's shared column widths can only be final once every fragment's own text is fixed, and a fragment
    // sealed earlier is not touched again otherwise -- this asks each one to look at the whole table once more.
    private void _FinalizeTableSpan()
    {
        if (_tableSpanRows is null)
        {
            return;
        }

        foreach (var spanRow in _tableSpanRows)
        {
            _Set(spanRow, entry => entry with { TableSpanRevision = entry.TableSpanRevision + 1 });
        }

        _tableSpanRows = null;
    }

    // The row this reply is currently streaming into: the open one until a blank line finished it, a new one
    // after that. A continuation carries neither the badge nor the name, so the group still reads as one reply.
    private Row _OpenAssistantRow()
    {
        if (_currentAssistantRow is { } open && !_assistantBlockSealed)
        {
            return open;
        }

        var previous = _assistantBlockSealed ? _currentAssistantRow : null;
        var continuing = previous is not null;
        if (previous is not null)
        {
            _Set(previous, entry => entry with { ReplyContinuesBelow = true });
        }
        else
        {
            // A reply that ended mid-fence or mid-table must not hand its open one to the next reply (AC-1265, AC-1272).
            _codeSpanRows = null;
            _nextRowContinuesCodeBlock = false;
            _FinalizeTableSpan();
            _nextRowContinuesTable = false;
        }

        var row = _NewRow(AssistantText, string.Empty);
        row.Entry = row.Entry with
        {
            StartsReply = !continuing,
            IsReplyContinuation = continuing,
            StartsInsideCodeBlock = continuing && _nextRowContinuesCodeBlock,
            StartsInsideTable = continuing && _nextRowContinuesTable,
        };

        if (row.Entry.StartsInsideCodeBlock)
        {
            _codeSpanRows?.Add(row);
        }

        if (row.Entry.StartsInsideTable)
        {
            _tableSpanRows?.Add(row);
        }

        _nextRowContinuesCodeBlock = false;
        _nextRowContinuesTable = false;
        _currentAssistantRow = row;
        _assistantBlockSealed = false;
        return _Add(row, parent: null);
    }

    // Where in `pending` the blank line that finishes a markdown block ends, or -1 while the row is still inside
    // one. A blank line inside a fenced code block finishes nothing, so the fences opened before it are counted.
    private static int _FinishedBlockEnd(string existing, string pending)
    {
        var from = 0;
        while (true)
        {
            var at = pending.IndexOf("\n\n", from, StringComparison.Ordinal);
            if (at < 0)
            {
                return -1;
            }

            var end = at + 2;
            if (_OpenFence(pending[..end], _OpenFence(existing)) is null)
            {
                return end;
            }

            from = end;
        }
    }

    // Where in `pending` an already-counted line total reaches `maxLines`, or -1 while it may keep growing.
    private static int _LineBoundEnd(string pending, int existingLines, int maxLines)
    {
        var lines = existingLines;
        for (var i = 0; i < pending.Length; i++)
        {
            if (pending[i] == '\n' && ++lines >= maxLines)
            {
                return i + 1;
            }
        }

        return -1;
    }

    // The end of the line in `pending` that takes the open row to its line bound while a fence is still open,
    // or -1 while it may keep growing. A fence opened inside `pending` is left to the next chunk.
    private static int _OpenFenceLineEnd(TranscriptSnapshotEntry row, string pending)
    {
        // The row's own text is not enough: a row continuing a split fence carries no opener, so reading only
        // its text says the fence is closed and that second fragment then grows without a bound of its own.
        var existing = row.Text;
        return _OpenFence(existing, row.StartsInsideCodeBlock ? '`' : null) is null
            ? -1
            : _LineBoundEnd(pending, existing.AsSpan().Count('\n'), CodeBlockRowLines);
    }

    // The end of the line in `pending` that takes the open row to its table-row bound, or -1 while it may keep
    // growing (including while it is not yet known to be a table at all). The one path allowed to cut a table.
    private static int _OpenTableLineEnd(TranscriptSnapshotEntry row, string pending)
    {
        // An open fence is `_OpenFenceLineEnd`'s to bound -- without this, a pasted table example inside a
        // still-streaming fence reads as a real table and the fence's own continuation is abandoned mid-block.
        if (_OpenFence(row.Text, row.StartsInsideCodeBlock ? '`' : null) is not null)
        {
            return -1;
        }

        var open = row.StartsInsideTable ? row.Text : _OpenBlockText(row.Text);
        return !row.StartsInsideTable && !_OpenBlockIsTable(open)
            ? -1
            : _LineBoundEnd(pending, open.AsSpan().Count('\n'), TableFragmentLines);
    }

    // Where in `pending` a block that will not end by itself gives the row up, or -1 while it may keep growing.
    // A line boundary by preference, so a list item is never cut in half; a word boundary only when the block
    // holds no line at all, which is the one paragraph shape that has no other seam to use.
    private static int _UnbrokenBlockEnd(TranscriptSnapshotEntry row, string pending)
    {
        // An open fence is `_OpenFenceLineEnd`'s to bound. Cutting one here would end the row without sealing it.
        if (_OpenFence(row.Text, row.StartsInsideCodeBlock ? '`' : null) is not null)
        {
            return -1;
        }

        var open = _OpenBlockText(row.Text);
        var over = UnbrokenBlockCharacters - open.Length;
        if (over >= pending.Length)
        {
            return -1;
        }

        var from = Math.Max(0, over);
        if (open.Contains('\n', StringComparison.Ordinal) || pending.IndexOf('\n', 0) >= 0)
        {
            var line = pending.IndexOf('\n', from);
            // A table is the one multi-line block this must leave alone: its continuation carries neither the
            // header nor the separator row, so the fragment falls back to prose and the columns are gone.
            return line < 0 || _OpenBlockIsTable(open + pending[..(line + 1)]) ? -1 : line + 1;
        }

        var word = pending.IndexOf(' ', from);
        return word < 0 ? -1 : word + 1;
    }

    // The block this row is currently inside: everything after the last blank line.
    private static string _OpenBlockText(string text)
    {
        var last = text.LastIndexOf("\n\n", StringComparison.Ordinal);
        return last < 0 ? text : text[(last + 2)..];
    }

    // Asked only at the moment a split would happen, not per streamed chunk: the parser walks the whole open block.
    private static bool _OpenBlockIsTable(string open) =>
        MarkdownParser.Parse(open) is [.., { Kind: MarkdownBlockKind.Table }];

    // ponytail: rescans the whole open row with Split per chunk — O(n²) and one allocation each; track fence state on the row if it shows up.
    private static char? _OpenFence(string text, char? open = null)
    {
        foreach (var line in text.Split('\n'))
        {
            var content = line.AsSpan();
            var indent = 0;
            while (indent < content.Length && indent < 3 && content[indent] == ' ')
            {
                indent++;
            }

            content = content[indent..];
            if (content.Length >= 3
                && content[0] is '`' or '~'
                && content[1] == content[0]
                && content[2] == content[0])
            {
                open = open == content[0] ? null : open ?? content[0];
            }
        }

        return open;
    }

    // `Unknown` is the default every ordinary row carries, so it is stored as nothing (AC-1090).
    private static SessionErrorKind? _Stored(SessionErrorKind kind) => kind == SessionErrorKind.Unknown ? null : kind;

    // Clamped the way a row always clamped its text (AC-1088); a standalone result row also says what it measured.
    private Row _NewRow(string kind, string text)
    {
        var clamped = ToolOutputBudget.Clamp(text);
        var entry = new TranscriptSnapshotEntry(Guid.NewGuid().ToString("n"), kind, clamped, null, null, null, null, false, time.GetLocalNow())
        {
            TruncatedFromChars = kind == ToolResultKind && text.Length > clamped.Length ? text.Length : 0,
        };

        return new Row(entry, text.Length);
    }

    // The row for a tool call — the same shape wherever one is built, top-level or in a sub-agent lane.
    private Row _ToolUseRow(string toolUseId, string toolName, string inputJson)
    {
        var row = _NewRow(ToolUse, $"Tool: {toolName}({inputJson})");
        // Clamped as the row always clamped it (AC-1088): a `Write` carries the whole file it is about to write.
        row.Entry = row.Entry with { ToolUseId = toolUseId, ToolName = toolName, InputJson = ToolOutputBudget.Clamp(inputJson) };
        return row;
    }

    // AC-1088 carries the call id onto the orphan row too: without it the whole output cannot be found back.
    private Row _ToolResultRow(ToolResult toolResult)
    {
        var row = _NewRow(ToolResultKind, toolResult.Content);
        row.Entry = row.Entry with { IsResultError = toolResult.IsError, ToolUseId = toolResult.ToolUseId };
        return row;
    }

    private Row _SetResult(Row row, ToolResult toolResult)
    {
        var clamped = ToolOutputBudget.Clamp(toolResult.Content);
        _Set(row, entry => entry with
        {
            IsResultError = toolResult.IsError,
            BackgroundTaskId = BackgroundTaskAnnouncement.TaskId(toolResult.Content),
            TruncatedFromChars = toolResult.Content.Length > clamped.Length ? toolResult.Content.Length : 0,
            ResultText = clamped,
        });
        return row;
    }

    // Streamed text arrives delta by delta, so the cap holds on the running total, and the count carried here keeps
    // the marker naming the whole rather than what survived the last clamp.
    private void _Append(Row row, string delta)
    {
        row.FullTextChars += delta.Length;
        _Set(row, entry => entry with { Text = ToolOutputBudget.Clamp(entry.Text + delta, row.FullTextChars) });
    }

    private void _Set(Row row, Func<TranscriptSnapshotEntry, TranscriptSnapshotEntry> change)
    {
        var next = change(row.Entry);
        if (next == row.Entry)
        {
            return;
        }

        row.Entry = next;
        _Publish(row);
    }

    private Row _Add(Row row, Row? parent, bool publish = true)
    {
        row.Parent = parent;
        (parent?.Children ?? _rows).Add(row);
        _Register(row);
        if (publish)
        {
            _Publish(row);
        }

        return row;
    }

    private void _Register(Row row)
    {
        _byId[row.Entry.Id] = row;
        foreach (var child in row.Children)
        {
            _Register(child);
        }
    }

    // A nested row changing is its anchor changing: the store keeps a sub-agent's rows inside the anchor (AC-1090).
    private void _Publish(Row row)
    {
        var top = row;
        while (top.Parent is { } parent)
        {
            top = parent;
        }

        top.Version++;
        changed(top.Version, _Snapshot(top));
    }

    private static TranscriptSnapshotEntry _Snapshot(Row row) => row.Children.Count == 0
        ? row.Entry
        : row.Entry with { SubAgentRows = [.. row.Children.Select(_Snapshot)] };

    private static Row _Seed(TranscriptSnapshotEntry entry)
    {
        var row = new Row(entry with { SubAgentRows = null }, entry.Text.Length);
        foreach (var nested in entry.SubAgentRows ?? [])
        {
            var child = _Seed(nested);
            child.Parent = row;
            row.Children.Add(child);
        }

        return row;
    }

    // True when anything changed. A nested row this build never saw is added under the row that now carries it.
    private bool _Adopt(Row row, TranscriptSnapshotEntry entry)
    {
        var own = entry with { SubAgentRows = null };
        var changedAny = own != row.Entry;
        if (own.Text != row.Entry.Text)
        {
            row.FullTextChars = own.Text.Length;
        }

        row.Entry = own;
        foreach (var nested in entry.SubAgentRows ?? [])
        {
            if (_byId.TryGetValue(nested.Id, out var child))
            {
                changedAny |= _Adopt(child, nested);
                continue;
            }

            _Add(_Seed(nested), row, publish: false);
            changedAny = true;
        }

        return changedAny;
    }
}
