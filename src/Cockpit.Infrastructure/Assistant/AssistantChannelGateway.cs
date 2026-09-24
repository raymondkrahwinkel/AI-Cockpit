using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Images;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Assistant;

// AC-1023: the host half of `IAssistantChannelGateway`, where a refusal is a result rather than an exception. The
// identity check in `SendAsync` and the prompt filtering further down are the security boundary itself, host-side so
// a plugin cannot skip them. AC-1379: no thread of its own — the app hands it a host that marshals to the UI thread.
public sealed class AssistantChannelGateway : IAssistantChannelGateway
{
    private readonly AssistantChannelContribution _channel;
    private readonly IAssistantSessionHost _host;
    private readonly IConsentBroker _consent;
    private readonly ILogger<AssistantChannelGateway> _logger;

    // Row identity for a plugin, which needs something stable to recognise "the same message, longer" by: one Guid per
    // row id, with a hash of the text and result last relayed so a change to anything else stays quiet. ponytail: kept
    // for the session's life, as the rows are; prune on a row removal if the upsert stream ever carries one.
    private readonly Dictionary<string, (Guid Id, int Content)> _relayedRows = new(StringComparer.Ordinal);

    private readonly HashSet<Guid> _relayedPrompts = [];

    private IAssistantSession? _observed;
    private bool _disposed;

    public AssistantChannelGateway(
        AssistantChannelContribution channel,
        IAssistantSessionHost host,
        IConsentBroker consent,
        ILogger<AssistantChannelGateway> logger)
    {
        _channel = channel;
        _host = host;
        _consent = consent;
        _logger = logger;

        _host.PropertyChanged += _OnHostPropertyChanged;
        _consent.PromptOpened += _OnPromptOpened;
        _consent.PromptClosed += _OnPromptClosed;

        _WatchSession(_host.Session);
    }

    public event EventHandler<AssistantChannelRow>? RowChanged;

    public event EventHandler<AssistantChannelConsentPrompt>? ConsentPromptOpened;

    public event EventHandler<Guid>? ConsentPromptClosed;

    public Task<AssistantChannelSendResult> SendAsync(
        string senderUserId,
        string text,
        CancellationToken cancellationToken = default) =>
        SendAsync(senderUserId, text, [], cancellationToken);

    public async Task<AssistantChannelSendResult> SendAsync(
        string senderUserId,
        string text,
        IReadOnlyList<byte[]> images,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return AssistantChannelSendResult.Refused("This channel is closed.");
        }

        if (!_channel.Access.IsAllowed(senderUserId))
        {
            // AC-1074: Information, not Debug — `FileLoggerProvider` drops everything below it, so the Debug this
            // used to be could never reach the log an operator debugging "nothing comes in" actually reads.
            _logger.LogInformation(
                "Channel {ChannelId} ({ChannelName}) ignored a message from {SenderUserId}: not on the access list.",
                _channel.Id,
                _channel.Name,
                senderUserId);
            return AssistantChannelSendResult.IgnoredSender();
        }

        // Before the send, so the decoding runs on the caller's thread rather than the host's.
        var (accepted, refusal) = _Accept(images);

        try
        {
            await _host.SendAsync(text, accepted, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return AssistantChannelSendResult.Refused(exception.Message);
        }

        return refusal is null
            ? AssistantChannelSendResult.Sent()
            : AssistantChannelSendResult.SentWithoutImages(refusal);
    }

    // The host's half of the trust boundary (AC-1049): the plugin says these are images, `InboundImage` decides.
    // A file that will not pass is dropped on its own — the message it came with still goes, which is what a
    // sender who wrote a paragraph and attached the wrong file needs to happen.
    private static (IReadOnlyList<byte[]> Accepted, string? Refusal) _Accept(IReadOnlyList<byte[]> images)
    {
        if (images.Count == 0)
        {
            return ([], null);
        }

        var accepted = new List<byte[]>();
        var refusals = new List<string>();

        foreach (var image in images.Take(AssistantChannelImageLimits.MaxPerMessage))
        {
            if (InboundImage.TryNormalizeToPng(image, out var png, out var refusal))
            {
                accepted.Add(png);
            }
            else
            {
                refusals.Add(refusal);
            }
        }

        if (images.Count > AssistantChannelImageLimits.MaxPerMessage)
        {
            refusals.Add($"only the first {AssistantChannelImageLimits.MaxPerMessage} images of a message are passed on");
        }

        return (accepted, refusals.Count == 0 ? null : string.Join("; ", refusals.Distinct()));
    }

