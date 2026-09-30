using System.Text;
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
        var roundText = new StringBuilder();
        var toolRounds = 0;

        for (var attempt = 0; ; attempt++)
        {
            var updates = new List<ChatResponseUpdate>();
            try
            {
                await foreach (var update in agent.GetStreamingResponseAsync(history, options, cancellationToken).ConfigureAwait(false))
                {
                    updates.Add(update);
                    _Emit(update.Text, roundText, turnText, onText);
                }

                toolRounds += _Append(history, updates, complete: true);
                return new RoundResult(roundText.ToString(), updates.LastOrDefault(update => update.FinishReason is not null)?.FinishReason, toolRounds);
            }
            catch (Exception ex) when (attempt < MaxTransportRetries && !cancellationToken.IsCancellationRequested && _IsTransportFailure(ex))
            {
                // Keep the tool rounds that finished, so the retry does not run them a second time.
                logger.LogWarning(ex, "Chat turn lost its connection; retrying ({Attempt}/{Max}).", attempt + 1, MaxTransportRetries);
                toolRounds += _Append(history, updates, complete: false);
                await Task.Delay(RetryBaseDelay * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void _Emit(string? delta, StringBuilder roundText, StringBuilder turnText, Action<string> onText)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        // A continuation round's text starts a new paragraph instead of running on from the previous round.
        if (roundText.Length == 0 && turnText.Length > 0)
        {
            delta = "\n\n" + delta;
        }

        roundText.Append(delta);
        turnText.Append(delta);
        onText(delta);
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

    private sealed record RoundResult(string Text, ChatFinishReason? FinishReason, int ToolRounds);
}

internal sealed record ChatTurnOutcome(string Text, ChatFinishReason? FinishReason, int ToolRounds, int Continuations);
