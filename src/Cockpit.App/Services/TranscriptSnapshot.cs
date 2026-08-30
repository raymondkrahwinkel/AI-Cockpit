using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.App.Services;

// Turns a transcript row into the durable form `ISessionTranscriptStore` keeps, and back (AC-1090). Lived in
// `AssistantSessionHost` while the assistant was the only pane with a saved transcript (AC-684); moved here
// unchanged in substance, because both sides now use the same layer.
internal static class TranscriptSnapshot
{
    public static TranscriptSnapshotEntry Capture(TranscriptEntryViewModel entry) => new(
        entry.Id,
        entry.Kind.ToString(),
        entry.Text,
        entry.ToolName,
        _QuestionInputJsonWithAnswers(entry),
        entry.ToolUseId,
        entry.ResultText,
        entry.IsResultError,
        entry.Timestamp)
    {
        SubAgentRows = entry.HasSubAgentRows ? [.. entry.SubAgentRows.Select(Capture)] : null,
        PermissionDecision = entry.PermissionDecision,

        // Written only when it is something: `Unknown` is the default every ordinary row carries, and a field on
        // every line is exactly the kind of weight this log is supposed to stop paying.
        ErrorKind = entry.ErrorKind == SessionErrorKind.Unknown ? null : entry.ErrorKind,
        RetryAfter = entry.RetryAfter,
        IsFailedTurnRow = entry.IsFailedTurnRow,
        ReplyToId = entry.ReplyTo?.Id,
        LatestReplyId = entry.LatestReply?.Id,
        BackgroundTaskId = entry.BackgroundTaskId,
    };

    // Rebuilds rows in the order they were recorded. A row this build cannot make sense of is skipped, the same
    // contract `SessionStateStore` uses for a line it cannot parse.
    public static IReadOnlyList<TranscriptEntryViewModel> Restore(IReadOnlyList<TranscriptSnapshotEntry> records)
    {
        var restored = new List<TranscriptEntryViewModel>(records.Count);
        var byId = new Dictionary<string, TranscriptEntryViewModel>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            // A reply always follows the row it answers, so a single forward pass resolves `ReplyTo` — which is
            // init-only and has to be known before the row exists.
            if (_Restore(record, record.ReplyToId is { } replyTo ? byId.GetValueOrDefault(replyTo) : null) is not { } entry)
            {
                continue;
            }

            restored.Add(entry);
            byId[record.Id] = entry;
        }

        // `LatestReply` points the other way — at a row further down — so it needs the whole map.
        foreach (var record in records.Where(record => record.LatestReplyId is not null))
        {
            if (byId.TryGetValue(record.Id, out var entry) && byId.TryGetValue(record.LatestReplyId!, out var reply))
            {
                entry.LatestReply = reply;
            }
        }

        return restored;
    }

    private static TranscriptEntryViewModel? _Restore(TranscriptSnapshotEntry record, TranscriptEntryViewModel? replyTo)
    {
        if (!Enum.TryParse<TranscriptEntryKind>(record.Kind, out var kind))
        {
            return null;
        }

        var entry = new TranscriptEntryViewModel(kind, record.Text, record.Timestamp)
        {
            Id = record.Id,
            ToolName = record.ToolName,
            InputJson = record.InputJson,
            ToolUseId = record.ToolUseId,
            ReplyTo = replyTo,
            IsFailedTurnRow = record.IsFailedTurnRow,
            PermissionDecision = record.PermissionDecision,
            ErrorKind = record.ErrorKind ?? SessionErrorKind.Unknown,
            RetryAfter = record.RetryAfter,
        };

        if (record.ResultText is not null)
        {
            entry.SetResult(record.ResultText, record.IsResultError);
        }

        // After `SetResult`, which derives the id from the result text: the recorded value is the one that was
        // actually in play, and survives a result this build no longer holds in full.
        entry.BackgroundTaskId = record.BackgroundTaskId ?? entry.BackgroundTaskId;

        foreach (var nested in record.SubAgentRows ?? [])
        {
            if (_Restore(nested, replyTo: null) is { } nestedEntry)
            {
                entry.SubAgentRows.Add(nestedEntry);
            }
        }

        // AC-955: replays with its options and, if answered, its answer, read-only — not a blank row for a
        // call already responded to. Reparsed here, not a snapshot field of its own: `InputJson` already is
        // the question payload, the same parse `PermissionRequested` runs live.
        if (kind == TranscriptEntryKind.Question && AskUserQuestionViewModel.Parse(record.InputJson) is { Count: > 0 } prompts)
        {
            entry.QuestionPrompts = prompts;
            _ApplySavedAnswers(prompts, record.InputJson);
        }

        return entry;
    }

    // AC-955: ticked options and typed "Other" text do not otherwise survive a restart — `InputJson` alone is
    // the question asked, not what was picked. Merged in under `answers`, keyed by question text (same key
    // `ClaudeControlProtocol._BuildUpdatedInput` uses for the CLI, a different shape).
    private static string? _QuestionInputJsonWithAnswers(TranscriptEntryViewModel entry)
    {
        if (entry.Kind != TranscriptEntryKind.Question
            || entry.QuestionPrompts is not { Count: > 0 } prompts
            || string.IsNullOrWhiteSpace(entry.InputJson))
        {
            return entry.InputJson;
        }

        var answered = prompts.Where(prompt => prompt.IsAnswered).ToList();
        if (answered.Count == 0)
        {
            return entry.InputJson;
        }

        try
        {
            var root = JsonNode.Parse(entry.InputJson)!.AsObject();
            var answers = new JsonObject();
            foreach (var prompt in answered)
            {
                var picked = new JsonObject
                {
                    ["options"] = new JsonArray([.. prompt.Options.Where(option => option.IsSelected)
                        .Select(option => (JsonNode)JsonValue.Create(option.Label))]),
                };

                if (prompt.IsOtherSelected)
                {
                    picked["other"] = prompt.OtherText;
                }

                answers[prompt.Question] = picked;
            }

            root["answers"] = answers;
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return entry.InputJson;
        }
    }

    private static void _ApplySavedAnswers(IReadOnlyList<AskUserQuestionViewModel> prompts, string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (!document.RootElement.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var prompt in prompts)
            {
                if (!answers.TryGetProperty(prompt.Question, out var picked) || picked.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (picked.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                {
                    var labels = options.EnumerateArray()
                        .Where(option => option.ValueKind == JsonValueKind.String)
                        .Select(option => option.GetString())
                        .ToHashSet(StringComparer.Ordinal);

                    foreach (var option in prompt.Options.Where(option => labels.Contains(option.Label)))
                    {
                        option.IsSelected = true;
                    }
                }

                if (picked.TryGetProperty("other", out var other) && other.ValueKind == JsonValueKind.String)
                {
                    prompt.OtherText = other.GetString() ?? string.Empty;
                    prompt.IsOtherSelected = true;
                }

                prompt.IsAnswered = true;
            }
        }
        catch (JsonException)
        {
            // Not a payload worth restoring an answer from — the card still renders, just unanswered.
        }
    }
}
