using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

// AC-1395 acceptance 2: a pure UI plugin (no entryAssembly) is skipped by the backend bootstrap and loaded by
// the desktop. Clock, the real built plugin, stands in for the pattern PluginUiPartTests already proves
// generically with a synthetic manifest. Mirrors ClaudeProviderPluginLoadTests.
public class ClockPluginLoadTests
{
    [Fact]
    public void ThePluginHasNoBackendPart_AndItsUiPartRegistersTheClockWidget_WhenBuilt()
    {
        if (_LocatePluginOutput() is not { } folder)
        {
            Assert.Fail("The built Clock plugin output was not found.");
            return;
        }

        var manifestJson = File.ReadAllText(Path.Combine(folder, "plugin.json"));
        if (!PluginManifest.TryParse(manifestJson, out var manifest, out _) || manifest is not { } parsedManifest)
        {
            Assert.Fail("Clock's plugin.json did not parse.");
            return;
        }

        Assert.Null(parsedManifest.EntryAssembly);
        Assert.Equal("Cockpit.Plugin.Clock.dll", parsedManifest.UiAssembly);
        Assert.Equal("Cockpit.Plugin.Clock.ClockUi", parsedManifest.UiEntryType);

        var hash = PluginHash.Compute(File.ReadAllBytes(Path.Combine(folder, "Cockpit.Plugin.Clock.dll")));
        var discovered = new DiscoveredPlugin(folder, "clock", parsedManifest, hash, PluginLoadDecision.Load);

        // The backend bootstrap has nothing to activate: no entryAssembly, so PluginActivator refuses politely
        // rather than throwing.
        var activator = new PluginActivator(NullLogger<PluginActivator>.Instance);
        Assert.Null(activator.Activate(discovered));

        var uiPart = Assert.IsAssignableFrom<ICockpitPluginUi>(PluginUiManager.ActivateUi(discovered, backend: null));
        var uiHost = Substitute.For<ICockpitUiHost>();
        uiPart.InitializeUi(uiHost);
        uiHost.Received(1).AddWidget(Arg.Is<WidgetRegistration>(registration => registration.Id == "widgets.clock"));
    }

    private static string? _LocatePluginOutput()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateRoot = Path.Combine(directory.FullName, "plugins-dev", "Cockpit.Plugin.Clock", "bin");
            if (Directory.Exists(candidateRoot))
            {
                var dll = Directory
                    .EnumerateFiles(candidateRoot, "Cockpit.Plugin.Clock.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
                return dll is null ? null : Path.GetDirectoryName(dll);
            }

            directory = directory.Parent;
        }

        return null;
    }
}
