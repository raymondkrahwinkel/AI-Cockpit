using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Slack.Tests;

// Hand-written test double for `IAssistantChannelGateway` — records what the plugin asked of the
// host seam and lets a test raise its events on demand.
internal sealed class FakeAssistantChannelGateway : IAssistantChannelGateway
{
    private readonly object _gate = new();

    public List<(string SenderId, string Text)> SentMessages { get; } = [];

    public List<(Guid PromptId, ConsentOutcome Outcome, bool Remember)> Responses { get; } = [];

    public AssistantChannelSendResult NextResult { get; set; } = AssistantChannelSendResult.Sent();

    public event EventHandler<AssistantChannelRow>? RowChanged;

    public event EventHandler<AssistantChannelConsentPrompt>? ConsentPromptOpened;

    public event EventHandler<Guid>? ConsentPromptClosed;

    public Task<AssistantChannelSendResult> SendAsync(string senderUserId, string text, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            SentMessages.Add((senderUserId, text));
        }

        return Task.FromResult(NextResult);
    }

    public void RespondToConsent(Guid promptId, ConsentOutcome outcome, bool remember = false)
    {
        lock (_gate)
        {
            Responses.Add((promptId, outcome, remember));
        }
    }

    public void RaiseRowChanged(AssistantChannelRow row) => RowChanged?.Invoke(this, row);

    public void RaisePromptOpened(AssistantChannelConsentPrompt prompt) => ConsentPromptOpened?.Invoke(this, prompt);

    public void RaisePromptClosed(Guid promptId) => ConsentPromptClosed?.Invoke(this, promptId);

    public void Dispose()
    {
    }
}
