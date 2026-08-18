using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-782: the bar's Compact button belongs on the context-fill line, only when the provider supports compaction,
/// and never doubles up with the automatic 80%-trigger while a compaction is already in flight.
/// </summary>
public class SessionCompactWarningTests
{
    private static readonly PluginUsageSignal Context =
        new("context", "ctx", PluginUsageSignalKind.Fill, 50) { Description = "Context window" };

    private static readonly PluginUsageSignal Weekly =
        new("weekly", "wk", PluginUsageSignalKind.Allowance, 90) { Description = "Week" };

    [Fact]
    public void TheContextFillLine_ShowsCompact_WhenTheProviderSupportsIt()
    {
        var panel = new TtyViewModel { Capabilities = SessionCapabilities.ClaudeCli with { SupportsContextCompaction = true } };

        panel.ApplyUsage([Context], [new PluginUsageReading("context", 60, null)]);

        Assert.True(panel.Warnings.Single(w => w.Key == "context").ShowCompact);
    }

    [Fact]
    public void AnAllowanceLine_NeverShowsCompact_EvenWithTheCapability()
    {
        var panel = new TtyViewModel { Capabilities = SessionCapabilities.ClaudeCli with { SupportsContextCompaction = true } };

        panel.ApplyUsage([Weekly], [new PluginUsageReading("weekly", 95, null)]);

        Assert.False(panel.Warnings.Single(w => w.Key == "weekly").ShowCompact);
    }

    [Fact]
    public void TheContextFillLine_HidesCompact_WithoutTheCapability()
    {
        // Default SessionCapabilities.ClaudeCli does not declare SupportsContextCompaction — the pre-AC-664 shape.
        var panel = new TtyViewModel();

        panel.ApplyUsage([Context], [new PluginUsageReading("context", 60, null)]);

        Assert.False(panel.Warnings.Single(w => w.Key == "context").ShowCompact);
    }

    [Fact]
    public void ShowCompact_RecoversWhenCapabilitiesArriveAfterTheWarningAlreadyStood()
    {
        // AC-893: StartWithProfileAsync's own ordering — usage refreshed before Capabilities is set — reproduced
        // directly on the base VM. OnCapabilitiesChanged must rebuild the bar on its own, without a second
        // ApplyUsage call, or a session that starts already over threshold shows Compact-less until its next turn.
        var panel = new TtyViewModel();
        panel.ApplyUsage([Context], [new PluginUsageReading("context", 60, null)]);
        Assert.False(panel.Warnings.Single(w => w.Key == "context").ShowCompact);

        panel.Capabilities = SessionCapabilities.ClaudeCli with { SupportsContextCompaction = true };

        Assert.True(panel.Warnings.Single(w => w.Key == "context").ShowCompact);
    }

    [Fact]
    public async Task AResumedSessionStartingAboveThreshold_ShowsCompactAssoonAsItStarts()
    {
        // AC-893 criterion 4: the real start path (SessionViewModel.StartWithProfileAsync), not just the VM flag
        // on a hand-built TtyViewModel — a driver that already reports usage over threshold at start (a daily
        // resume, like the Assistant) and declares SupportsContextCompaction must not need a second turn before
        // the Compact button appears.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyEvents());
        driver.CurrentStatus.Returns(new SessionStatusFeed(60, []));
        driver.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: true, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false)
        {
            SupportsContextCompaction = true,
        });

        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var vm = new SessionViewModel(new SessionManager(factory));

        await vm.StartConfiguredAsync(
            new SessionProfile("default", new ClaudeConfig(@"C:\fake\.claude")),
            SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);

        Assert.True(vm.Warnings.Single(w => w.Key == "context").ShowCompact);

        await vm.DisposeAsync();
    }

    [Fact]
    public async Task TheCompactCommand_CannotRunWhileTheSessionIsBusy()
    {
        var (vm, _) = await _StartedAsync();

        Assert.True(vm.CompactCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.CompactCommand.CanExecute(null));

        vm.IsBusy = false;
        Assert.True(vm.CompactCommand.CanExecute(null));

        await vm.DisposeAsync();
    }

    private static async Task<(SessionViewModel Vm, ISessionDriver Driver)> _StartedAsync()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyEvents());
        driver.Capabilities.Returns(new SessionCapabilities(
            SupportsTools: true, SupportsPermissions: true, SupportsLiveModelSwitch: false, SupportsPlanMode: false, SupportsThinking: false)
        {
            SupportsContextCompaction = true,
        });

        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var vm = new SessionViewModel(new SessionManager(factory));
        await vm.StartConfiguredAsync(
            new SessionProfile("default", new ClaudeConfig(@"C:\fake\.claude")),
            SessionOptionCatalog.DefaultPermissionMode, SessionOptionCatalog.DefaultModel, SessionOptionCatalog.DefaultEffort);
        return (vm, driver);
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }
}
