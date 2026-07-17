using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="ITranscriptCleanupService"/> backed by any local OpenAI-compatible LLM server
/// (<c>POST {base}/v1/chat/completions</c>) — Ollama and LM Studio both serve this, so one code path
/// covers a laptop on Ollama and a desktop on LM Studio. Ported from WisperFlow's <c>cleanup.py</c> safety
/// net (research: Cockpit-DotNet-Voice-Stack-2026-07-07.md §4): too-short input skips the call entirely,
/// and any failure or suspicious-looking output falls back to the raw transcript rather than surfacing an
/// error or risking a hallucinated result reaching the session.
/// </summary>
internal sealed class OpenAiCompatTranscriptCleanupService(HttpClient httpClient, IVoiceSettingsStore settingsStore, ILocalLlmEndpointResolver endpointResolver, ILogger<OpenAiCompatTranscriptCleanupService> logger)
    : ITranscriptCleanupService, ISingletonService
{
    private const string SystemPrompt =
        "You clean up a raw speech-to-text transcript for insertion as typed text. Add punctuation " +
        "where a pause implies it, remove filler words (uh, um, you know), add a question mark for " +
        "questions. Keep the original language and meaning exactly — do not translate, summarize, or " +
        "add content. Reply with only the cleaned text, nothing else.";

    private const string SpeechPrompt =
        "You turn assistant text into what a person would say out loud when explaining it to someone. " +
        "Rewrite it as short, natural spoken sentences. Leave out code, file paths, URLs, command names and " +
        "markdown; mention them in plain words only when they matter. Never read symbols, brackets or " +
        "punctuation literally, and do not spell things out. Keep the meaning and the original language. " +
        "Mark the language of each part with [[nl]] before Dutch text and [[en]] before English text, and " +
        "switch the marker whenever the language changes, even for a single word or phrase. Begin with the " +
        "marker for the first language. Reply with only the marked spoken text — no preamble, no quotes, no bullet lists.";

    private const string SummaryPrompt =
        "You summarize assistant text so it can be heard, not read — give the listener the gist in far fewer " +
        "words. Rewrite it as short, natural spoken sentences. Leave out code, file paths, URLs, command names " +
        "and markdown; mention them in plain words only when they matter. Never read symbols, brackets or " +
        "punctuation literally. Keep it shorter than the original, but this is a hard rule: preserve every " +
        "number, name, decision, warning and action item exactly — never drop, round, soften or invent any of " +
        "them. When in doubt, keep it. Keep the original language. Mark the language of each part with [[nl]] " +
        "before Dutch text and [[en]] before English text, switching the marker whenever the language changes, " +
        "even for a single word or phrase. Begin with the marker for the first language. Reply with only the " +
        "marked spoken summary — no preamble, no quotes, no bullet lists.";

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
            var cleaned = (await _CompleteAsync(settings, SystemPrompt, $"Input: {rawText}\nOutput:", temperature: 0, cancellationToken)
                .ConfigureAwait(false)).Trim();

            if (TranscriptCleanupGuard.IsSuspicious(rawText, cleaned, Options))
            {
                logger.LogWarning("Cleanup output looked suspicious ({Length} chars for {RawLength}-char input); using raw transcript", cleaned.Length, rawText.Length);
                return rawText;
            }

            return cleaned;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Server down/unreachable/timed out or returned malformed JSON — the documented fallback is
            // the raw transcript, never an error surfaced to the operator (spec: "bij twijfel/down →
            // rauwe transcript"). LM Studio answering 404 to a path it does not serve lands here too.
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
            var spoken = (await _CompleteAsync(settings, SpeechPrompt, $"Text: {text}\nSpoken:", temperature: 0.3, cancellationToken)
                .ConfigureAwait(false)).Trim();
            return string.IsNullOrWhiteSpace(spoken) ? text : spoken;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
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
            var spoken = (await _CompleteAsync(settings, SummaryPrompt, $"Text: {text}\nSpoken summary:", temperature: 0.3, cancellationToken)
                .ConfigureAwait(false)).Trim();
            return string.IsNullOrWhiteSpace(spoken) ? text : spoken;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Read-aloud summarization unavailable; using the original text");
            return text;
        }
    }

    /// <summary>
    /// One non-streaming chat completion against the configured local server's OpenAI-compatible endpoint —
    /// the <c>/v1/chat/completions</c> shape Ollama and LM Studio both accept. The base URL is stored without
    /// the <c>/v1</c> suffix (matching the profile providers), so it is appended here.
    /// </summary>
    private async Task<string> _CompleteAsync(VoiceSettings settings, string systemPrompt, string userPrompt, double temperature, CancellationToken cancellationToken)
    {
        // Auto-detect the running local server + a model on it (or use the configured fallback), then post there.
        var endpoint = await endpointResolver.ResolveAsync(settings, cancellationToken).ConfigureAwait(false);

        var request = new ChatCompletionRequest
        {
            Model = endpoint.Model,
            Messages =
            [
                new ChatCompletionMessage { Role = "system", Content = systemPrompt },
                new ChatCompletionMessage { Role = "user", Content = userPrompt },
            ],
            Temperature = temperature,
            Seed = 42,
            Stream = false,
        };

        var url = $"{endpoint.BaseUrl.TrimEnd('/')}/v1/chat/completions";
        using var response = await httpClient.PostAsJsonAsync(url, request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken).ConfigureAwait(false);
        return body?.Choices is { Count: > 0 } choices ? choices[0].Message?.Content ?? string.Empty : string.Empty;
    }
}
