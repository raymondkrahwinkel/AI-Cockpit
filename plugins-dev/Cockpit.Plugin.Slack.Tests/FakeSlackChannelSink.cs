namespace Cockpit.Plugin.Slack.Tests;

// Hand-written test double for `ISlackChannelSink` — records every post/edit/reaction the
// bridge asked for, without a live socket.
internal sealed class FakeSlackChannelSink : ISlackChannelSink
{
    private readonly object _gate = new();
    private int _nextMessageId = 1;

    // Set to make the next PostAsync call fail (and only that one) — how the "a failed post must not register
    // the prompt as open" test (AC-1024 review point 2, applies here too) simulates a real Slack failure.
    public bool FailNextPost { get; set; }

    public List<(string Text, Guid? ConsentPromptId)> Posted { get; } = [];

    public List<(string MessageTs, string Text, bool KeepButtons)> Edited { get; } = [];

    public List<(string MessageTs, string Emoji)> Reactions { get; } = [];

    public Task<string> PostAsync(string text, Guid? consentPromptId = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (FailNextPost)
            {
                FailNextPost = false;
                return Task.FromException<string>(new InvalidOperationException("simulated Slack post failure"));
            }

            Posted.Add((text, consentPromptId));
            return Task.FromResult((_nextMessageId++).ToString());
        }
    }

    public Task EditAsync(string messageTs, string text, bool keepButtons = true, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Edited.Add((messageTs, text, keepButtons));
        }

        return Task.CompletedTask;
    }

    public Task AddReactionAsync(string messageTs, string emoji, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Reactions.Add((messageTs, emoji));
        }

        return Task.CompletedTask;
    }
}
