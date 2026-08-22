namespace Cockpit.Plugin.Discord.Tests;

/// <summary>Hand-written test double for <see cref="IDiscordChannelSink"/> — records every post/edit/reaction the bridge asked for, without a live socket.</summary>
internal sealed class FakeDiscordChannelSink : IDiscordChannelSink
{
    private ulong _nextMessageId = 1;

    public List<(string Text, Guid? ConsentPromptId)> Posted { get; } = [];

    public List<(ulong MessageId, string Text, bool KeepButtons)> Edited { get; } = [];

    public List<(ulong MessageId, string Emoji)> Reactions { get; } = [];

    public Task<ulong> PostAsync(string text, Guid? consentPromptId = null, CancellationToken cancellationToken = default)
    {
        Posted.Add((text, consentPromptId));
        return Task.FromResult(_nextMessageId++);
    }

    public Task EditAsync(ulong messageId, string text, bool keepButtons = true, CancellationToken cancellationToken = default)
    {
        Edited.Add((messageId, text, keepButtons));
        return Task.CompletedTask;
    }

    public Task AddReactionAsync(ulong messageId, string emoji, CancellationToken cancellationToken = default)
    {
        Reactions.Add((messageId, emoji));
        return Task.CompletedTask;
    }
}
