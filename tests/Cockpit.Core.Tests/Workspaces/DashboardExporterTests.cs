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
