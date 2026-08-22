using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Discord;

/// <summary>
/// Routes between one open <see cref="IAssistantChannelGateway"/> and a Discord channel via
/// <see cref="IDiscordChannelSink"/> (AC-1024), testable without a live socket. Unlike ordinary chat (checked
/// host-side, AC-1023 §3), a button click or "JA/NEE" reply reaches here directly, so it re-checks <see cref="_access"/> first.
/// </summary>
internal sealed class DiscordChannelBridge : IDisposable
{
    private readonly IAssistantChannelGateway _gateway;
    private readonly IDiscordChannelSink _sink;
    private readonly AssistantChannelAccess _access;
    private readonly Func<AssistantChannelVerbosity> _verbosity;

    private readonly Dictionary<Guid, ulong> _rowMessageIds = new();
    private readonly Dictionary<Guid, ulong> _promptMessageIds = new();

    // FIFO order of prompts currently relayed to this channel, oldest first — what the "type JA/NEE" fallback
    // answers when more than one is open at once. Rare, and a channel plugin has no picker UI for "which one" —
    // answering the oldest is the same order the operator would reach them in the app's own queue.
    private readonly List<Guid> _openPromptOrder = [];

    private bool _disposed;

    public DiscordChannelBridge(
        IAssistantChannelGateway gateway,
        IDiscordChannelSink sink,
        AssistantChannelAccess access,
        Func<AssistantChannelVerbosity> verbosity)
    {
        _gateway = gateway;
        _sink = sink;
        _access = access;
        _verbosity = verbosity;

        _gateway.RowChanged += _OnRowChanged;
        _gateway.ConsentPromptOpened += _OnPromptOpened;
        _gateway.ConsentPromptClosed += _OnPromptClosed;
    }

    /// <summary>
    /// A message arrived in the Discord channel. Answers an open consent prompt when the text is JA/NEE and one
    /// is waiting; otherwise forwards it as a chat turn. A real failure (never an ignored sender) gets a ⚠️
    /// reaction — the only sender-visible sign anything happened.
    /// </summary>
    public async Task HandleInboundMessageAsync(string senderId, string text, ulong messageId, CancellationToken cancellationToken = default)
    {
        if (_openPromptOrder.Count > 0 && DiscordConsentReplyParser.TryParse(text, out var outcome))
        {
            if (_access.IsAllowed(senderId))
            {
                _gateway.RespondToConsent(_openPromptOrder[0], outcome);
            }

            return;
        }

        var result = await _gateway.SendAsync(senderId, text, cancellationToken).ConfigureAwait(false);
        if (!result.Ok && !result.Ignored && result.Error is not null)
        {
            await _sink.AddReactionAsync(messageId, "⚠️", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An Approve/Deny button was clicked. The caller acknowledges Discord's 3-second interaction deadline
    /// before calling this — this only decides and edits.
    /// </summary>
    public async Task HandleButtonAsync(string customId, string senderId, CancellationToken cancellationToken = default)
    {
        if (!DiscordConsentButtonId.TryParse(customId, out var promptId, out var approve) || !_access.IsAllowed(senderId))
        {
            return;
        }

        _gateway.RespondToConsent(promptId, approve ? ConsentOutcome.Approved : ConsentOutcome.Denied);

        if (_promptMessageIds.TryGetValue(promptId, out var messageId))
        {
            await _sink.EditAsync(messageId, approve ? "✅ Approved." : "🚫 Denied.", keepButtons: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async void _OnRowChanged(object? sender, AssistantChannelRow row)
    {
        if (_disposed || !DiscordVerbosityFilter.ShouldRelay(row.Kind, _verbosity()))
        {
            return;
        }

        var text = DiscordVerbosityFilter.Render(row, _verbosity());

        try
        {
            if (row.IsUpdate && _rowMessageIds.TryGetValue(row.Id, out var existing))
            {
                await _sink.EditAsync(existing, text).ConfigureAwait(false);
            }
            else
            {
                _rowMessageIds[row.Id] = await _sink.PostAsync(text).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A single relay failure (a rate limit, a deleted channel) should not tear down the whole bridge —
            // the next row gets its own attempt.
        }
    }

    private async void _OnPromptOpened(object? sender, AssistantChannelConsentPrompt prompt)
    {
        if (_disposed)
        {
            return;
        }

        _openPromptOrder.Add(prompt.Id);

        try
        {
            _promptMessageIds[prompt.Id] = await _sink.PostAsync(prompt.Request.Action, prompt.Id).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void _OnPromptClosed(object? sender, Guid promptId)
    {
        _openPromptOrder.Remove(promptId);
        _promptMessageIds.Remove(promptId);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gateway.RowChanged -= _OnRowChanged;
        _gateway.ConsentPromptOpened -= _OnPromptOpened;
        _gateway.ConsentPromptClosed -= _OnPromptClosed;
    }
}
