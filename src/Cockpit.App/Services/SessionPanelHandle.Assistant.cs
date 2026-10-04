using System.ComponentModel;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Sessions;

namespace Cockpit.App.Services;

// AC-1440: the desktop's assistant session is its handle, as the backend's is (`SessionHostHandle`), with the pane the
// registry made behind it. `AssistantSessionHost` calls it on the UI thread, so nothing here marshals but `Rows`.
internal sealed partial class SessionPanelHandle : IAssistantSession
{
    // The cockpit's current voice and read-aloud language, handed down by `CockpitViewModel.CreateAssistantHandle` —
    // what the operator has selected right now, which is what the assistant's speech has always followed.
    internal Func<(int VoiceSid, string Language)>? AssistantVoice { get; set; }

    private EventHandler<bool>? _assistantStateChanged;

    // The assistant's pane is made synchronously when its handle registers, on the UI thread its host runs on.
    private SessionViewModel _Assistant =>
        _pane as SessionViewModel ?? throw new InvalidOperationException($"Pane '{PaneId}' has no SDK view.");

    bool IAssistantSession.IsSessionReady => _Assistant.IsSessionReady;

    bool IAssistantSession.IsBusy => _Assistant.IsBusy;

    // Either kind of waiting counts: the SDK's own permission row, and the cockpit's consent gate for a host-side tool.
    bool IAssistantSession.IsWaitingOnOperator => _Assistant.HasPendingPermission || _Assistant.PendingConsent is not null;

    double? IAssistantSession.ContextUsedPercent => _Assistant.ContextUsedPercent;

    bool IAssistantSession.SupportsContextCompaction => _Assistant.Capabilities.SupportsContextCompaction;

    string? IAssistantSession.TurnsHeldBecause
    {
        get => _Assistant.TurnsHeldBecause;
        set => _Assistant.TurnsHeldBecause = value;
    }

    ReadingLevel IAssistantSession.ReadingLevel
    {
        get => _Assistant.ReadingLevel;
        set => _Assistant.ReadingLevel = value;
    }

    string IAssistantSession.FailureReason => _Assistant.StartFailure ?? _Assistant.Status;

    bool IAssistantSession.CanPasteImages => _Assistant.CanPasteImages;

    bool IAssistantSession.HasPendingAttachments => _Assistant.HasPendingAttachments;

    // The pane is hooked while anyone listens, as `OutputTextProduced` is, so a handle nobody follows holds no handler on it.
    event EventHandler<bool>? IAssistantSession.StateChanged
    {
        add
        {
            if (_assistantStateChanged is null)
            {
                _Assistant.PropertyChanged += _OnAssistantPaneChanged;
            }

            _assistantStateChanged += value;
        }

        remove
        {
            _assistantStateChanged -= value;
            if (_assistantStateChanged is null && _pane is { } pane)
            {
                pane.PropertyChanged -= _OnAssistantPaneChanged;
            }
        }
    }

    // Read on the UI thread, capped like the gateway's own hop was (AC-1138): a channel can open from an MCP request thread.
    IReadOnlyList<TranscriptSnapshotEntry> IAssistantSession.Rows =>
        UiThreadCall.Run<IReadOnlyList<TranscriptSnapshotEntry>>(() => [.. _Assistant.Transcript.Select(TranscriptSnapshot.Capture)]);

    Task IAssistantSession.PrepareRecordedTranscriptAsync(SessionResume resume, CancellationToken cancellationToken) =>
        _Assistant.PrepareRecordedTranscriptAsync(resume, cancellationToken);

    // AC-1013: App defaults only as the floor — the profile's own permission mode/model/effort ride the launch
    // options (the driver prefers those), so a profile that says nothing still starts as before.
    Task IAssistantSession.StartAsync(AssistantLaunch launch) =>
        _Assistant.StartConfiguredAsync(
            launch.Profile,
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            workingDirectory: launch.WorkingDirectory,
            resume: launch.Resume,
            enabledMcpServerNames: launch.EnabledMcpServerNames,
            launchOptions: launch.LaunchOptions,
            readingLevel: launch.ReadingLevel);

    void IAssistantSession.AddPastedImage(byte[] pngBytes) => _Assistant.AddPastedImage(pngBytes);

    void IAssistantSession.SubmitComposer() => _Assistant.SendCommand.Execute(null);

    void IAssistantSession.InjectAndSubmit(string text) => _Assistant.InjectAndSubmit(text);

    Task<bool> IAssistantSession.CompactContextAsync() => _Assistant.CompactContextAsync();

    void IAssistantSession.AddDivider(string text) =>
        _Assistant.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Divider, text));

    void IAssistantSession.DenyPendingConsent()
    {
        if (_Assistant.PendingConsent is { } consent)
        {
            consent.DenyCommand.Execute(null);
            _Assistant.PendingConsent = null;
        }
    }

    // AC-1013: seeds speech (decision 2's "TTS erna") since nothing else does; read-aloud is always verbatim (AC-546).
    // One synthesis for the whole reply: sentence by sentence, every full stop came with an audible hole in it.
    void IAssistantSession.ApplySpeech(bool speakReplies)
    {
        var pane = _Assistant;
        pane.ReadAloudAsOneUtterance = true;

        if (AssistantVoice?.Invoke() is { } voice)
        {
            pane.TtsVoiceSid = voice.VoiceSid;
            pane.ReadAloudLanguage = voice.Language;
        }

        pane.ReadResponsesAloud = speakReplies;
    }

    void IAssistantSession.SpeakReplies(bool speak) => _Assistant.ReadResponsesAloud = speak;

    ValueTask IAsyncDisposable.DisposeAsync() => _pane?.DisposeAsync() ?? ValueTask.CompletedTask;

    // The properties the assistant's chip and its hand-over read (AC-1013, AC-596); a null name is "everything".
    // The consent card is a second way to wait on the operator, with a property of its own (#AC-47).
    private void _OnAssistantPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_assistantStateChanged is { } changed
            && e.PropertyName is null
                or nameof(SessionViewModel.IsBusy)
                or nameof(SessionViewModel.HasPendingPermission)
                or nameof(SessionViewModel.PendingConsent)
                or nameof(SessionViewModel.ContextUsedPercent))
        {
            changed(this, e.PropertyName is null or nameof(SessionViewModel.ContextUsedPercent));
        }
    }
}

// AC-1379: the assistant's session as the desktop's pane, for the views that draw it; null while none runs.
internal static class AssistantSessionHostView
{
    public static SessionViewModel? View(this IAssistantSessionHost host) =>
        host is RemoteAssistantHost remote ? remote.Pane : (host.Session as SessionPanelHandle)?.Pane as SessionViewModel;
}
