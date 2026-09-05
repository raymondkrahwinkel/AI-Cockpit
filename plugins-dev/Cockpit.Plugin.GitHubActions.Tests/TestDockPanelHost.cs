using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Docking;

namespace Cockpit.Plugin.GitHubActions.Tests;

// A minimal `ICockpitHost` for exercising `CiWorkflowRunsDockPanelRegistrar`, mirroring the pull-requests plugin's
// own TestDockPanelHost: records every `AddDockPanel` registration, and every other required member throws — the
// registrar touches none of them.
internal sealed class TestDockPanelHost : ICockpitHost
{
    // Simulates an older host whose copy of Cockpit.Plugins.Abstractions predates the dock-rail panel — the exact
    // failure the registrar's own try/catch exists for.
    public Func<Exception>? DockPanelUnsupportedException { get; init; }

    public List<DockPanelRegistration> RegisteredPanels { get; } = [];

    public IServiceProvider Services => throw new NotSupportedException();

    public ICockpitActions Actions => throw new NotSupportedException();

    public IPluginStorage Storage => throw new NotSupportedException();

    public void AddSettings(Func<Control> createView) => throw new NotSupportedException();

    public void AddSideMenuButton(string title, Action onInvoke) => throw new NotSupportedException();

    public void AddSideMenuSection(string title, Func<Control> createView) => throw new NotSupportedException();

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        throw new NotSupportedException();

    public void AddDockPanel(DockPanelRegistration registration)
    {
        if (DockPanelUnsupportedException is { } makeException)
        {
            throw makeException();
        }

        RegisteredPanels.Add(registration);
    }
}
