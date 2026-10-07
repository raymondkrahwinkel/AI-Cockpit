using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Plugins.OpenAiCompat;

// One user turn over an OpenAI-compatible chat client (AC-1431). Linked as source into both the host's own
// OpenAiCompatSessionDriver (Ollama, LM Studio) and this family's OpenAiCompatPluginSessionDriver, so the history,
// the continuation net, the transport retry and the tool-loop bound exist once for every HTTP chat route.
internal static class ChatTurnLoop
{
    // Explicit rather than the library default, so a model that keeps calling tools cannot loop without end.
    internal const int MaxToolIterations = 40;

    internal const int MaxTransportRetries = 2;

    internal const string CompletionMarker = "[turn complete]";

    // The convention the continuation net reads: a structured end-of-turn signal the model gives itself,
    // instead of the driver guessing from the wording whether an answer was a result or an announcement.
    internal const string CompletionConvention =
        "When a task needs tools, keep calling them until the task is done: if you announce a next step, make that tool call in the same reply. "
        + "End your final reply of the turn — the task done, or a question you need answered — with the line " + CompletionMarker + ".";

    // A user message rather than a system one: several chat templates (Qwen among them) reject a system message
    // anywhere but at the front of the conversation.
    internal const string ContinuationNudge =
        "(cockpit) Your reply ended without a tool call and without " + CompletionMarker + ". "
        + "Carry out the step you announced now, or finish and end with " + CompletionMarker + ".";

    // ponytail: a fixed character cap on tool results older than the previous turn keeps the resent history from
    // growing without end; no token count and no summarising, so a long session can still reach the context limit.
    internal const int MaxOldToolResultChars = 1500;

    private const string TruncationNote = "[truncated by cockpit: ";

    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(1);

    internal static void ConfigureToolLoop(FunctionInvokingChatClient client) => client.MaximumIterationsPerRequest = MaxToolIterations;

    internal static string? WithCompletionConvention(string? systemPrompt, bool toolsOffered)
    {
        if (!toolsOffered)
        {
            return systemPrompt;
        }

        return string.IsNullOrWhiteSpace(systemPrompt) ? CompletionConvention : $"{systemPrompt}\n\n{CompletionConvention}";
    }

    // Runs the turn, appends every message it produced — tool calls and their results included — to `history`,
    // and streams its text through `onText`. Exceptions other than a retried transport failure propagate.
    internal static async Task<ChatTurnOutcome> RunAsync(IChatClient agent, List<ChatMessage> history, ChatOptions options, Action<string> onText, ILogger? logger, CancellationToken cancellationToken)
    {
        logger ??= NullLogger.Instance;
        _TrimOldToolResults(history);
        var guard = agent.GetService<ContextGuardChatClient>();
        var limits = guard?.Limits ?? ChatTurnLimits.Default;
        guard?.BeginTurn();
        var turnText = new StringBuilder();
        var toolRounds = 0;

        for (var continuation = 0; ; continuation++)
        {
            var round = await _RunRoundAsync(agent, history, options, turnText, onText, logger, cancellationToken).ConfigureAwait(false);
            toolRounds += round.ToolRounds;

            logger.LogDebug("Chat turn round {Round}: finish_reason {FinishReason}, {ToolRounds} tool round(s) so far.", continuation + 1, round.FinishReason?.Value ?? "(none)", toolRounds);
            if (round.FinishReason == ChatFinishReason.Length)
            {
                logger.LogWarning("Chat turn round {Round} was cut off by the output-token limit (finish_reason length).", continuation + 1);
            }

            // A reply without a tool call ends the turn; the marker is only a net for a model that stops on an
            // announcement after using tools. A turn the guard already wound down is never nudged back on (AC-1489).
            var endedEarly = round.FinishReason == ChatFinishReason.Length
                || (options.Tools is { Count: > 0 } && toolRounds > 0 && !round.Text.Contains(CompletionMarker, StringComparison.OrdinalIgnoreCase));
            if (!endedEarly || continuation >= limits.MaxContinuations || guard?.Turn.WoundDown == true)
            {
                guard?.LogTurn(logger);
                return new ChatTurnOutcome(turnText.ToString(), round.FinishReason, toolRounds, continuation);
            }

            history.Add(new ChatMessage(ChatRole.User, ContinuationNudge));
        }
    }

