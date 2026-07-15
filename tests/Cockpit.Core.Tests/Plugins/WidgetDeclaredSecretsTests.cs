using Cockpit.App.Plugins;
using Cockpit.Core.Secrets;
using Cockpit.Core.Workspaces;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Widgets;
using FluentAssertions;
using NSubstitute;
using Avalonia.Controls;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// A key a plugin declares as a credential has to be dropped from a dashboard export too. The name rule cannot
/// guess "pat", and a declaration honoured by the at-rest encryption and the backup scrubber but not by the
/// file you hand to someone protects the wrong two of the three.
/// </summary>
public class WidgetDeclaredSecretsTests
{
    [Fact]
    public void DeclaredSecretKeys_AreCarriedFromTheRegisteringPlugin()
    {
        var registry = new WidgetRegistry();

        registry.Register(_Widget("tracker.issues"), _Storage(), _Sessions(), ["pat"]);

        registry.DeclaredSecretKeys.Should().Equal("pat");
    }

    [Fact]
    public void DeclaredSecretKeys_AreTheUnionAcrossPlugins_AndDeduplicated()
    {
        // Over-scrubbing costs a plugin a setting whose name another declared secret; under-scrubbing ships a
        // live credential. The first is the one you can afford.
        var registry = new WidgetRegistry();

        registry.Register(_Widget("a.one"), _Storage(), _Sessions(), ["pat"]);
        registry.Register(_Widget("b.two"), _Storage(), _Sessions(), ["credential", "PAT"]);

        registry.DeclaredSecretKeys.Should().BeEquivalentTo(["pat", "credential"]);
    }

    [Fact]
    public void DeclaredSecretKeys_NoPluginDeclaringAny_IsEmpty()
    {
        var registry = new WidgetRegistry();
        registry.Register(_Widget("clock.time"), _Storage(), _Sessions(), []);

        registry.DeclaredSecretKeys.Should().BeEmpty();
    }

    [Fact]
    public void AnExport_DropsADeclaredKey_AlongsideTheOnesTheNameRuleKnows()
    {
        // The end-to-end shape: what the registry collected is what the exporter scrubs by.
        var registry = new WidgetRegistry();
        registry.Register(_Widget("tracker.issues"), _Storage(), _Sessions(), ["pat"]);

        var dashboard = Workspace.Create("Board", WorkspaceType.Dashboard)
            .WithPane(new WorkspacePane("p1", PaneKind.Widget) { WidgetId = "tracker.issues" });
        var config = new Dictionary<string, string>
        {
            ["pat"] = "\"ghp-live\"",
            ["apiKey"] = "\"live-key\"",
            ["repo"] = "\"cockpit\"",
        };

        var export = DashboardExporter.ToExport(dashboard, _ => config, new SecretFields(registry.DeclaredSecretKeys));

        export.Panes[0].Config.Should().ContainKey("repo");
        export.Panes[0].Config.Should().NotContainKey("pat").And.NotContainKey("apiKey");
        export.Panes[0].Config.Values.Should().NotContain(value => value.Contains("live") || value.Contains("ghp"));
    }

    private static WidgetRegistration _Widget(string id) => new(id, id, _ => new TextBlock());

    private static IPluginStorage _Storage() => Substitute.For<IPluginStorage>();

    private static ICockpitSessionObserver _Sessions() => Substitute.For<ICockpitSessionObserver>();
}
