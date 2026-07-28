using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Profiles;
using NSubstitute;

namespace Cockpit.Plugin.FanOut.Tests;

[Collection("avalonia")]
public class FanOutWorkspaceBodyTests
{
    [Fact]
    public void Start_ThreeArms_EmbedsOneIsolatedSessionPerArm()
    {
        var context = new RecordingWorkspaceContext();

        HeadlessAvalonia.Run(() =>
        {
            var body = new FanOutWorkspaceBody(_Host(), context);
            body.Start(_Run("Personal", "Codex", "Local"));
            return body;
        });

        Assert.Equal(3, context.Requests.Count);
        Assert.All(context.Requests, request => Assert.True(request.IsolateInWorktree));
        Assert.Single(context.Requests.Select(request => request.RunId).Distinct());
    }

    [Fact]
    public void Start_ThreeArms_TilesTheirViewsWhereTheLayoutPutsThem()
    {
        var context = new RecordingWorkspaceContext();

        var tiles = HeadlessAvalonia.Run(() =>
        {
            var body = new FanOutWorkspaceBody(_Host(), context);
            body.Start(_Run("Personal", "Codex", "Local"));

            var surface = Assert.IsType<DockPanel>(body.Content);
            return surface.Children.OfType<Grid>().Single();
        });

        // Spelled out rather than read back from FanOutTileLayout: asking the layout where it put the tiles and
        // then checking the tiles are there would pass whatever the layout said. Three arms sit abreast.
        Assert.Equal(3, tiles.ColumnDefinitions.Count);
        Assert.Equal(1, tiles.RowDefinitions.Count);
        Assert.Equal(3, tiles.Children.Count);

        for (var column = 0; column < 3; column++)
        {
            var placed = tiles.Children[column];
            Assert.Equal(column, Grid.GetColumn(placed));
            Assert.Equal(0, Grid.GetRow(placed));
            Assert.Equal(1, Grid.GetColumnSpan(placed));
        }
    }

    [Fact]
    public void Start_CalledAgain_KeepsTheRunItAlreadyStarted()
    {
        var context = new RecordingWorkspaceContext();

        HeadlessAvalonia.Run<object?>(() =>
        {
            var body = new FanOutWorkspaceBody(_Host(), context);
            body.Start(_Run("Personal", "Codex", "Local"));
            body.Start(_Run("Personal", "Codex", "Local"));
            return null;
        });

        // A second batch would run three more agents in three more worktrees, with the tiles holding the first
        // three replaced — nothing left on screen to reach or stop them.
        Assert.Equal(3, context.Requests.Count);
    }

    [Fact]
    public void Start_ARunThatCannotStart_EmbedsNothing()
    {
        var context = new RecordingWorkspaceContext();

        HeadlessAvalonia.Run<object?>(() =>
        {
            var body = new FanOutWorkspaceBody(_Host(), context);
            body.Start(_Run("Personal"));
            return null;
        });

        Assert.Empty(context.Requests);
    }

    [Fact]
    public void Constructor_BeforeAnythingIsStarted_EmbedsNothing()
    {
        var context = new RecordingWorkspaceContext();

        HeadlessAvalonia.Run(() => new FanOutWorkspaceBody(_Host(), context));

        Assert.Empty(context.Requests);
    }

    private static ICockpitHost _Host()
    {
        var host = Substitute.For<ICockpitHost>();
        host.GetProfilesAsync().Returns(Task.FromResult<IReadOnlyList<PluginProfileInfo>>(
        [
            new PluginProfileInfo("Personal", "Plugin", string.Empty),
            new PluginProfileInfo("Codex", "Plugin", string.Empty),
            new PluginProfileInfo("Local", "Ollama", string.Empty),
        ]));

        return host;
    }

    private static FanOutRun _Run(params string[] profiles) =>
        new("Speed up the importer.", string.Empty, profiles.Select(profile => new FanOutVariant(profile, $"as {profile}")).ToList());
}