    // A turn starts at a user message that is not a continuation nudge.
    internal static int TurnStartIndex(IList<ChatMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == ChatRole.User && messages[index].Text != ContinuationNudge)
            {
                return index;
            }
        }

        return 0;
    }

    // The text a tool result goes over the wire as: a string verbatim, anything else as its JSON.
    internal static string? ResultText(object? result) => result switch
    {
        null => null,
        string value => value,
        JsonElement json => json.ValueKind == JsonValueKind.String ? json.GetString() : json.GetRawText(),
        _ => _SerializeResult(result),
    };

    private static string _SerializeResult(object result)
    {
        try
        {
            return JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return result.ToString() ?? string.Empty;
        }
    }

    private static async Task<RoundResult> _RunRoundAsync(IChatClient agent, List<ChatMessage> history, ChatOptions options, StringBuilder turnText, Action<string> onText, ILogger logger, CancellationToken cancellationToken)
    {
        var relay = new TextRelay(turnText, onText);
        var toolRounds = 0;

        for (var attempt = 0; ; attempt++)
        {
            var updates = new List<ChatResponseUpdate>();
            try
            {
                await foreach (var update in agent.GetStreamingResponseAsync(history, options, cancellationToken).ConfigureAwait(false))
                {
                    updates.Add(update);
                    relay.Add(update.Text);
                }

                relay.Flush();
                toolRounds += _Append(history, updates, complete: true);
                return new RoundResult(relay.Raw.ToString(), updates.LastOrDefault(update => update.FinishReason is not null)?.FinishReason, toolRounds);
            }
            catch (Exception ex) when (attempt < MaxTransportRetries && !cancellationToken.IsCancellationRequested && _IsTransportFailure(ex))
            {
                // Keep the tool rounds that finished, so the retry does not run them a second time.
                logger.LogWarning(ex, "Chat turn lost its connection; retrying ({Attempt}/{Max}).", attempt + 1, MaxTransportRetries);
                toolRounds += _Append(history, updates, complete: false);
                relay.DropHeld();
                await Task.Delay(RetryBaseDelay * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Appends the messages of one round in the order the function-invocation loop produced them and returns how many
    // tool rounds they hold. A call never lands without its result: an interrupted round keeps only what came before
    // its last tool message, and a call left unanswered (the iteration bound) is dropped. Blank messages are skipped.
    private static int _Append(List<ChatMessage> history, List<ChatResponseUpdate> updates, bool complete)
    {
        var messages = updates.ToChatResponse().Messages.ToList();
        if (!complete)
        {
            messages = [.. messages.Take(messages.FindLastIndex(message => message.Role == ChatRole.Tool) + 1)];
        }

        var answered = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId).ToHashSet();
        foreach (var message in messages)
        {
            message.Contents = [.. message.Contents.Where(content => content is not FunctionCallContent call || answered.Contains(call.CallId))];
            if (message.Contents.Any(content => content is not TextContent text || !string.IsNullOrWhiteSpace(text.Text)))
            {
                history.Add(message);
            }
        }

        return messages.Count(message => message.Role == ChatRole.Tool);
    }

    // Shortens the tool results of every turn before the previous one; the call and its result both stay, only the
    // result's text gets shorter. A turn starts at a user message that is not a continuation nudge.
    private static void _TrimOldToolResults(List<ChatMessage> history)
    {
        var turnStarts = history.Select((message, index) => (message, index))
            .Where(entry => entry.message.Role == ChatRole.User && entry.message.Text != ContinuationNudge)
            .Select(entry => entry.index)
            .ToList();
        if (turnStarts.Count < 3)
        {
            return;
        }

        foreach (var result in history.Take(turnStarts[^2]).SelectMany(message => message.Contents).OfType<FunctionResultContent>())
        {
            var text = result.Result switch
            {
                string value => value,
                JsonElement json => json.GetRawText(),
                _ => null,
            };
            if (text is not null && text.Length > MaxOldToolResultChars && !text.Contains(TruncationNote, StringComparison.Ordinal))
            {
                result.Result = $"{text[..MaxOldToolResultChars]}\n{TruncationNote}{text.Length} chars]";
            }
        }
    }

    // A connection that dropped mid-stream ("Unable to read data from the transport connection") surfaces as an
    // IOException, or an HttpRequestException without a status; an HTTP error answer is not a transport failure.
    private static bool _IsTransportFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is IOException or HttpRequestException { StatusCode: null })
            {
                return true;
            }
        }

        return false;
    }

    // Streams a round's text to the UI without the completion marker: that belongs to the model's own history, not
    // to the operator. A tail that could still turn into the marker (or the whitespace before it) is held until the
    // next chunk or the end of the round decides. Raw keeps the unfiltered text the continuation net reads.
    private sealed class TextRelay(StringBuilder turnText, Action<string> onText)
    {
        private readonly StringBuilder _held = new();
        private bool _emitted;

        public StringBuilder Raw { get; } = new();

        public void Add(string? delta)
        {
            if (string.IsNullOrEmpty(delta))
            {
                return;
            }

            Raw.Append(delta);
            _held.Append(delta);
            _Release(final: false);
        }

        public void Flush() => _Release(final: true);

        public void DropHeld() => _held.Clear();

        private void _Release(bool final)
        {
            var text = _held.ToString();
            var markerAt = text.IndexOf(CompletionMarker, StringComparison.OrdinalIgnoreCase);
            if (markerAt >= 0)
            {
                text = string.Concat(text.AsSpan(0, markerAt).TrimEnd(), text.AsSpan(markerAt + CompletionMarker.Length));
            }

            var hold = final ? 0 : _HeldTail(text);
            _held.Clear().Append(text, text.Length - hold, hold);
            var release = text[..^hold];
            if (release.Length == 0)
            {
                return;
            }

            // A continuation round's text starts a new paragraph instead of running on from the previous round.
            if (!_emitted && turnText.Length > 0)
            {
                release = "\n\n" + release;
            }

            _emitted = true;
            turnText.Append(release);
            onText(release);
        }

        // The length of the text's end that may still be the start of the marker, plus the whitespace before it.
        private static int _HeldTail(string text)
        {
            var prefix = Enumerable.Range(1, CompletionMarker.Length - 1).Reverse()
                .FirstOrDefault(length => text.EndsWith(CompletionMarker[..length], StringComparison.OrdinalIgnoreCase));
            var start = text.Length - prefix;
            while (start > 0 && char.IsWhiteSpace(text[start - 1]))
            {
                start--;
            }

            return text.Length - start;
        }
    }

    private sealed record RoundResult(string Text, ChatFinishReason? FinishReason, int ToolRounds);
}

