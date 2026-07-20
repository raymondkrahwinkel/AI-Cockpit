using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The host verify-feed routing (AC-86) per session kind. The text snapshot rides the verify tool result, so this
/// feed is only for the screenshot a tool result cannot carry: a vision SDK session shows it as a real user turn, a
/// non-vision SDK session and a TTY session take nothing (and never type a multi-line blob into a pty). Each override
/// reports whether the screenshot was actually shown.
/// </summary>
public class VerifyFeedRoutingTests
{
    private static readonly SessionProfile Profile = new("default", new ClaudeConfig(@"C:\fake\.claude"));
    private static readonly byte[] Screenshot = [0x89, 0x50, 0x4E, 0x47];
    private const string Caption = "Screenshot of the rendered UI for verify runner \"Cockpit\".";

    [Fact]
    public async Task SdkVisionSession_ShowsTheScreenshotAsAUserTurn()
    {
        var (vm, driver) = await _StartedSdkAsync(SessionCapabilities.ClaudeCli);

        var shown = await vm.FeedVerifyResultAsync(Caption, Screenshot);

        shown.Should().BeTrue();
        await driver.Received(1).SendUserMessageAsync(
            Caption,
            Arg.Is<IReadOnlyList<ImageAttachment>>(images => images.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SdkNonVisionSession_ShowsNothing_TheSnapshotRidesTheToolResult()
    {
        var nonVision = new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: false, SupportsLiveModelSwitch: false,
            SupportsPlanMode: false, SupportsThinking: false, SupportsVision: false);
        var (vm, driver) = await _StartedSdkAsync(nonVision);

        var shown = await vm.FeedVerifyResultAsync(Caption, Screenshot);

        shown.Should().BeFalse();
        await driver.DidNotReceive().SendUserMessageAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TtySession_ShowsNothing_AndNeverWritesToThePty()
    {
        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver());
        var writes = new List<string>();
        vm.VoiceTranscriptReady += text => writes.Add(text);
        vm.SetAutoSubmitScheduler(submit => submit());

        var shown = await vm.FeedVerifyResultAsync(Caption, Screenshot);

        // A pty carries no image and the snapshot is on the tool result — nothing is typed, so no premature submits.
        shown.Should().BeFalse();
        writes.Should().BeEmpty();
    }

    private static async Task<(SessionViewModel Vm, ISessionDriver Driver)> _StartedSdkAsync(SessionCapabilities capabilities)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(EmptyEvents());
        driver.Capabilities.Returns(capabilities);
        var vm = new SessionViewModel(new SessionManager(FactoryFor(driver)));
        await vm.StartConfiguredAsync(
            Profile, SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        return (vm, driver);
    }

    private static ITtySessionProviderResolver _Resolver()
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ITtySessionProvider>());
        return resolver;
    }

    private static ISessionDriverFactory FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }

    private static async IAsyncEnumerable<SessionEvent> EmptyEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
