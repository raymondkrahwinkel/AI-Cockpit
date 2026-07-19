using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="ITranscriptCleanupService"/> backed by any local OpenAI-compatible LLM server via the shared
/// <see cref="IChatClientFactory"/> (the OpenAI SDK over <c>{base}/v1</c>) — Ollama and LM Studio both serve
/// this, so one code path covers a laptop on Ollama and a desktop on LM Studio, and STT cleanup and read-aloud
/// naturalize/summarize all share it. Ported from WisperFlow's <c>cleanup.py</c> safety net (research:
/// Cockpit-DotNet-Voice-Stack-2026-07-07.md §4): too-short input skips the call entirely, and any failure or
/// suspicious-looking output falls back to the raw transcript rather than surfacing an error or risking a
/// hallucinated result reaching the session.
/// <para>
/// The call is bounded on three axes so a local server can never stall the voice pipeline (measured against LM
/// Studio 2026-07-19): a hard <c>MaxOutputTokens</c> cap (one uncapped generation held the server's single
/// generation slot for 84s while everything queued behind it — and cancelling the client does not stop the
/// server generating, so capping is the real fix), a per-call timeout that falls back to the raw text, and a
/// single-flight gate so the app never fires overlapping calls that just pile up in the server's queue.
/// </para>
/// </summary>
internal sealed class OpenAiCompatTranscriptCleanupService(IChatClientFactory chatClientFactory, IVoiceSettingsStore settingsStore, ILocalLlmEndpointResolver endpointResolver, ILogger<OpenAiCompatTranscriptCleanupService> logger)
    : ITranscriptCleanupService, ISingletonService
{
    private const string SystemPrompt =
        "You clean up a raw speech-to-text transcript for insertion as typed text. Add punctuation " +
        "where a pause implies it, remove filler words (uh, um, you know), add a question mark for " +
        "questions. Keep the original language and meaning exactly — do not translate, summarize, or " +
        "add content. Reply with only the cleaned text, nothing else.";

    // STT cleanup blocks the transcript reaching the input box, so it runs on a tighter leash than read-aloud,
    // which the operator is only listening to. Both are generous enough for a warm generation plus a one-off
    // just-in-time model load, and short enough that a wedged server degrades to the fallback quickly.
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SpeechTimeout = TimeSpan.FromSeconds(20);

    // A turn-start acknowledgement is only worth speaking if it is near-instant; past a few seconds the preset
    // phrase would have been the better call, so time out fast and let the caller fall back.
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(4);

    // One shared local server serves every voice-LLM call and generates one response at a time; overlapping
    // calls only stack in its queue. Serializing here keeps the app from adding to that pile-up — a timeout or
    // barge-in on the in-flight call frees the slot for the next.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static string SpeechPrompt(string readAloudLanguage) =>
        "You turn assistant text into what a person would say out loud when explaining it to someone. " +
        "Rewrite it as short, natural spoken sentences. Leave out code, file paths, URLs, command names and " +
        "markdown; mention them in plain words only when they matter. Never read symbols, brackets or " +
        "punctuation literally, and do not spell things out. " + _LanguageDirective(readAloudLanguage) + " " +
        "Mark the language of each part with [[nl]] before Dutch text and [[en]] before English text, and " +
        "switch the marker whenever the language changes, even for a single word or phrase. Begin with the " +
        "marker for the first language. Reply with only the marked spoken text — no preamble, no quotes, no bullet lists.";

    private static string SummaryPrompt(string readAloudLanguage) =>
        "You summarize assistant text so it can be heard, not read — give the listener the gist in far fewer " +
        "words. Rewrite it as short, natural spoken sentences. Leave out code, file paths, URLs, command names " +
        "and markdown; mention them in plain words only when they matter. Never read symbols, brackets or " +
        "punctuation literally. Keep it shorter than the original, but this is a hard rule: preserve every " +
        "number, name, decision, warning and action item exactly — never drop, round, soften or invent any of " +
        "them. When in doubt, keep it. " + _LanguageDirective(readAloudLanguage) + " Mark the language of each part " +
        "with [[nl]] before Dutch text and [[en]] before English text, switching the marker whenever the language " +
        "changes, even for a single word or phrase. Begin with the marker for the first language. Reply with only " +
        "the marked spoken summary — no preamble, no quotes, no bullet lists.";

    private static string AckPrompt(string readAloudLanguage) =>
        "You briefly acknowledge, in one short spoken sentence, that you are about to start on the user's request — " +
        "like \"Let me take a look\" or \"I'll figure that out\". Do not answer the request, ask anything, restate it, " +
        "or explain; only acknowledge that you are starting. " + _LanguageDirective(readAloudLanguage) + " Reply with " +
        "only the one sentence — no preamble, no quotes.";

    // The read-aloud preferred-language nudge: lean to the operator's chosen base language while keeping code,
    // names and genuinely foreign phrases in their own language (still tagged, so they are pronounced right).
    private static string _LanguageDirective(string readAloudLanguage) => readAloudLanguage.ToLowerInvariant() switch
    {
        "nl" => "Speak mainly in Dutch, but keep code, identifiers, file names, commands, quotes and genuinely English technical terms in English.",
        "en" => "Speak mainly in English, but keep code, identifiers, file names, commands, quotes and genuinely Dutch phrases in Dutch.",
        _ => "Keep the meaning and the original language.",
    };

    private static readonly TranscriptCleanupOptions Options = new();

    public async Task<string> CleanupAsync(string rawText, CancellationToken cancellationToken = default)
    {
        if (TranscriptCleanupGuard.ShouldSkipCleanup(rawText, Options))
        {
            return rawText;
        }

        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Temperature 0 + a fixed seed: cleanup must be deterministic, so the same utterance is typed the same way.
            var cleaned = (await _CompleteAsync(settings, SystemPrompt, $"Input: {rawText}\nOutput:", temperature: 0, _EstimateOutputCap(rawText.Length, divisor: 3, min: 64, max: 1024), CleanupTimeout, cancellationToken)
                .ConfigureAwait(false)).Trim();

            if (TranscriptCleanupGuard.IsSuspicious(rawText, cleaned, Options))
            {
                logger.LogWarning("Cleanup output looked suspicious ({Length} chars for {RawLength}-char input); using raw transcript", cleaned.Length, rawText.Length);
                return rawText;
            }

            return cleaned;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled (a barge-in / the session going away) — propagate rather than pass off the
            // raw transcript as a completed cleanup.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or ClientResultException)
        {
            // Server down/unreachable, our own timeout, or malformed JSON — the documented fallback is the raw
            // transcript, never an error surfaced to the operator (spec: "bij twijfel/down → rauwe transcript").
            // LM Studio answering 404 to a path it does not serve lands here too.
            logger.LogWarning(ex, "Transcript cleanup unavailable; using raw transcript");
            return rawText;
        }
    }

    public async Task<string> NaturalizeForSpeechAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // A little warmth for natural phrasing, but seeded so the same reply reads the same way.
            var spoken = (await _CompleteAsync(settings, SpeechPrompt(settings.ReadAloudLanguage), $"Text: {text}\nSpoken:", temperature: 0.3, _EstimateOutputCap(text.Length, divisor: 3, min: 64, max: 1024), SpeechTimeout, cancellationToken)
                .ConfigureAwait(false)).Trim();
            return string.IsNullOrWhiteSpace(spoken) ? text : spoken;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or ClientResultException)
        {
            logger.LogWarning(ex, "Read-aloud naturalization unavailable; using the original text");
            return text;
        }
    }

    public async Task<string> SummarizeForSpeechAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // A little warmth for natural phrasing, but seeded so the same reply summarizes the same way.
            var spoken = (await _CompleteAsync(settings, SummaryPrompt(settings.ReadAloudLanguage), $"Text: {text}\nSpoken summary:", temperature: 0.3, _EstimateOutputCap(text.Length, divisor: 6, min: 48, max: 512), SpeechTimeout, cancellationToken)
                .ConfigureAwait(false)).Trim();
            return string.IsNullOrWhiteSpace(spoken) ? text : spoken;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or ClientResultException)
        {
            logger.LogWarning(ex, "Read-aloud summarization unavailable; using the original text");
            return text;
        }
    }

    public async Task<string> AcknowledgeForSpeechAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // A little warmth so it does not read robotically; capped tiny and timed out fast — a preset phrase is
            // the fallback, so there is no reason to wait on a slow server for a nicety.
            return (await _CompleteAsync(settings, AckPrompt(settings.ReadAloudLanguage), $"Request: {userMessage}\nAcknowledgement:", temperature: 0.6, maxOutputTokens: 24, AckTimeout, cancellationToken)
                .ConfigureAwait(false)).Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or ClientResultException)
        {
            logger.LogWarning(ex, "Turn acknowledgement generation unavailable; falling back to a preset phrase");
            return "";
        }
    }

    /// <summary>
    /// One non-streaming chat completion against the configured local server's OpenAI-compatible endpoint via the
    /// shared <see cref="IChatClientFactory"/> (the OpenAI SDK, which Ollama and LM Studio both accept). The
    /// resolver auto-detects the running server + a model on it, or hands back the configured fallback. Serialized
    /// behind <see cref="_gate"/> and bounded by <paramref name="timeout"/> and <paramref name="maxOutputTokens"/>
    /// so a slow or runaway generation can neither stall the pipeline nor pin the server's single slot.
    /// </summary>
    private async Task<string> _CompleteAsync(VoiceSettings settings, string systemPrompt, string userPrompt, double temperature, int maxOutputTokens, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // The gate wait shares the linked token, so a call that only ever waits behind a slow one still gives up
        // at the timeout (and falls back) instead of queuing unboundedly.
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var endpoint = await endpointResolver.ResolveAsync(settings, linked.Token).ConfigureAwait(false);
            var chatClient = chatClientFactory.CreateForEndpoint(endpoint.BaseUrl, endpoint.Model);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userPrompt),
            };
            // Seeded so the same input reads the same way; temperature is 0 for cleanup, a little warmth for speech.
            var options = new ChatOptions { Temperature = (float)temperature, Seed = 42, MaxOutputTokens = maxOutputTokens };

            var response = await chatClient.GetResponseAsync(messages, options, linked.Token).ConfigureAwait(false);
            return response.Text;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A token ceiling for one voice-LLM call: roughly four characters per token, scaled by <paramref name="divisor"/>
    /// (cleanup/naturalize track the input length, a summary is shorter), then clamped. The clamp is the point —
    /// it caps generation regardless of input so a model that ignores "reply with only…" cannot run for minutes.
    /// </summary>
    private static int _EstimateOutputCap(int inputChars, int divisor, int min, int max)
        => Math.Clamp(inputChars / divisor, min, max);
}