internal sealed record ChatTurnOutcome(string Text, ChatFinishReason? FinishReason, int ToolRounds, int Continuations);

// The bounds the context guard and the continuation net work within (AC-1489), read from an optional `turnLimits`
// object in the provider config. The defaults follow opencode (50 KiB / 2000 lines per result, 20K reserve, 40K
// protected); the context window defaults low on purpose, since a model that has more says so in its config.
internal sealed record ChatTurnLimits
{
    internal static readonly ChatTurnLimits Default = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public int MaxToolResultChars { get; init; } = 51_200;

    public int MaxToolResultLines { get; init; } = 2_000;

    public long TurnToolBudgetChars { get; init; } = 1_048_576;

    public int ContextWindowTokens { get; init; } = 131_072;

    public double SoftLimitRatio { get; init; } = 0.75;

    public int ReserveTokens { get; init; } = 20_000;

    public int ProtectRecentTokens { get; init; } = 40_000;

    public int MaxContinuations { get; init; } = 1;

    // A missing field keeps its default; a `turnLimits` that does not parse keeps them all rather than failing the session.
    internal static ChatTurnLimits From(JsonElement? json, ILogger? logger = null)
    {
        if (json is not { ValueKind: JsonValueKind.Object } value)
        {
            return Default;
        }

        try
        {
            return value.Deserialize<ChatTurnLimits>(JsonOptions) ?? Default;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "The provider config's turnLimits did not parse; using the defaults.");
            return Default;
        }
    }
}

