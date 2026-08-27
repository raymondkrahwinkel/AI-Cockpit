using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Discord;

// Routes between one open `IAssistantChannelGateway` and a Discord channel via
// `IDiscordChannelSink` (AC-1024), testable without a live socket. Unlike ordinary chat (checked
// host-side, AC-1023 §3), a button click or "JA/NEE" reply reaches here directly, so it re-checks `_access` first.
internal sealed class DiscordChannelBridge : IDisposable
{
    private readonly IAssistantChannelGateway _gateway;
    private readonly IDiscordChannelSink _sink;
    private readonly IDiscordFileFetcher _files;

    // Fixed for this bridge's lifetime, unlike _verbosity: an access change only reaches storage via the
    // settings dialog's save, which DiscordChannelPlugin's OnSettingsSaved answers by disposing this bridge and
    // building a fresh one — a live Func<> here would never see anything _Reconnect had not already rebuilt.
    private readonly AssistantChannelAccess _access;
    private readonly Func<AssistantChannelVerbosity> _verbosity;

    // AC-1074: where a dropped attachment gets said out loud. Routed to the host rather than Trace, which nothing
    // in this app listens to, so the reason went nowhere at all.
    private readonly Action<string> _reportError;

    // Guards the three collections below: RowChanged/ConsentPromptOpened/ConsentPromptClosed arrive on the
    // gateway's own thread (the UI thread), while HandleInboundMessageAsync/HandleButtonAsync are called from
    // Discord.NET's socket threads — the same reason AssistantChannelGateway locks around _relayedPrompts.
    private readonly object _gate = new();

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
        IDiscordFileFetcher files,
        AssistantChannelAccess access,
        Func<AssistantChannelVerbosity> verbosity,
        Action<string>? reportError = null)
    {
        _gateway = gateway;
        _sink = sink;
        _files = files;
        _access = access;
        _verbosity = verbosity;
        _reportError = reportError ?? (_ => { });

        _gateway.RowChanged += _OnRowChanged;
        _gateway.ConsentPromptOpened += _OnPromptOpened;
        _gateway.ConsentPromptClosed += _OnPromptClosed;
    }

    // A message arrived in the Discord channel. Answers an open consent prompt when the text is JA/NEE and one
    // is waiting; otherwise forwards it as a chat turn, images and all (AC-1049). A real failure, or an
    // attachment that did not make it, gets a ⚠️ reaction — never an ignored sender, who is answered with silence.
    public async Task HandleInboundMessageAsync(
        string senderId,
        string text,
        ulong messageId,
        IReadOnlyList<DiscordInboundFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        if (DiscordConsentReplyParser.TryParse(text, out var outcome))
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

        var (images, someFileRefused, downloadFailures) = await _CollectImagesAsync(files, cancellationToken).ConfigureAwait(false);

        // One report per message rather than per file: a bad token fails every attachment, and that would be a
        // burst of identical toasts for what is one problem.
        if (downloadFailures is { } failures)
        {
            _reportError($"Discord: an attachment never reached the assistant — {failures}");
        }

        AssistantChannelSendResult result;
        try
        {
            result = await _gateway.SendAsync(senderId, text, images, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // AC-1138: the host now refuses rather than waiting forever when its UI thread is away, and the caller
            // discards this task — so without a word here the message would vanish. Reported for the same reason a
            // dropped attachment is: the sender is owed the fact that it did not arrive.
            _reportError($"Discord: a message never reached the assistant — {exception.Message}");
            return;
        }

        if (result.Ignored)
        {
            return;
        }

        // The text still went (AC-1049 criterion 5), so a refused attachment is a mark on the sender's own
        // message rather than a message that failed.
        if ((!result.Ok && result.Error is not null) || someFileRefused || result.ImagesRefused is not null)
        {
            await _sink.AddReactionAsync(messageId, "⚠️", cancellationToken).ConfigureAwait(false);
        }
    }

    // What of a message's attachments is worth handing over. The content type and size Discord reports are a
    // pre-filter that saves a pointless download — the host decides what an image really is, and does not take
    // our word for it.
    private async Task<(IReadOnlyList<byte[]> Images, bool Refused, string? Failures)> _CollectImagesAsync(
        IReadOnlyList<DiscordInboundFile>? files, CancellationToken cancellationToken)
    {
        if (files is not { Count: > 0 })
        {
            return ([], false, null);
        }

        var images = new List<byte[]>();
        var failures = new List<string>();
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
                failures.Add($"'{file.Name}' ({exception.Message})");
                refused = true;
            }
        }

        return (images, refused, failures.Count == 0 ? null : string.Join("; ", failures));
    }

    // An Approve/Deny button was clicked. The caller acknowledges Discord's 3-second interaction deadline
    // before calling this — this only decides and edits.
    public async Task HandleButtonAsync(string customId, string senderId, CancellationToken cancellationToken = default)
    {
        if (!DiscordConsentButtonId.TryParse(customId, out var promptId, out var approve) || !_access.IsAllowed(senderId))
        {
            return;
        }

        _gateway.RespondToConsent(promptId, approve ? ConsentOutcome.Approved : ConsentOutcome.Denied);

        ulong? messageId;
        lock (_gate)
        {
            messageId = _promptMessageIds.TryGetValue(promptId, out var id) ? id : null;
        }

        if (messageId is { } editMessageId)
        {
            await _sink.EditAsync(editMessageId, approve ? "✅ Approved." : "🚫 Denied.", keepButtons: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async void _OnRowChanged(object? sender, AssistantChannelRow row)
    {
        if (_disposed || !DiscordVerbosityFilter.ShouldRelay(row.Kind, _verbosity()))
        {
            return;
        }

        var text = DiscordVerbosityFilter.Render(row, _verbosity());

        ulong? existing;
        lock (_gate)
        {
            existing = row.IsUpdate && _rowMessageIds.TryGetValue(row.Id, out var id) ? id : null;
        }

        try
        {
            if (existing is { } existingMessageId)
            {
                await _sink.EditAsync(existingMessageId, text).ConfigureAwait(false);
            }
            else
            {
                var posted = await _sink.PostAsync(text).ConfigureAwait(false);
                lock (_gate)
                {
                    _rowMessageIds[row.Id] = posted;
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

        ulong messageId;
        try
        {
            messageId = await _sink.PostAsync(prompt.Request.Action, prompt.Id).ConfigureAwait(false);
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
            _promptMessageIds[prompt.Id] = messageId;
        }
    }

    private void _OnPromptClosed(object? sender, Guid promptId)
    {
        lock (_gate)
        {
            _openPromptOrder.Remove(promptId);
            _promptMessageIds.Remove(promptId);
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
