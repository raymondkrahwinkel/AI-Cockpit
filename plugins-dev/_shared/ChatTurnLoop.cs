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

    internal const int MaxContinuations = 2;

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

            var endedEarly = round.FinishReason == ChatFinishReason.Length
                || (options.Tools is { Count: > 0 } && toolRounds > 0 && !round.Text.Contains(CompletionMarker, StringComparison.OrdinalIgnoreCase));
            if (!endedEarly || continuation == MaxContinuations)
            {
                return new ChatTurnOutcome(turnText.ToString(), round.FinishReason, toolRounds, continuation);
            }

            history.Add(new ChatMessage(ChatRole.User, ContinuationNudge));
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
