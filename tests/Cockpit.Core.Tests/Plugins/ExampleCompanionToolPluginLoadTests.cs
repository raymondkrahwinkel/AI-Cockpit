using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.UI;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

// AC-240 counter-proof: loads the real, compiled example companion-tool plugin and asserts its InitializeUi
// calls ICockpitUiHost.AddCompanionTool with its own tool id. AC-1395: became a pure UI plugin (no backend
// contribution left once InitializeUi could carry the registration itself) — mirrors ClockPluginLoadTests.
public class ExampleCompanionToolPluginLoadTests
{
    [Fact]
    public void ThePluginHasNoBackendPart_AndItsUiPartRegistersItsOwnCompanionTool_WhenBuilt()
    {
        if (_LocatePluginOutput() is not { } folder)
        {
            Assert.Fail("The built ExampleCompanionTool plugin output was not found.");
            return;
        }

        var manifestJson = File.ReadAllText(Path.Combine(folder, "plugin.json"));
        if (!PluginManifest.TryParse(manifestJson, out var manifest, out _) || manifest is not { } parsedManifest)
        {
            Assert.Fail("ExampleCompanionTool's plugin.json did not parse.");
            return;
        }

        Assert.Null(parsedManifest.EntryAssembly);
        Assert.Equal("Cockpit.Plugin.ExampleCompanionTool.dll", parsedManifest.UiAssembly);
        Assert.Equal("Cockpit.Plugin.ExampleCompanionTool.ExampleCompanionToolUi", parsedManifest.UiEntryType);

        var hash = PluginHash.Compute(File.ReadAllBytes(Path.Combine(folder, "Cockpit.Plugin.ExampleCompanionTool.dll")));
        var discovered = new DiscoveredPlugin(folder, "example-companion-tool", parsedManifest, hash, PluginLoadDecision.Load);

        // The backend bootstrap has nothing to activate: no entryAssembly, so PluginActivator refuses politely
        // rather than throwing.
        var activator = new PluginActivator(NullLogger<PluginActivator>.Instance);
        Assert.Null(activator.Activate(discovered));

        var uiPart = Assert.IsAssignableFrom<ICockpitPluginUi>(PluginUiManager.ActivateUi(discovered, backend: null));
        var uiHost = Substitute.For<ICockpitUiHost>();
        uiPart.InitializeUi(uiHost);
        uiHost.Received(1).AddCompanionTool(Arg.Is<CompanionToolRegistration>(registration => registration.Id == "example-companion-tool.hello"));
    }

    // Walks up from the test output to the repo root and finds the plugin's build output (either config).
    private static string? _LocatePluginOutput()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidateRoot = Path.Combine(directory.FullName, "plugins-dev", "Cockpit.Plugin.ExampleCompanionTool", "bin");
            if (Directory.Exists(candidateRoot))
            {
                var dll = Directory
                    .EnumerateFiles(candidateRoot, "Cockpit.Plugin.ExampleCompanionTool.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
                return dll is null ? null : Path.GetDirectoryName(dll);
            }

            directory = directory.Parent;
        }

        return null;
    }
}
