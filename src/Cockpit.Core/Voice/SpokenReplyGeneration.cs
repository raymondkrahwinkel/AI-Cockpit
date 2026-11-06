using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Voice;

public sealed record SpokenReplyResult(IReadOnlyList<string> Sentences, string? RulesReason);

public static class SpokenReplyGeneration
{
    public static async Task<SpokenReplyResult> GenerateAsync(
        ISessionDriverFactory? factory, SessionProfile? profile, string answer, string? context,
        SpokenReplyMode mode, SpokenReplyMethod method, CancellationToken cancellationToken = default)
    {
        var fallback = SpokenReply.Select(answer, mode);
        if (mode == SpokenReplyMode.Full || method == SpokenReplyMethod.Rules || fallback.Count == 0)
        {
            return new(fallback, null);
        }
        if (factory is null || profile is null)
        {
            return new(fallback, "Using built-in shortening: the selected AI profile is missing or cannot generate speech privately.");
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            pending.Token.ThrowIfCancellationRequested();
            string? generated = await Task.Run(() => factory.GenerateEphemeralSpeechAsync(profile, answer, context, pending.Token), pending.Token)
                .WaitAsync(pending.Token).ConfigureAwait(false);
            pending.Token.ThrowIfCancellationRequested();
            if (generated is { Length: > 0 and <= 16000 } && !generated.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character)))
            {
                var sentences = SpokenReply.Select(generated, mode);
                if (sentences.Count > 0)
                {
                    return new(sentences, null);
                }
            }
            return new(fallback, "Using built-in shortening: the selected AI could not provide a usable spoken answer.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new([], null);
        }
        catch (Exception)
        {
            // Provider exceptions can contain the private payload.
            return new(fallback, "Using built-in shortening: the selected AI is unavailable, cannot generate speech privately, or took too long.");
        }
    }
}
