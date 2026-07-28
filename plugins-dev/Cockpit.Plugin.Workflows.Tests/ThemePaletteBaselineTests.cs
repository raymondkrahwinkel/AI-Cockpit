using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugin.Workflows.Canvas;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.TestSupport;
using NSubstitute;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// This plugin's half of the AC-338 theme-palette baseline: what its own surfaces actually paint, held against a
/// committed file the same way <c>Cockpit.App.ViewTests.ThemePaletteBaselineTests</c> holds the host's. Two
/// surfaces stand in for the plugin — the canvas (the one AC-337 was about) and the flow list a session lands on.
/// </summary>
/// <remarks>
/// To re-record after an intended change: run with <c>COCKPIT_UPDATE_THEME_BASELINES=1</c>, review the diff, then
/// run again without it. The rewriting run still fails on purpose.
/// </remarks>
[Collection("avalonia")]
public class ThemePaletteBaselineTests
{
    public static TheoryData<string> Scenes => ["canvas", "manager"];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void AScene_PaintsNothingItsBaselineDoesNotAccountFor(string scene)
    {
        var painted = _Painted(scene);

        var baseline = Path.Combine(RepositoryPaths.Root, "plugins-dev", "Cockpit.Plugin.Workflows.Tests", "Baselines", $"{scene}.palette.txt");
        ThemePaletteBaseline.Verify(baseline, painted);
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

        var painted = _Painted("manager");

        Assert.Contains(ThemePalette.Hex(primary), painted, StringComparison.Ordinal);
    }

    private static string _Painted(string scene)
    {
        Control control = scene switch
        {
            "canvas" => _Canvas(),
            "manager" => _Manager(),
            _ => throw new ArgumentOutOfRangeException(nameof(scene), scene, "unknown scene"),
        };

        var window = new Window { Width = 1100, Height = 700, Content = control };
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

    /// <summary>Trigger, decision and action side by side — the three kinds a card can be, so the trigger accent and the decision's own gold both show up.</summary>
    private static WorkflowCanvas _Canvas()
    {
        var flow = new Workflow { Id = "w", Name = "Ticket → branch → agent" };
        flow.Nodes.Add(new WorkflowNode { Id = "a", TypeId = "cockpit.manual", Name = "Run manually", X = 40, Y = 40 });
        flow.Nodes.Add(new WorkflowNode { Id = "b", TypeId = "cockpit.if", Name = "Did it pass?", X = 420, Y = 40 });
        flow.Nodes.Add(new WorkflowNode { Id = "c", TypeId = "cockpit.notify", Name = "Post the result", X = 800, Y = 40 });
        flow.Connect("a", 0, "b");
        flow.Connect("b", 0, "c");

        return new WorkflowCanvas(flow);
    }

    /// <summary>One armed flow and one idle one, so both the "Active" and "Inactive" toggle states are on screen.</summary>
    private static WorkflowManagerControl _Manager()
    {
        var workflows = new List<Workflow>
        {
            new()
            {
                Id = "w1",
                Name = "Ticket to branch",
                IsActive = true,
                Nodes = { new WorkflowNode { Id = "a", TypeId = "cockpit.manual", Name = "Start" } },
            },
            new()
            {
                Id = "w2",
                Name = "Nightly report",
                IsActive = false,
                Nodes =
                {
                    new WorkflowNode { Id = "a", TypeId = "cockpit.manual", Name = "Start" },
                    new WorkflowNode { Id = "b", TypeId = "cockpit.notify", Name = "Post" },
                },
            },
        };

        var host = Substitute.For<ICockpitHost>();
        return new WorkflowManagerControl(workflows, host, templates: [], save: () => { });
    }

}
