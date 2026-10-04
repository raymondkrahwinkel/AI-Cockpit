using System.ComponentModel;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;

namespace Cockpit.App.Services;

// AC-1479: the assistant on a connect server, as the host the chat window already reads. The window is a peephole onto a
// pane; here the pane follows the server's assistant handle, so the transcript and the prompt are the server's.
internal sealed class RemoteAssistantHost(SessionViewModel pane) : IAssistantSessionHost
{
    public SessionViewModel Pane { get; } = pane;

    public IAssistantSession? Session => null;

    public AssistantActivity Activity => AssistantActivity.Ready;

    public string? UnavailableReason => null;

    public string? DefaultWorkingDirectory => null;

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    // The server starts and ends its own assistant; this window only talks to it.
    public Task<IAssistantSession?> EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.FromResult<IAssistantSession?>(null);

    public Task<IAssistantSession?> RestartAsync(CancellationToken cancellationToken = default) => Task.FromResult<IAssistantSession?>(null);

    public Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        Pane.InputText = text;
        return Pane.SendCommand.ExecuteAsync(null);
    }

    public void SetSpeakReplies(bool speak)
    {
    }

    public Task ApplySettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void ReportHoldListening(bool listening)
    {
    }

    public void ReportTranscribing(bool transcribing)
    {
    }

    public void ReportPreparing(string? status, double? fraction)
    {
    }
}

// What the window's settings and read-aloud queue are for a server's assistant: nothing of this laptop's, so a toggle in
// the header neither speaks nor writes the assistant settings of this machine.
internal sealed class RemoteAssistantSettingsStore : IAssistantSettingsStore
{
    public Task<AssistantSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AssistantSettings { IsEnabled = true, SpeakReplies = false, AlwaysOnTop = false });

    public Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class RemoteAssistantVoiceQueue : Cockpit.Core.Abstractions.Voice.IVoicePlaybackQueue
{
    public void Enqueue(IReadOnlyList<string> sentences, int speakerId, string language, Cockpit.Core.Voice.VoicePlaybackSource source = Cockpit.Core.Voice.VoicePlaybackSource.Session)
    {
    }

    public void Enqueue(IReadOnlyList<Cockpit.Core.Voice.SpeechSegment> segments, int speakerId, Cockpit.Core.Voice.VoicePlaybackSource source = Cockpit.Core.Voice.VoicePlaybackSource.Session)
    {
    }

    public void NotifyPreparing(Cockpit.Core.Voice.VoicePlaybackSource source = Cockpit.Core.Voice.VoicePlaybackSource.Session)
    {
    }

    public event EventHandler<bool>? PlaybackActiveChanged
    {
        add
        {
        }

        remove
        {
        }
    }

    public event EventHandler? SpeakingStarted
    {
        add
        {
        }

        remove
        {
        }
    }

    public void StopAll()
    {
    }

    public int Generation => 0;

    public Cockpit.Core.Voice.VoicePlaybackSource ActiveSource => Cockpit.Core.Voice.VoicePlaybackSource.Session;
}
