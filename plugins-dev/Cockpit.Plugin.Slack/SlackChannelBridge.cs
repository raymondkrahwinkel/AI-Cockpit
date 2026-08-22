using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Slack;

// Routes between one open `IAssistantChannelGateway` and a Slack channel via `ISlackChannelSink` (AC-1025),
// testable without a live socket. Unlike ordinary chat (checked host-side, AC-1023 §3), a button click or
// "JA/NEE" reply reaches here directly, so it re-checks `_access` first.
internal sealed class SlackChannelBridge : IDisposable
{
    private readonly IAssistantChannelGateway _gateway;
    private readonly ISlackChannelSink _sink;

    // Fixed for this bridge's lifetime, unlike _verbosity: an access change only reaches storage via the
    // settings dialog's save, which SlackChannelPlugin's OnSettingsSaved answers by disposing this bridge and
    // building a fresh one — a live Func<> here would never see anything _Reconnect had not already rebuilt.
    private readonly AssistantChannelAccess _access;
    private readonly Func<AssistantChannelVerbosity> _verbosity;

    // Guards the three collections below: RowChanged/ConsentPromptOpened/ConsentPromptClosed arrive on the
    // gateway's own thread (the UI thread), while HandleInboundMessageAsync/HandleButtonAsync are called from
    // SlackNet's own socket threads — the same reason AssistantChannelGateway locks around _relayedPrompts.
    private readonly object _gate = new();

    private readonly Dictionary<Guid, string> _rowMessageTs = new();
    private readonly Dictionary<Guid, string> _promptMessageTs = new();

    // FIFO order of prompts currently relayed to this channel, oldest first — what the "type JA/NEE" fallback
    // answers when more than one is open at once. Rare, and a channel plugin has no picker UI for "which one" —
    // answering the oldest is the same order the operator would reach them in the app's own queue.
    private readonly List<Guid> _openPromptOrder = [];

    private bool _disposed;

    public SlackChannelBridge(
        IAssistantChannelGateway gateway,
        ISlackChannelSink sink,
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

    // A message arrived in the Slack channel. Answers an open consent prompt when the text is JA/NEE and one
    // is waiting; otherwise forwards it as a chat turn. A real failure (never an ignored sender) gets a
    // ⚠️ reaction — the only sender-visible sign anything happened.
    public async Task HandleInboundMessageAsync(string senderId, string text, string messageTs, CancellationToken cancellationToken = default)
    {
        if (SlackConsentReplyParser.TryParse(text, out var outcome))
        {
            Guid? promptId;
            lock (_gate)
            {
                promptId = _openPromptOrder.Count > 0 ? _openPromptOrder[0] : null;
            }

            if (promptId is { } openPromptId)
            {
                if (_access.IsAllowed(senderId))
                {
                    _gateway.RespondToConsent(openPromptId, outcome);
                }

                return;
            }
        }

        var result = await _gateway.SendAsync(senderId, text, cancellationToken).ConfigureAwait(false);
        if (!result.Ok && !result.Ignored && result.Error is not null)
        {
            await _sink.AddReactionAsync(messageTs, "warning", cancellationToken).ConfigureAwait(false);
        }
    }

    // An Approve/Deny button was clicked.
    public async Task HandleButtonAsync(string actionId, string senderId, CancellationToken cancellationToken = default)
    {
        if (!SlackConsentButtonId.TryParse(actionId, out var promptId, out var approve) || !_access.IsAllowed(senderId))
        {
            return;
        }

        _gateway.RespondToConsent(promptId, approve ? ConsentOutcome.Approved : ConsentOutcome.Denied);

        string? messageTs;
        lock (_gate)
        {
            messageTs = _promptMessageTs.TryGetValue(promptId, out var ts) ? ts : null;
        }

        if (messageTs is { } editTs)
        {
            await _sink.EditAsync(editTs, approve ? "✅ Approved." : "🚫 Denied.", keepButtons: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async void _OnRowChanged(object? sender, AssistantChannelRow row)
    {
        if (_disposed || !SlackVerbosityFilter.ShouldRelay(row.Kind, _verbosity()))
        {
            return;
        }

        var text = SlackVerbosityFilter.Render(row, _verbosity());

        string? existing;
        lock (_gate)
        {
            existing = row.IsUpdate && _rowMessageTs.TryGetValue(row.Id, out var ts) ? ts : null;
        }

        try
        {
            if (existing is { } existingTs)
            {
                await _sink.EditAsync(existingTs, text).ConfigureAwait(false);
            }
            else
            {
                var posted = await _sink.PostAsync(text).ConfigureAwait(false);
                lock (_gate)
                {
                    _rowMessageTs[row.Id] = posted;
                }
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

        string messageTs;
        try
        {
            messageTs = await _sink.PostAsync(prompt.Request.Action, prompt.Id).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Not registered as open when the post itself failed — otherwise a "JA" typed for an unrelated
            // reason would answer a prompt nobody in the channel ever actually saw.
            return;
        }

        lock (_gate)
        {
            _openPromptOrder.Add(prompt.Id);
            _promptMessageTs[prompt.Id] = messageTs;
        }
    }

    private void _OnPromptClosed(object? sender, Guid promptId)
    {
        lock (_gate)
        {
            _openPromptOrder.Remove(promptId);
            _promptMessageTs.Remove(promptId);
        }
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
