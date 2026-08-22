namespace Cockpit.Plugin.Discord.Tests;

/// <summary>
/// Hand-written test double for <see cref="IDiscordChannelSink"/> — records every post/edit/reaction the
/// bridge asked for, without a live socket.
/// </summary>
internal sealed class FakeDiscordChannelSink : IDiscordChannelSink
{
    private readonly object _gate = new();
    private ulong _nextMessageId = 1;

    // Set to make the next PostAsync call fail (and only that one) — how the "a failed post must not register
    // the prompt as open" test (AC-1024 review point 2) simulates a real Discord failure.
    public bool FailNextPost { get; set; }

    public List<(string Text, Guid? ConsentPromptId)> Posted { get; } = [];

    public List<(ulong MessageId, string Text, bool KeepButtons)> Edited { get; } = [];

    public List<(ulong MessageId, string Emoji)> Reactions { get; } = [];

    public Task<ulong> PostAsync(string text, Guid? consentPromptId = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (FailNextPost)
            {
                FailNextPost = false;
                return Task.FromException<ulong>(new InvalidOperationException("simulated Discord post failure"));
            }

            Posted.Add((text, consentPromptId));
            return Task.FromResult(_nextMessageId++);
        }
    }

    public Task EditAsync(ulong messageId, string text, bool keepButtons = true, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Edited.Add((messageId, text, keepButtons));
        }

        return Task.CompletedTask;
    }

    public Task AddReactionAsync(ulong messageId, string emoji, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Reactions.Add((messageId, emoji));
        }

        return Task.CompletedTask;
    }
}
