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
/// </summary>
internal sealed class OpenAiCompatTranscriptCleanupService(IChatClientFactory chatClientFactory, IVoiceSettingsStore settingsStore, ILocalLlmEndpointResolver endpointResolver, ILogger<OpenAiCompatTranscriptCleanupService> logger)
    : ITranscriptCleanupService, ISingletonService
{
    private const string SystemPrompt =
        "You clean up a raw speech-to-text transcript for insertion as typed text. Add punctuation " +
        "where a pause implies it, remove filler words (uh, um, you know), add a question mark for " +
        "questions. Keep the original language and meaning exactly — do not translate, summarize, or " +
        "add content. Reply with only the cleaned text, nothing else.";

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
            var cleaned = (await _CompleteAsync(settings, SystemPrompt, $"Input: {rawText}\nOutput:", temperature: 0, cancellationToken)
                .ConfigureAwait(false)).Trim();

            if (TranscriptCleanupGuard.IsSuspicious(rawText, cleaned, Options))
            {
                logger.LogWarning("Cleanup output looked suspicious ({Length} chars for {RawLength}-char input); using raw transcript", cleaned.Length, rawText.Length);
                return rawText;
            }

            return cleaned;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ClientResultException)
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
            var spoken = (await _CompleteAsync(settings, SpeechPrompt(settings.ReadAloudLanguage), $"Text: {text}\nSpoken:", temperature: 0.3, cancellationToken)
                .ConfigureAwait(false)).Trim();
            return string.IsNullOrWhiteSpace(spoken) ? text : spoken;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ClientResultException)
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
            var spoken = (await _CompleteAsync(settings, SummaryPrompt(settings.ReadAloudLanguage), $"Text: {text}\nSpoken summary:", temperature: 0.3, cancellationToken)
                .ConfigureAwait(false)).Trim();
            return string.IsNullOrWhiteSpace(spoken) ? text : spoken;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ClientResultException)
        {
            logger.LogWarning(ex, "Read-aloud summarization unavailable; using the original text");
            return text;
        }
    }

    /// <summary>
    /// One non-streaming chat completion against the configured local server's OpenAI-compatible endpoint via the
    /// shared <see cref="IChatClientFactory"/> (the OpenAI SDK, which Ollama and LM Studio both accept). The
    /// resolver auto-detects the running server + a model on it, or hands back the configured fallback.
    /// </summary>
    private async Task<string> _CompleteAsync(VoiceSettings settings, string systemPrompt, string userPrompt, double temperature, CancellationToken cancellationToken)
    {
        var endpoint = await endpointResolver.ResolveAsync(settings, cancellationToken).ConfigureAwait(false);
        var chatClient = chatClientFactory.CreateForEndpoint(endpoint.BaseUrl, endpoint.Model);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt),
        };
        // Seeded so the same input reads the same way; temperature is 0 for cleanup, a little warmth for speech.
        var options = new ChatOptions { Temperature = (float)temperature, Seed = 42 };

        var response = await chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        return response.Text;
    }
}
