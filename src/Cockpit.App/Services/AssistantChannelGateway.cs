using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Consent;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.App.Services;

// The app-level half of `IAssistantChannelGateway` (AC-1023): the one place a chat channel reaches the assistant,
// and the narrowest surface that can carry a two-way conversation. Sibling of `AssistantAgentGateway` and shaped
// like it on purpose — same UI-thread marshalling, since a platform's socket callback arrives on its own thread
// while `SessionViewModel` and its transcript only ever move on the UI thread.
//
// *This class is the identity gate, not the plugin.* `SendAsync` checks the channel's own `AssistantChannelAccess`
// before anything reaches `IAssistantSessionHost.SendAsync`, so a plugin that forgot to check — or was made to skip
// it — still cannot put a stranger's words into the operator's conversation. §3's silence is a return value here,
// never a reply the plugin has to remember not to send.
//
// *And it is not the consent gate.* Relaying a prompt is not deciding it: what reaches a channel is only the
// assistant's own prompts, and only a prompt this channel was actually told about can be answered through it. The
// app's own Allow/Deny card stays exactly where it was — both routes answer the same broker, first one wins.
internal sealed class AssistantChannelGateway : IAssistantChannelGateway
{
    private readonly AssistantChannelContribution _channel;
    private readonly IAssistantSessionHost _host;
    private readonly IConsentBroker _consent;

    // Row identity: the transcript's entries carry none of their own, and a streaming row is mutated in place, so a
    // plugin needs something stable to recognise "the same message, longer" by. Reference-keyed, and the same
    // dictionary is what tells us which entries we are subscribed to when the collection resets.
    private readonly Dictionary<TranscriptEntryViewModel, Guid> _rowIds = new(ReferenceEqualityComparer.Instance);

    private readonly HashSet<Guid> _relayedPrompts = [];

    private SessionViewModel? _observed;
    private bool _disposed;

    public AssistantChannelGateway(
        AssistantChannelContribution channel,
        IAssistantSessionHost host,
        IConsentBroker consent)
    {
        _channel = channel;
        _host = host;
        _consent = consent;

        _host.PropertyChanged += _OnHostPropertyChanged;
        _consent.PromptOpened += _OnPromptOpened;
        _consent.PromptClosed += _OnPromptClosed;

        Dispatcher.UIThread.Invoke(() => _WatchSession(_host.Session));
    }

    public event EventHandler<AssistantChannelRow>? RowChanged;

    public event EventHandler<AssistantChannelConsentPrompt>? ConsentPromptOpened;

    public event EventHandler<Guid>? ConsentPromptClosed;

    public async Task<AssistantChannelSendResult> SendAsync(
        string senderUserId,
        string text,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return AssistantChannelSendResult.Refused("This channel is closed.");
        }

        if (!_channel.Access.IsAllowed(senderUserId))
        {
            return AssistantChannelSendResult.IgnoredSender();
        }

        await Dispatcher.UIThread.InvokeAsync(() => _host.SendAsync(text, cancellationToken)).ConfigureAwait(false);

        return AssistantChannelSendResult.Sent();
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
        Dispatcher.UIThread.Invoke(() => _WatchSession(null));
    }

    // ── transcript ─────────────────────────────────────────────────────────────────────────────────────────────

    private void _OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IAssistantSessionHost.Session) && !_disposed)
        {
            Dispatcher.UIThread.Post(() => _WatchSession(_host.Session));
        }
    }

    private void _WatchSession(SessionViewModel? next)
    {
        if (ReferenceEquals(_observed, next))
        {
            return;
        }

        if (_observed is not null)
        {
            _observed.Transcript.CollectionChanged -= _OnTranscriptChanged;
        }

        foreach (var entry in _rowIds.Keys)
        {
            entry.PropertyChanged -= _OnRowChanged;
        }

        _rowIds.Clear();
        _observed = next;

        if (next is null)
        {
            return;
        }

        next.Transcript.CollectionChanged += _OnTranscriptChanged;

        // Attached without raising: a channel that joins mid-conversation relays what happens from now on, never a
        // replay of everything already said.
        foreach (var entry in next.Transcript)
        {
            _Attach(entry);
        }
    }

    private void _OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _WatchSession(null);
            _WatchSession(_host.Session);
            return;
        }

        foreach (var removed in e.OldItems?.OfType<TranscriptEntryViewModel>() ?? [])
        {
            removed.PropertyChanged -= _OnRowChanged;
            _rowIds.Remove(removed);
        }

        foreach (var added in e.NewItems?.OfType<TranscriptEntryViewModel>() ?? [])
        {
            _Attach(added);
            _Raise(added, isUpdate: false);
        }
    }

    private void _Attach(TranscriptEntryViewModel entry)
    {
        if (_rowIds.TryAdd(entry, Guid.NewGuid()))
        {
            entry.PropertyChanged += _OnRowChanged;
        }
    }

    private void _OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TranscriptEntryViewModel entry
            && e.PropertyName is nameof(TranscriptEntryViewModel.Text) or nameof(TranscriptEntryViewModel.ResultText))
        {
            _Raise(entry, isUpdate: true);
        }
    }

    private void _Raise(TranscriptEntryViewModel entry, bool isUpdate)
    {
        if (_disposed || !_rowIds.TryGetValue(entry, out var id))
        {
            return;
        }

        RowChanged?.Invoke(this, new AssistantChannelRow(id, _Kind(entry.Kind), entry.Text, entry.Timestamp)
        {
            ToolName = entry.ToolName,
            ResultText = entry.ResultText,
            IsUpdate = isUpdate,
        });
    }

    private static AssistantChannelRowKind _Kind(TranscriptEntryKind kind) => kind switch
    {
        TranscriptEntryKind.UserText => AssistantChannelRowKind.UserText,
        TranscriptEntryKind.ToolUse => AssistantChannelRowKind.ToolUse,
        TranscriptEntryKind.ToolResult => AssistantChannelRowKind.ToolResult,
        TranscriptEntryKind.Thinking => AssistantChannelRowKind.Thinking,
        TranscriptEntryKind.Question => AssistantChannelRowKind.Question,
        TranscriptEntryKind.TurnCompleted => AssistantChannelRowKind.TurnCompleted,
        TranscriptEntryKind.Error => AssistantChannelRowKind.Error,
        TranscriptEntryKind.Divider => AssistantChannelRowKind.Divider,
        _ => AssistantChannelRowKind.AssistantText,
    };

    // ── consent ────────────────────────────────────────────────────────────────────────────────────────────────

    private void _OnPromptOpened(object? sender, ConsentPrompt prompt)
    {
        // Only the assistant's own: a channel is a door onto that one conversation, and relaying another session's
        // prompt would let whoever is on the other end approve work they were never shown.
        if (_disposed || prompt.Request.Source.PaneId != AssistantIdentity.PaneId)
        {
            return;
        }

        lock (_relayedPrompts)
        {
            _relayedPrompts.Add(prompt.Id);
        }

        ConsentPromptOpened?.Invoke(this, new AssistantChannelConsentPrompt(prompt.Id, prompt.Request, prompt.CanRemember));
    }

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
