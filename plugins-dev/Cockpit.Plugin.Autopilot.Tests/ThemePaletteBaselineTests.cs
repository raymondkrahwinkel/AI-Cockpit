using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using Cockpit.TestSupport;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// This plugin's half of the AC-338 theme-palette baseline: what its own surfaces actually paint, held against a
/// committed file the same way <c>Cockpit.App.ViewTests.ThemePaletteBaselineTests</c> holds the host's. Two
/// surfaces stand in for the plugin — the plan-flow workspace (in its default, no-run state) and the settings
/// dialog (on the section it opens on).
/// </summary>
/// <remarks>
/// To re-record after an intended change: run with <c>COCKPIT_UPDATE_THEME_BASELINES=1</c>, review the diff, then
/// run again without it. The rewriting run still fails on purpose.
/// </remarks>
[Collection("avalonia")]
public class ThemePaletteBaselineTests
{
    private const string RewriteVariable = "COCKPIT_UPDATE_THEME_BASELINES";

    public static TheoryData<string> Scenes => ["workspace", "settings"];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void AScene_PaintsTheColoursItsBaselineRecords(string scene)
    {
        var painted = _Painted(scene);

        var baseline = Path.Combine(RepositoryPaths.Root, "plugins-dev", "Cockpit.Plugin.Autopilot.Tests", "Baselines", $"{scene}.palette.txt");
        var recorded = File.Exists(baseline) ? _Normalised(File.ReadAllText(baseline)) : null;

        if (Environment.GetEnvironmentVariable(RewriteVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
            File.WriteAllText(baseline, painted);
            Assert.Fail($"Rewrote the baseline for '{scene}'. Review the diff, then run again without {RewriteVariable}.");
        }

        Assert.True(recorded is not null,
            $"Scene '{scene}' has no baseline. Every scene the harness can render carries one — run with "
            + $"{RewriteVariable}=1 to write it, then review it.");

        Assert.Equal(recorded, _Normalised(painted));
    }

    /// <summary>
    /// Proves the harness is honest before any baseline built on it is believed (AC-337): the theme's text colour
    /// arrives through a selector, which only runs once a control reaches a shown window's styling root. A tree
    /// that is only measured, never shown, would still resolve its resource lookups and pass a plausible-looking
    /// but wrong report.
    /// </summary>
    [Fact]
    public void TheHarness_ShowsItsWindow_SoTheThemesSelectorsHaveRun()
    {
        var primary = (Color)(Application.Current?.FindResource("CockpitTextPrimaryColor")
            ?? throw new InvalidOperationException("The theme has no CockpitTextPrimaryColor."));

        var painted = _Painted("settings");

        Assert.Contains($"#{primary.A:X2}{primary.R:X2}{primary.G:X2}{primary.B:X2}", painted, StringComparison.Ordinal);
    }

    private static string _Painted(string scene)
    {
        Control control = scene switch
        {
            "workspace" => _Workspace(),
            "settings" => _Settings(),
            _ => throw new ArgumentOutOfRangeException(nameof(scene), scene, "unknown scene"),
        };

        var window = new Window { Width = 1200, Height = 800, Content = control };
        window.Show();
        window.UpdateLayout();
        try
        {
            return ThemePalette.Describe(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The plan-flow workspace with no run active — what a freshly opened Autopilot workspace looks like: the
    /// queue bar on top and the "No run is executing" hint filling the rest.
    /// </summary>
    private static AutopilotPlanWorkspaceBody _Workspace()
    {
        var storage = new FakeStorage();
        var host = Substitute.For<ICockpitHost>();
        var context = Substitute.For<IWorkspaceContext>();
        context.Sessions.Returns(Substitute.For<ICockpitSessionObserver>());

        return new AutopilotPlanWorkspaceBody(
            host,
            context,
            new AutopilotSettings(storage),
            new AutopilotPlanController(),
            new AutopilotRunManager(new AutopilotRunQueue(storage), new AutopilotSettings(storage)),
            new AutopilotRunQueue(storage),
            new AutopilotRunHistory(storage),
            new AutopilotTemplateStore(storage));
    }

    /// <summary>The settings dialog opens on its first section ("CEO (planning)") — see AutopilotSettingsSectionsTests.</summary>
    private static AutopilotSettingsControl _Settings()
    {
        var storage = new FakeStorage();
        var host = Substitute.For<ICockpitHost>();
        host.RegisteredAutopilotTemplates.Returns([]);

        return new AutopilotSettingsControl(new AutopilotSettings(storage), host, new AutopilotTemplateStore(storage));
    }

    /// <summary>An in-memory <see cref="IPluginStorage"/> that round-trips through the object itself, like the settings-sections test's own fake.</summary>
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var value) && value is T typed ? typed : default;

        public void Set<T>(string key, T value) => _data[key] = value;
    }

    /// <summary>Line endings are the checkout's, not the palette's.</summary>
    private static string _Normalised(string report) => report.ReplaceLineEndings("\n");
}
