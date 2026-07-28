using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Tests.ViewModels;
using Cockpit.Core.Updates;
using Cockpit.Infrastructure.Updates;
using NSubstitute;

namespace Cockpit.Core.Tests.Updates;

/// <summary>
/// No silent channel drift (AC-387): a channel nobody chose follows the build, a channel somebody chose outlives a
/// restart and beats the build, and nothing else the operator touches turns the first into the second.
/// <para>
/// The failure being kept out: a nightly downloaded on purpose, started without a configuration file, landing on
/// stable and being offered the latest stable as its first update — a downgrade, presented as an upgrade.
/// </para>
/// </summary>
public class UpdateChannelChoiceTests : IDisposable
{
    private readonly string _configFile = Path.Combine(Directory.CreateTempSubdirectory("ac387-cfg-").FullName, "cockpit.json");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_configFile)!, recursive: true);

    /// <summary>Criterion 3: a nightly build with nothing configured looks for nightlies, not for the stable it would be a downgrade to.</summary>
    [Fact]
    public async Task ANightlyBuild_WithNothingConfigured_FollowsTheNightlyChannel()
    {
        var vm = UpdateTestCockpit.Build(Updates("0.8.0-nightly.12"), Store(new UpdateSettings()));

        await vm.InitialiseUpdatesAsync();

        Assert.True(vm.IncludeNightlyBuilds);
    }

    /// <summary>And the same build the other way round, so the rule is not just "true for everything".</summary>
    [Fact]
    public async Task AStableBuild_WithNothingConfigured_FollowsTheStableChannel()
    {
        var vm = UpdateTestCockpit.Build(Updates("0.8.0"), Store(new UpdateSettings()));

        await vm.InitialiseUpdatesAsync();

        Assert.False(vm.IncludeNightlyBuilds);
    }

    /// <summary>Criterion 4, first half: a choice beats what the build would have implied.</summary>
    [Fact]
    public async Task AChosenChannel_WinsOverTheBuildsOwnStream()
    {
        var vm = UpdateTestCockpit.Build(Updates("0.8.0-nightly.12"), Store(new UpdateSettings(Channel: UpdateChannel.Stable)));

        await vm.InitialiseUpdatesAsync();

        Assert.False(vm.IncludeNightlyBuilds);
    }

    /// <summary>
    /// Reading the settings must not look like using them. This is the shape the old code had — the control was filled
    /// from disk and the fill wrote straight back — which is how every installation ended up with a stored channel
    /// nobody had picked.
    /// </summary>
    [Fact]
    public async Task FillingTheControlsFromDisk_DoesNotWriteAChoiceBack()
    {
        var store = Store(new UpdateSettings());
        var vm = UpdateTestCockpit.Build(Updates("0.8.0-nightly.12"), store);

        await vm.InitialiseUpdatesAsync();

        await store.DidNotReceive().SaveAsync(Arg.Any<UpdateSettings>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Touching the channel is the choice, and it is recorded as one — the control now says stable for a nightly
    /// build, which only a person can have meant.
    /// </summary>
    [Fact]
    public async Task TouchingTheChannel_RecordsAChoice()
    {
        var store = Store(new UpdateSettings());
        var vm = UpdateTestCockpit.Build(Updates("0.8.0-nightly.12"), store);
        await vm.InitialiseUpdatesAsync();

        vm.IncludeNightlyBuilds = false;

        await store.Received().SaveAsync(Arg.Is<UpdateSettings>(s => s.Channel == UpdateChannel.Stable), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The side door: the startup checkbox saves the same section. If that save carried the channel currently on
    /// screen, ticking an unrelated box would freeze a derived channel into a choice — the drift arriving by another
    /// route, and invisibly.
    /// </summary>
    [Fact]
    public async Task TouchingTheStartupSetting_LeavesTheChannelUnchosen()
    {
        var store = Store(new UpdateSettings());
        var vm = UpdateTestCockpit.Build(Updates("0.8.0-nightly.12"), store);
        await vm.InitialiseUpdatesAsync();

        vm.CheckForUpdatesOnStartup = false;

        await store.Received().SaveAsync(Arg.Is<UpdateSettings>(s => s.Channel == null), Arg.Any<CancellationToken>());
    }

    /// <summary>Criterion 4, second half: written to disk and read back, the choice is still a choice.</summary>
    [Fact]
    public async Task AChosenChannel_SurvivesARestart()
    {
        await new UpdateSettingsStore(_configFile).SaveAsync(new UpdateSettings(Channel: UpdateChannel.Nightly));

        var reloaded = await new UpdateSettingsStore(_configFile).LoadAsync();

        Assert.Equal(UpdateChannel.Nightly, reloaded.Channel);
    }

    /// <summary>A cockpit nobody has configured says so, rather than reporting a channel it invented.</summary>
    [Fact]
    public async Task AnEmptyConfiguration_HasNoChosenChannel() =>
        Assert.Null((await new UpdateSettingsStore(_configFile).LoadAsync()).Channel);

    /// <summary>
    /// The migration this ticket had to decide (AC-387). A configuration written before this change carries a
    /// <c>Channel</c> that every start wrote back whether or not anybody had touched the control — so it is evidence
    /// of nothing, and reading it as a choice would leave the drift in place for every existing installation. It is
    /// read as unchosen, once, and the build decides again.
    /// </summary>
    [Fact]
    public async Task AChannelWrittenByTheOldSettings_IsReadAsNeverChosen()
    {
        await File.WriteAllTextAsync(_configFile, """{"Updates":{"CheckOnStartup":true,"Channel":"Nightly"}}""");

        var settings = await new UpdateSettingsStore(_configFile).LoadAsync();

        Assert.Null(settings.Channel);
        // The rest of the section is still read: only the channel's standing changed, not the file's.
        Assert.True(settings.CheckOnStartup);
    }

    /// <summary>A channel that cannot be read is not a choice either — the same answer, for the same reason.</summary>
    [Fact]
    public async Task AnUnreadableChannel_IsReadAsNeverChosen()
    {
        await File.WriteAllTextAsync(_configFile, """{"Updates":{"CheckOnStartup":true,"ChosenChannel":"Weekly"}}""");

        Assert.Null((await new UpdateSettingsStore(_configFile).LoadAsync()).Channel);
    }

    private static IUpdateService Updates(string version)
    {
        var updates = Substitute.For<IUpdateService>();
        updates.Current.Returns((version, string.Empty));
        updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>()).Returns(UpdateCheckResult.UpToDate);

        return updates;
    }

    private static IUpdateSettingsStore Store(UpdateSettings settings)
    {
        var store = Substitute.For<IUpdateSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        return store;
    }
}