    public void RespondToConsent(Guid promptId, ConsentOutcome outcome, bool remember = false)
    {
        lock (_relayedPrompts)
        {
            if (!_relayedPrompts.Contains(promptId))
            {
                return;
            }
        }

        _consent.Respond(promptId, outcome, remember);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.PropertyChanged -= _OnHostPropertyChanged;
        _consent.PromptOpened -= _OnPromptOpened;
        _consent.PromptClosed -= _OnPromptClosed;
        _WatchSession(null);
    }

    // ── transcript ─────────────────────────────────────────────────────────────────────────────────────────────

    private void _OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IAssistantSessionHost.Session) && !_disposed)
        {
            _WatchSession(_host.Session);
        }
    }

    private void _WatchSession(IAssistantSession? next)
    {
        lock (_relayedRows)
        {
            if (ReferenceEquals(_observed, next))
            {
                return;
            }

            if (_observed is not null)
            {
                _observed.RowUpserted -= _OnRowUpserted;
            }

            _relayedRows.Clear();
            _observed = next;

            if (next is null)
            {
                return;
            }

            next.RowUpserted += _OnRowUpserted;
        }

        // Taken as already said, never replayed. Read outside the lock, since the pane answers on its UI thread; a row
        // that arrived in between was relayed already and keeps its identity.
        var baseline = next.Rows;
        lock (_relayedRows)
        {
            if (!ReferenceEquals(_observed, next))
            {
                return;
            }

            foreach (var row in baseline)
            {
                _relayedRows.TryAdd(row.Id, (Guid.NewGuid(), _Content(row)));
            }
        }
    }

    // A row's first version is a row arriving; any later one is the same row changing — including a row that was
    // already there when the channel joined, which is watched from here on but never replayed.
    private void _OnRowUpserted(TranscriptRowUpsert upsert)
    {
        if (_disposed)
        {
            return;
        }

        var row = upsert.Row;
        Guid id;
        lock (_relayedRows)
        {
            if (_relayedRows.TryGetValue(row.Id, out var relayed))
            {
                if (relayed.Content == _Content(row))
                {
                    return;
                }

                id = relayed.Id;
            }
            else
            {
                id = Guid.NewGuid();
            }

            _relayedRows[row.Id] = (id, _Content(row));
        }

        RowChanged?.Invoke(this, new AssistantChannelRow
        {
            Id = id,
            Kind = _Kind(row.Kind),
            Text = row.Text,
            Timestamp = row.Timestamp,
            ToolName = row.ToolName,
            ResultText = row.ResultText,
            IsUpdate = upsert.Version > 1,
        });
    }

    private static int _Content(TranscriptSnapshotEntry row) => HashCode.Combine(row.Text, row.ResultText);

    // The row kinds as a snapshot spells them — `TranscriptEntryKind`'s names.
    private static AssistantChannelRowKind _Kind(string kind) => kind switch
    {
        "UserText" => AssistantChannelRowKind.UserText,
        "ToolUse" => AssistantChannelRowKind.ToolUse,
        "ToolResult" => AssistantChannelRowKind.ToolResult,
        "Thinking" => AssistantChannelRowKind.Thinking,
        "Question" => AssistantChannelRowKind.Question,
        "TurnCompleted" => AssistantChannelRowKind.TurnCompleted,
        "Error" => AssistantChannelRowKind.Error,
        "Divider" => AssistantChannelRowKind.Divider,
        _ => AssistantChannelRowKind.AssistantText,
    };

    // ── consent ────────────────────────────────────────────────────────────────────────────────────────────────

    private void _OnPromptOpened(object? sender, ConsentPrompt prompt)
    {
        // The assistant's own, or a workflow's that no session owns ("Ask me first", AC-1360), whose Action carries
        // the literal step. Everything else stays off: the other end would approve work it was never shown.
        if (_disposed || !_IsRelayable(prompt.Request.Source))
        {
            return;
        }

        lock (_relayedPrompts)
        {
            _relayedPrompts.Add(prompt.Id);
        }

        ConsentPromptOpened?.Invoke(this, new AssistantChannelConsentPrompt(prompt.Id, prompt.Request, prompt.CanRemember));
    }

    // Least privilege: only the Workflows plugin opts in for now. PluginId is stamped by the host, so another plugin
    // cannot pass its prompt off as a workflow's.
    private const string _WorkflowsPluginId = "workflows";

    private static bool _IsRelayable(ConsentSource source) =>
        source.PaneId == AssistantIdentity.PaneId || (source.PaneId is null && source.PluginId == _WorkflowsPluginId);

    private void _OnPromptClosed(object? sender, Guid promptId)
    {
        lock (_relayedPrompts)
        {
            if (!_relayedPrompts.Remove(promptId))
            {
                return;
            }
        }

        if (!_disposed)
        {
            ConsentPromptClosed?.Invoke(this, promptId);
        }
    }
}
