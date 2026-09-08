using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Mcp;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The incoming-pairing banner (AC-1291). A pairing request used to reach no screen at all unless the Options
/// window happened to stand open on the Nodes page: that page's view model was the only subscriber to
/// <see cref="INodePairingBroker.Changed"/>, and it only subscribed once the window opened.
/// </summary>
[Collection("avalonia")]
public class IncomingPairingBannerTests
{
    /// <summary>Criterion 1: the Options window is never opened here, and the request still lands on screen.</summary>
    [Fact]
    public void IncomingRequest_WithTheOptionsWindowClosed_ShowsTheBannerNamingTheCaller() => HeadlessAvalonia.Run(() =>
    {
        var broker = new FakePairingBroker();
        var vm = _NewVm(broker, Substitute.For<ISessionDialogService>());

        broker.Offer(new NodePairingPending
        {
            PairingId = "p1",
            ControllerName = "laptop",
            ControllerAddress = "192.168.1.240",
            Code = "123456",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2),
        });

        Assert.True(vm.HasIncomingPairing);
        Assert.Contains("laptop", vm.IncomingPairingBanner);
        Assert.Contains("192.168.1.240", vm.IncomingPairingBanner);
    });

    /// <summary>
    /// Criterion 2: the button lands on the page the code is actually on. The bindings carry a "Security." prefix
    /// because they come off <c>SecurityOptionsViewModel</c>, but the sidebar page is Nodes.
    /// </summary>
    [Fact]
    public Task ReviewButton_OpensOptionsOnTheNodesPage() => HeadlessAvalonia.RunAsync(async () =>
    {
        var dialogs = Substitute.For<ISessionDialogService>();
        var vm = _NewVm(new FakePairingBroker(), dialogs);

        await vm.ShowIncomingPairingCommand.ExecuteAsync(null);

        await dialogs.Received(1).ShowOptionsDialogAsync(vm, "nodes");
    });

    private static CockpitViewModel _NewVm(INodePairingBroker broker, ISessionDialogService dialogService)
    {
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            dialogService,
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            nodePairingBroker: broker);
    }

    // Only the two members the banner reads; the rest of the handshake is proved against the real broker in
    // Cockpit.Infrastructure.Tests.
    private sealed class FakePairingBroker : INodePairingBroker
    {
        public NodePairing? Pairing => null;

        public NodePairingPending? Pending { get; private set; }

        public event EventHandler? Changed;

        public void Offer(NodePairingPending? pending)
        {
            Pending = pending;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<NodePairingOffer> RequestAsync(string controllerName, string controllerAddress, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ConfirmAsync(string pairingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Refuse(string pairingId) => throw new NotSupportedException();

        public Task<NodePairingGrant> ClaimAsync(string pairingId, string claimToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UnpairAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public bool IsProfileAllowed(string profileLabel) => false;

        public bool IsProjectAllowed(string projectId) => false;

        public Task SetScopeAsync(
            IReadOnlyList<string> allowedProfileLabels,
            IReadOnlyList<string> allowedProjectIds,
            bool allowAllProfiles,
            bool allowAllProjects,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
