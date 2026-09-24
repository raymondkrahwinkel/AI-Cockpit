using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.GitStatus.Tests;

// An `ICockpitHost` that records every contribution a plugin registers through it (AC-522), for
// `GitStatusPluginLoadTests` to count. Its channel is the one `GitStatusHeaderControlTests` hands the UI part,
// so the header asks the real backend part (AC-1390).
internal sealed class FakeCockpitHost(ICockpitActions actions) : ICockpitHost
{
    private readonly List<IWorkflowStep> _workflowSteps = [];

    public int SettingsRegistered { get; private set; }

    public int SessionHeaderItemsRegistered { get; private set; }

    public List<string> SideMenuButtons { get; } = [];

    public List<string> SideMenuSections { get; } = [];

    public IReadOnlyList<IWorkflowStep> WorkflowSteps => _workflowSteps;

    public IServiceProvider Services { get; } = new NoServices();

    public ICockpitActions Actions { get; } = actions;

    public IPluginStorage Storage { get; } = new InMemoryPluginStorage();

    public InProcessChannel Bridge { get; } = new();

    public IPluginBackendChannel Channel => Bridge;

    public void AddSettings(Func<Control> createView) => SettingsRegistered++;

    public void AddSideMenuButton(string title, Action onInvoke) => SideMenuButtons.Add(title);

    public void AddSideMenuSection(string title, Func<Control> createView) => SideMenuSections.Add(title);

    public void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView) => SessionHeaderItemsRegistered++;

    public void AddWorkflowStep(IWorkflowStep step) => _workflowSteps.Add(step);

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) => Task.CompletedTask;

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
