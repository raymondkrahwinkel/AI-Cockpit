using Cockpit.Core.Secrets;
using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Tests.Workspaces;

/// <summary>
/// Exporting a dashboard to keep or to hand over (Raymond, 2026-07-15: "een import/export systeem … zodat je
/// zelf een backup kan maken van een dashboard of hem kan delen met anderen"). The case that matters most is
/// the one nobody asks for: a dashboard you "just share" must not carry a credential.
/// </summary>
public class DashboardExporterTests
{
    [Fact]
    public void ToExport_CarriesTheArrangementAndEachWidgetsConfig()
    {
        var dashboard = _Dashboard(("p1", "clock.time", 0, 0), ("p2", "monitor.usage", 2, 0));

        var export = DashboardExporter.ToExport(dashboard, _ => new Dictionary<string, string> { ["metrics"] = "\"cpu\"" }, SecretFields.ByName);

        Assert.Equal("Monitoring", export.Name);
        Assert.Equal(8, export.Layout.Columns);
        Assert.Equal(new[] { "clock.time", "monitor.usage" }, export.Panes.Select(pane => pane.WidgetId));
        Assert.Equal(new GridCell(2, 0), export.Panes[1].Cell);
        Assert.Equal("\"cpu\"", export.Panes[0].Config["metrics"]);
    }

    [Fact]
    public void ToExport_DropsCredentials_SoASharedDashboardCarriesNoKey()
    {
        var dashboard = _Dashboard(("p1", "weather.now", 0, 0));
        var config = new Dictionary<string, string>
        {
            ["city"] = "\"Zwolle\"",
            ["apiKey"] = "\"live-key\"",
            ["token"] = "\"live-token\"",
            ["refreshSeconds"] = "60",
        };

        var export = DashboardExporter.ToExport(dashboard, _ => config, SecretFields.ByName);

        var exportedConfig = export.Panes[0].Config;
        Assert.Contains("city", exportedConfig);
        Assert.Contains("refreshSeconds", exportedConfig);
        Assert.DoesNotContain("apiKey", exportedConfig);
        Assert.DoesNotContain("token", exportedConfig);
        Assert.DoesNotContain(exportedConfig.Values, value => value.Contains("live-"));
    }

    [Fact]
    public void ToExport_DropsAKeyOnlyThePluginKnowsIsSecret()
    {
        // The name rule cannot guess "pat"; the plugin declares it, and the exporter has to honour that or the
        // declaration only protects the backup and not the thing you hand to someone.
        var dashboard = _Dashboard(("p1", "tracker.issues", 0, 0));
        var config = new Dictionary<string, string> { ["pat"] = "\"ghp-live\"", ["repo"] = "\"cockpit\"" };

        var export = DashboardExporter.ToExport(dashboard, _ => config, new SecretFields(["pat"]));

        Assert.Contains("repo", export.Panes[0].Config);
        Assert.DoesNotContain("pat", export.Panes[0].Config);
    }

    [Fact]
    public void ToExport_ASessionsWorkspace_HasNoWidgetsToExport()
    {
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions)
            .WithPane(new WorkspacePane("s1", PaneKind.AiSession));

