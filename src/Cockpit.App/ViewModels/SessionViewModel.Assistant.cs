using System.ComponentModel;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// AC-1379: the desktop pane as the assistant's session. `AssistantSessionHost` calls it on the UI thread, so nothing
// here marshals; the members it already had under the same name answer the seam as they are.
public partial class SessionViewModel : IAssistantSession
{
    // The cockpit's current voice and read-aloud language, handed down by `CockpitViewModel.CreateAssistantSession` —
    // what the operator has selected right now, which is what the assistant's speech has always followed.
    internal Func<(int VoiceSid, string Language)>? AssistantVoice { get; set; }

    // Either kind of waiting counts: the SDK's own permission row, and the cockpit's consent gate for a host-side tool.
    bool IAssistantSession.IsWaitingOnOperator => HasPendingPermission || PendingConsent is not null;

    bool IAssistantSession.SupportsContextCompaction => Capabilities.SupportsContextCompaction;

    string IAssistantSession.FailureReason => StartFailure ?? Status;

    private EventHandler<bool>? _assistantStateChanged;

    event EventHandler<bool>? IAssistantSession.StateChanged
    {
        add => _assistantStateChanged += value;
        remove => _assistantStateChanged -= value;
    }

    event Action<TranscriptRowUpsert>? IAssistantSession.RowUpserted
    {
        add => _host.RowUpserted += value;
        remove => _host.RowUpserted -= value;
    }

    // AC-1013: App defaults only as the floor — the profile's own permission mode/model/effort ride the launch
    // options (the driver prefers those), so a profile that says nothing still starts as before.
    Task IAssistantSession.StartAsync(AssistantLaunch launch) =>
        StartConfiguredAsync(
            launch.Profile,
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            workingDirectory: launch.WorkingDirectory,
            resume: launch.Resume,
            enabledMcpServerNames: launch.EnabledMcpServerNames,
            launchOptions: launch.LaunchOptions,
            readingLevel: launch.ReadingLevel);

    void IAssistantSession.SubmitComposer() => SendCommand.Execute(null);

    void IAssistantSession.AddDivider(string text) => Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Divider, text));

    void IAssistantSession.DenyPendingConsent()
    {
        if (PendingConsent is { } consent)
        {
            consent.DenyCommand.Execute(null);
            PendingConsent = null;
        }
    }

    // AC-1013: seeds speech (decision 2's "TTS erna") since nothing else does; read-aloud is always verbatim (AC-546).
    // One synthesis for the whole reply: sentence by sentence, every full stop came with an audible hole in it.
    void IAssistantSession.ApplySpeech(bool speakReplies)
    {
        ReadAloudAsOneUtterance = true;

        if (AssistantVoice?.Invoke() is { } voice)
        {
            TtsVoiceSid = voice.VoiceSid;
            ReadAloudLanguage = voice.Language;
        }

        ReadResponsesAloud = speakReplies;
    }

    void IAssistantSession.SpeakReplies(bool speak) => ReadResponsesAloud = speak;

    // The properties the assistant's chip and its hand-over read (AC-1013, AC-596); a null name is "everything".
    // The consent card is a second way to wait on the operator, with a property of its own (#AC-47).
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (_assistantStateChanged is { } changed
            && e.PropertyName is null
                or nameof(IsBusy)
                or nameof(HasPendingPermission)
                or nameof(PendingConsent)
                or nameof(ContextUsedPercent))
        {
            changed(this, e.PropertyName is null or nameof(ContextUsedPercent));
        }
    }
}

// AC-1379: the assistant's session as the desktop's pane, for the views that draw it; null while none runs.
internal static class AssistantSessionHostView
{
    public static SessionViewModel? View(this IAssistantSessionHost host) => host.Session as SessionViewModel;
}