// Sits between the function-invocation loop and the model (AC-1489), so it sees every HTTP call of a turn before it
// leaves: caps each tool result, keeps a per-turn budget, prunes old results near the context limit, and as a last
// resort drops the tools so the model must wrap up. Only a result's text ever gets shorter: every call keeps its result.
internal sealed class ContextGuardChatClient(IChatClient inner, ChatTurnLimits limits, ILogger? logger = null) : DelegatingChatClient(inner)
{
    private const int CharsPerToken = 4;

    // What an old result keeps when it is pruned: opencode's TOOL_OUTPUT_MAX_CHARS.
    private const int PrunedResultChars = 2_000;

    private const string CutNote = "[cockpit: output cut at ";

    private const string ClearedNote = "[cleared by cockpit to stay within the context: ";

    internal const string BudgetNote =
        "(cockpit) The tool budget for this turn is used up. Report what you did and what is left, then end with " + ChatTurnLoop.CompletionMarker + ".";

    internal const string ContextFullNote =
        "(cockpit) The context is full. Report what you did and what is left, then end with " + ChatTurnLoop.CompletionMarker + ".";

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public ChatTurnLimits Limits { get; } = limits;

    public TurnGuardState Turn { get; private set; } = new();

    public void BeginTurn() => Turn = new TurnGuardState();

    public void LogTurn(ILogger turnLogger) =>
        turnLogger.LogInformation(
            "Chat turn guard: peak ~{PeakTokens} input tokens, {ToolResults} tool result(s) of {ResultChars} chars, {Cut} cut, {Pruned} pruned, budget hit {BudgetHit}, context full {ContextFull}.",
            Turn.PeakTokens,
            Turn.ToolResults,
            Turn.ResultChars,
            Turn.Cut,
            Turn.Pruned,
            Turn.BudgetHit,
            Turn.ContextFull);

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (guarded, guardedOptions) = _Guard(messages, options);
        return base.GetResponseAsync(guarded, guardedOptions, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var (guarded, guardedOptions) = _Guard(messages, options);
        return base.GetStreamingResponseAsync(guarded, guardedOptions, cancellationToken);
    }

    private (IEnumerable<ChatMessage> Messages, ChatOptions? Options) _Guard(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var turn = Turn;
        _CapNewResults(list, turn);
        if (turn.ResultChars > Limits.TurnToolBudgetChars)
        {
            turn.BudgetHit = true;
        }

        var tokens = _EstimateTokens(list, options);
        var pruned = 0L;
        if (tokens > Limits.SoftLimitRatio * Limits.ContextWindowTokens)
        {
            pruned = _Prune(list, turn);
            tokens = _EstimateTokens(list, options);
        }

        if (tokens > Limits.ContextWindowTokens - Limits.ReserveTokens)
        {
            turn.ContextFull = true;
            _logger.LogWarning("Chat turn guard: ~{Tokens} input tokens is past the context window of {Window} minus the reserve; asking the model to wrap up.", tokens, Limits.ContextWindowTokens);
        }

        turn.PeakTokens = Math.Max(turn.PeakTokens, tokens);
        var note = turn.ContextFull ? ContextFullNote : turn.BudgetHit ? BudgetNote : null;
        _logger.LogDebug(
            "Chat turn guard: ~{Tokens} input tokens, {ResultChars} result chars this turn, {Pruned} chars pruned, reason {Reason}.",
            tokens,
            turn.ResultChars,
            pruned,
            note is null ? "(none)" : turn.ContextFull ? "context full" : "budget used up");
        if (note is null || options?.Tools is not { Count: > 0 })
        {
            return (list, options);
        }

        // Without tools the model can only answer in text, so the function-invocation loop ends the turn by itself.
        var windDown = options.Clone();
        windDown.Tools = null;
        return ([.. list, new ChatMessage(ChatRole.User, note)], windDown);
    }

