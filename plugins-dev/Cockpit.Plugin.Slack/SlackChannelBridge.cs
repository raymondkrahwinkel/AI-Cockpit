using System.Diagnostics;
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
    private readonly ISlackFileFetcher _files;

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
        ISlackFileFetcher files,
        AssistantChannelAccess access,
        Func<AssistantChannelVerbosity> verbosity)
    {
        _gateway = gateway;
        _sink = sink;
        _files = files;
        _access = access;
        _verbosity = verbosity;

        _gateway.RowChanged += _OnRowChanged;
        _gateway.ConsentPromptOpened += _OnPromptOpened;
        _gateway.ConsentPromptClosed += _OnPromptClosed;
    }

    // A message arrived in the Slack channel. Answers an open consent prompt when the text is JA/NEE and one
    // is waiting; otherwise forwards it as a chat turn, images and all (AC-1049). A real failure, or an
    // attachment that did not make it, gets a ⚠️ reaction — never an ignored sender, who is answered with silence.
    public async Task HandleInboundMessageAsync(
        string senderId,
        string text,
        string messageTs,
        IReadOnlyList<SlackInboundFile>? files = null,
        CancellationToken cancellationToken = default)
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

        var (images, someFileRefused) = await _CollectImagesAsync(files, cancellationToken).ConfigureAwait(false);

        var result = await _gateway.SendAsync(senderId, text, images, cancellationToken).ConfigureAwait(false);
        if (result.Ignored)
        {
            return;
        }

        // The text still went (AC-1049 criterion 5), so a refused attachment is a mark on the sender's own
        // message rather than a message that failed.
        if ((!result.Ok && result.Error is not null) || someFileRefused || result.ImagesRefused is not null)
        {
            await _sink.AddReactionAsync(messageTs, "warning", cancellationToken).ConfigureAwait(false);
        }
    }

    // What of a message's files is worth handing over. The mime type and size Slack reports are a pre-filter that
    // saves a pointless download — the host decides what an image really is, and does not take our word for it.
    private async Task<(IReadOnlyList<byte[]> Images, bool Refused)> _CollectImagesAsync(
        IReadOnlyList<SlackInboundFile>? files, CancellationToken cancellationToken)
    {
        if (files is not { Count: > 0 })
        {
            return ([], false);
        }

        var images = new List<byte[]>();
        var refused = false;

        foreach (var file in files)
        {
            if (file.Url is null
                || file.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
                || file.Size > AssistantChannelImageLimits.MaxBytes
                || images.Count >= AssistantChannelImageLimits.MaxPerMessage)
            {
                refused = true;
                continue;
            }

            try
            {
                images.Add(await _files.FetchAsync(file.Url, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The sender only ever sees the reaction, so the reason goes to Trace — a plugin has no host log
                // seam, and a download that failed is otherwise indistinguishable from a bad token (AC-1048).
                Trace.WriteLine($"Slack: '{file.Name}' was not passed to the assistant — {exception.Message}");
                refused = true;
            }
        }

        return (images, refused);
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
