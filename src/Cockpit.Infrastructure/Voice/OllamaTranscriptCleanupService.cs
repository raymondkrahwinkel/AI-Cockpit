using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="ITranscriptCleanupService"/> backed by a local Ollama daemon (<c>POST /api/generate</c>),
/// ported 1:1 from WisperFlow's <c>cleanup.py</c> safety net (research:
/// Cockpit-DotNet-Voice-Stack-2026-07-07.md §4): too-short input skips the call entirely, and any
/// failure or suspicious-looking output falls back to the raw transcript rather than surfacing an
/// error or risking a hallucinated result reaching the session.
/// </summary>
internal sealed class OllamaTranscriptCleanupService(HttpClient httpClient, IVoiceSettingsStore settingsStore, ILogger<OllamaTranscriptCleanupService> logger)
    : ITranscriptCleanupService, ISingletonService
{
    private const string SystemPrompt =
        "You clean up a raw speech-to-text transcript for insertion as typed text. Add punctuation " +
        "where a pause implies it, remove filler words (uh, um, you know), add a question mark for " +
        "questions. Keep the original language and meaning exactly — do not translate, summarize, or " +
        "add content. Reply with only the cleaned text, nothing else.";

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
            var request = new OllamaGenerateRequest
            {
                Model = settings.CleanupModel,
                System = SystemPrompt,
                Prompt = $"Input: {rawText}\nOutput:",
                Stream = false,
                Options = new OllamaGenerateOptions { Temperature = 0, Seed = 42 },
            };

            using var response = await httpClient
                .PostAsJsonAsync($"{settings.OllamaBaseUrl}/api/generate", request, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken).ConfigureAwait(false);
            var cleaned = body?.Response?.Trim() ?? string.Empty;

            if (TranscriptCleanupGuard.IsSuspicious(rawText, cleaned, Options))
            {
                logger.LogWarning("Ollama cleanup output looked suspicious ({Length} chars for {RawLength}-char input); using raw transcript", cleaned.Length, rawText.Length);
                return rawText;
            }

            return cleaned;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Ollama down/unreachable/timed out or returned malformed JSON — the documented fallback is
            // the raw transcript, never an error surfaced to the operator (spec: "bij twijfel/down →
            // rauwe transcript").
            logger.LogWarning(ex, "Ollama cleanup unavailable; using raw transcript");
            return rawText;
        }
    }
}
