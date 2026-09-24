using System.ComponentModel;
using Avalonia.Threading;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;

namespace Cockpit.App.Services;

// AC-1379: the presence as the assistant's host takes it on the desktop — its change raised on the UI thread, the hop
// `CockpitViewModel.WatchController` makes, since the host behind it is bound to the chip. The backend hands the plain one.
internal sealed class UiThreadControllerPresence : INodeControllerPresence
{
    private readonly INodeControllerPresence _presence;

    // Built once per host and living as long as it, so the one subscription here never needs taking off.
    public UiThreadControllerPresence(INodeControllerPresence presence)
    {
        _presence = presence;
        presence.Changed += (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
            }
        };
    }

    public ActiveController? Current => _presence.Current;

    public event EventHandler? Changed;
}

// AC-1379: the assistant's host as a chat channel reaches it on the desktop — a send capped onto the UI thread
// (AC-1023, AC-1138) and a session change posted to it, the hops `AssistantChannelGateway` made while it lived here.
internal sealed class UiThreadAssistantSessionHost(IAssistantSessionHost host) : IAssistantSessionHost
{
    private PropertyChangedEventHandler? _propertyChanged;

    // Subscribed to the host only while something listens, so a closed channel leaves nothing behind on the singleton.
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add
        {
            if (_propertyChanged is null)
            {
                host.PropertyChanged += _OnHostPropertyChanged;
            }

            _propertyChanged += value;
        }
        remove
        {
            _propertyChanged -= value;
            if (_propertyChanged is null)
            {
                host.PropertyChanged -= _OnHostPropertyChanged;
            }
        }
    }

    private void _OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => _propertyChanged?.Invoke(this, e));

    public IAssistantSession? Session => host.Session;

    public AssistantActivity Activity => host.Activity;

    public string? UnavailableReason => host.UnavailableReason;

    public string? DefaultWorkingDirectory => host.DefaultWorkingDirectory;

    public Task SendAsync(string text, CancellationToken cancellationToken = default) =>
        UiThreadCall.RunAsync(() => host.SendAsync(text, cancellationToken));

    public Task SendAsync(string text, IReadOnlyList<byte[]> pngImages, CancellationToken cancellationToken = default) =>
        UiThreadCall.RunAsync(() => host.SendAsync(text, pngImages, cancellationToken));

    public void SetSpeakReplies(bool speak) => host.SetSpeakReplies(speak);

    public Task<IAssistantSession?> EnsureStartedAsync(CancellationToken cancellationToken = default) => host.EnsureStartedAsync(cancellationToken);

    public Task<IAssistantSession?> RestartAsync(CancellationToken cancellationToken = default) => host.RestartAsync(cancellationToken);

    public Task<IAssistantSession?> ClearConversationAsync(CancellationToken cancellationToken = default) => host.ClearConversationAsync(cancellationToken);

    public bool RequestConversationClear() => host.RequestConversationClear();

    public Task ApplySettingsAsync(CancellationToken cancellationToken = default) => host.ApplySettingsAsync(cancellationToken);

    public void ReportHoldListening(bool listening) => host.ReportHoldListening(listening);

    public void ReportTranscribing(bool transcribing) => host.ReportTranscribing(transcribing);

    public void ReportPreparing(string? status, double? fraction) => host.ReportPreparing(status, fraction);
}
