using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// <see cref="SessionPanelViewModel.DisposeAsync"/> silences any in-flight/queued read-aloud playback when the
/// closing session had <see cref="TtyViewModel.ReadResponsesAloud"/> on — the same shared base-class behaviour
/// <c>SessionViewModel</c> gets, exercised here from the TTY panel.
/// </summary>
public class TtyDisposeStopsPlaybackTests
{
    private static readonly SessionProfile Work = new("work", ClaudePluginProfile.Create("/config/work", null));

    [Fact]
    public async Task DisposeAsync_WhileReadingAloud_StopsPlaybackSoAClosedSessionGoesSilent()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new TtyViewModel(
            Substitute.For<ITtyLauncher>(), _Resolver(), voicePlaybackQueue: voicePlaybackQueue, transcriptReader: _Reader());
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");
        vm.ReadResponsesAloud = true;

        await vm.DisposeAsync();

        voicePlaybackQueue.Received(1).StopAll();
    }

    [Fact]
    public async Task DisposeAsync_WhenNotReadingAloud_LeavesOtherSessionsPlaybackUntouched()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new TtyViewModel(
            Substitute.For<ITtyLauncher>(), _Resolver(), voicePlaybackQueue: voicePlaybackQueue, transcriptReader: _Reader());
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");

        await vm.DisposeAsync();

        voicePlaybackQueue.DidNotReceive().StopAll();
    }

    /// <summary>Resolves any profile (including none) to a fresh provider substitute — same as the real resolver does for a Claude profile or a profile-less session.</summary>
    private static ITtySessionProviderResolver _Resolver()
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ITtySessionProvider>());
        return resolver;
    }

    /// <summary>A transcript reader whose launch snapshot is empty, so the VM's baseline is non-null and the status tail actually starts.</summary>
    private static ISessionTranscriptReader _Reader()
    {
        var reader = Substitute.For<ISessionTranscriptReader>();
        reader.SnapshotTranscripts(Arg.Any<SessionProfile?>()).Returns(new HashSet<string>());
        return reader;
    }
}