        Assert.Empty(DashboardExporter.ToExport(sessions, _ => new Dictionary<string, string>(), SecretFields.ByName).Panes);
    }

    [Fact]
    public void FromExport_AMissingWidget_SkipsThatPaneAndNamesThePluginToInstall()
    {
        // Raymond's call: one absent widget out of ten costs you that widget, not the dashboard. The report is
        // what turns "something is missing" into "install this plugin".
        var export = DashboardExporter.ToExport(
            _Dashboard(("p1", "clock.time", 0, 0), ("p2", "weather.now", 2, 0)),
            _ => new Dictionary<string, string>(),
            SecretFields.ByName);

        var import = DashboardExporter.FromExport(export, isInstalled: id => id == "clock.time");

        Assert.Equal("clock.time", Assert.Single(import.Workspace.Panes).WidgetId);
        Assert.Equal(new[] { "weather.now" }, import.MissingWidgetIds);
        Assert.False(import.IsComplete);
    }

    [Fact]
    public void FromExport_TheSameMissingWidgetTwice_IsReportedOnce()
    {
        // Four clocks whose plugin is gone is one thing to install, not four things to read.
        var export = DashboardExporter.ToExport(
            _Dashboard(("p1", "weather.now", 0, 0), ("p2", "weather.now", 2, 0)),
            _ => new Dictionary<string, string>(),
            SecretFields.ByName);

        Assert.Equal(new[] { "weather.now" }, DashboardExporter.FromExport(export, isInstalled: _ => false).MissingWidgetIds);
    }

    [Fact]
    public void FromExport_EverythingAvailable_ImportsWhole()
    {
        var export = DashboardExporter.ToExport(
            _Dashboard(("p1", "clock.time", 0, 0)), _ => new Dictionary<string, string>(), SecretFields.ByName);

        var import = DashboardExporter.FromExport(export, isInstalled: _ => true);

        Assert.True(import.IsComplete);
        Assert.Empty(import.MissingWidgetIds);
    }

    [Fact]
    public void FromExport_RebuildsTheDashboardWithFreshInstanceIds()
    {
        var export = DashboardExporter.ToExport(
            _Dashboard(("p1", "clock.time", 1, 2)), _ => new Dictionary<string, string>(), SecretFields.ByName);

        var (workspace, _, _) = DashboardExporter.FromExport(export, _Anything);

        Assert.Equal(WorkspaceType.Dashboard, workspace.Type);
        Assert.Equal("Monitoring", workspace.Name);
        Assert.Single(workspace.Panes);
        Assert.Equal("clock.time", workspace.Panes[0].WidgetId);
        Assert.Equal(new GridCell(1, 2), workspace.Panes[0].Cell);
        Assert.NotEqual("p1", workspace.Panes[0].Id);
    }

    [Fact]
    public void FromExport_HandsBackTheConfigKeyedByTheNewInstanceId()
    {
        var export = DashboardExporter.ToExport(
            _Dashboard(("p1", "monitor.usage", 0, 0)), _ => new Dictionary<string, string> { ["metrics"] = "\"cpu\"" }, SecretFields.ByName);

        var (workspace, config, _) = DashboardExporter.FromExport(export, _Anything);

        Assert.Contains(workspace.Panes[0].Id, config);
        Assert.Equal("\"cpu\"", config[workspace.Panes[0].Id]["metrics"]);
    }

    [Fact]
    public void FromExport_ImportingTwice_YieldsTwoIndependentDashboards()
    {
        var export = DashboardExporter.ToExport(
            _Dashboard(("p1", "clock.time", 0, 0)), _ => new Dictionary<string, string>(), SecretFields.ByName);

        var (first, _, _) = DashboardExporter.FromExport(export, _Anything);
        var (second, _, _) = DashboardExporter.FromExport(export, _Anything);

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Panes[0].Id, second.Panes[0].Id);
    }

    [Fact]
    public void FromExport_CanBeGivenAName_SoAnImportNeedNotCollideWithWhatIsAlreadyThere()
    {
        var export = DashboardExporter.ToExport(
            _Dashboard(("p1", "clock.time", 0, 0)), _ => new Dictionary<string, string>(), SecretFields.ByName);

        Assert.Equal("Monitoring 2", DashboardExporter.FromExport(export, _Anything, name: "Monitoring 2").Workspace.Name);
    }

    [Fact]
    public void FromExport_AnOutOfRangeGrid_IsClamped_SoAHandEditedFileCannotDivideByZero()
    {
        var export = new DashboardExport(1, "D", new DashboardLayout { Columns = 0, Rows = 0 }, []);

        Assert.Equal(DashboardLayout.MinColumns, DashboardExporter.FromExport(export, _Anything).Workspace.Layout.Columns);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void CanRead_RefusesANewerFormat_RatherThanHalfReadingIt(int version, bool expected)
    {
        // A dashboard that silently arrives missing whatever the reader did not understand is worse than one
        // that does not arrive.
        Assert.Equal(expected, DashboardExporter.CanRead(new DashboardExport(version, "D", DashboardLayout.Default, [])));
    }

    /// <summary>Every widget is installed — for the tests that are about something other than what is missing.</summary>
    private static readonly Func<string, bool> _Anything = _ => true;

    private static Workspace _Dashboard(params (string Id, string WidgetId, int Column, int Row)[] panes)
    {
        var dashboard = Workspace.Create("Monitoring", WorkspaceType.Dashboard) with
        {
            Layout = new DashboardLayout { Columns = 8, Rows = 6 },
        };

        foreach (var pane in panes)
        {
            dashboard = dashboard.WithPane(new WorkspacePane(pane.Id, PaneKind.Widget)
            {
                WidgetId = pane.WidgetId,
                Cell = new GridCell(pane.Column, pane.Row),
            });
        }

        return dashboard;
    }
}
