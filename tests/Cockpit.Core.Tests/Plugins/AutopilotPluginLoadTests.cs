using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

// AC-1398: Autopilot's backend part names no UI type, so a backend without a window loads it (BackendPluginStartupTests),
// and its UI part is a separate entry type in the same assembly. The desktop activates that UI part from the assembly
// the backend part already loaded, and it registers the workspace. Mirrors ClockPluginLoadTests.
public class AutopilotPluginLoadTests
{
    [Fact]
    public void TheUiPart_ActivatesFromTheBackendPartsOwnAssembly_AndRegistersTheWorkspace()
    {
        var dll = Directory
            .EnumerateFiles(Path.Combine(_RepositoryRoot(), "plugins-dev", "Cockpit.Plugin.Autopilot", "bin"), "Cockpit.Plugin.Autopilot.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
        var folder = Path.GetDirectoryName(dll) ?? string.Empty;
        Assert.True(PluginManifest.TryParse(File.ReadAllText(Path.Combine(folder, "plugin.json")), out var manifest, out var error), error);
        var discovered = new DiscoveredPlugin(folder, "autopilot", manifest ?? throw new InvalidOperationException(error), PluginHash.Compute(File.ReadAllBytes(dll)), PluginLoadDecision.Load);

        var backend = new PluginActivator(NullLogger<PluginActivator>.Instance).Activate(discovered) ?? throw new InvalidOperationException("Autopilot's backend part did not activate.");
        backend.Initialize(Substitute.For<ICockpitHost>());
        var ui = PluginUiManager.ActivateUi(discovered, backend) ?? throw new InvalidOperationException("Autopilot's UI part did not activate.");
        var uiHost = Substitute.For<ICockpitUiHost>();
        ui.InitializeUi(uiHost);

        Assert.Equal(("AutopilotUi", backend.GetType().Assembly), (ui.GetType().Name, ui.GetType().Assembly));
        uiHost.Received(1).AddWorkspaceType(Arg.Is<WorkspaceTypeRegistration>(registration => registration.Id == "workspace.autopilot.plan"));
    }

    private static string _RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cockpit.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