    // Caps each result of the running turn the first time this guard sees it, in place, so the same object in the
    // driver's history never goes out whole again; what is left after the cap counts against the turn's budget.
    private void _CapNewResults(IList<ChatMessage> messages, TurnGuardState turn)
    {
        var start = ChatTurnLoop.TurnStartIndex(messages);
        foreach (var result in messages.Skip(start).SelectMany(message => message.Contents).OfType<FunctionResultContent>())
        {
            if (!turn.Seen.Add(result))
            {
                continue;
            }

            var text = ChatTurnLoop.ResultText(result.Result) ?? string.Empty;
            var cut = _Cut(text);
            if (cut.Length < text.Length)
            {
                result.Result = $"{cut}\n{CutNote}{cut.Length} of {text.Length} chars — narrow the query, or read on from where this stopped]";
                turn.Cut++;
            }

            turn.ToolResults++;
            turn.ResultChars += cut.Length;
        }
    }

    // The head of `text` within both the character and the line cap, never splitting a surrogate pair.
    private string _Cut(string text)
    {
        var length = Math.Min(text.Length, Limits.MaxToolResultChars);
        var lines = 0;
        for (var index = 0; index < length; index++)
        {
            if (text[index] == '\n' && ++lines == Limits.MaxToolResultLines)
            {
                length = index;
                break;
            }
        }

        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    // ponytail: pruning only, no summarising model call like opencode's compaction — tool results were over 90% of
    // the prompt in the measured failure (DEP-234). Add a summary call once a measured session shows pruning falls short.
    private long _Prune(IList<ChatMessage> messages, TurnGuardState turn)
    {
        var protectedChars = (long)Limits.ProtectRecentTokens * CharsPerToken;
        var seen = 0L;
        var pruned = 0L;
        foreach (var result in messages.Reverse().SelectMany(message => message.Contents.Reverse()).OfType<FunctionResultContent>())
        {
            var text = ChatTurnLoop.ResultText(result.Result);
            if (text is null)
            {
                continue;
            }

            seen += text.Length;
            if (seen <= protectedChars || text.Length <= PrunedResultChars || text.Contains(ClearedNote, StringComparison.Ordinal))
            {
                continue;
            }

            var keep = char.IsHighSurrogate(text[PrunedResultChars - 1]) ? PrunedResultChars - 1 : PrunedResultChars;
            result.Result = $"{text[..keep]}\n{ClearedNote}{text.Length} chars — read it again if you still need it]";
            pruned += text.Length - keep;
            turn.Pruned++;
        }

        return pruned;
    }

    // The same rough four-characters-a-token the host's McpToolTokenMath uses, over every message and tool schema.
    private static long _EstimateTokens(IList<ChatMessage> messages, ChatOptions? options)
    {
        var chars = 0L;
        foreach (var content in messages.SelectMany(message => message.Contents))
        {
            chars += content switch
            {
                TextContent text => text.Text.Length,
                FunctionCallContent call => call.Name.Length + (call.Arguments is null ? 0 : JsonSerializer.Serialize(call.Arguments, AIJsonUtilities.DefaultOptions).Length),
                FunctionResultContent result => ChatTurnLoop.ResultText(result.Result)?.Length ?? 0,
                _ => 0,
            };
        }

        foreach (var tool in options?.Tools ?? [])
        {
            chars += tool.Name.Length + tool.Description.Length + (tool is AIFunctionDeclaration function ? function.JsonSchema.GetRawText().Length : 0);
        }

        return chars / CharsPerToken;
    }
}

// What the guard did in one turn: read by the continuation net (no nudge once the turn was wound down) and logged
// once at its end, as the measurements the live proof compares against opencode.
internal sealed class TurnGuardState
{
    public HashSet<FunctionResultContent> Seen { get; } = new(ReferenceEqualityComparer.Instance);

    public long ResultChars { get; set; }

    public int ToolResults { get; set; }

    public int Cut { get; set; }

    public int Pruned { get; set; }

    public long PeakTokens { get; set; }

    public bool BudgetHit { get; set; }

    public bool ContextFull { get; set; }

    public bool WoundDown => BudgetHit || ContextFull;
}
