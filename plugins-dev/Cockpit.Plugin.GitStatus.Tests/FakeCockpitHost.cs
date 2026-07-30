using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>
/// An <see cref="ICockpitHost"/> that records every contribution a plugin registers through it (AC-522) — what
/// <see cref="GitStatusPluginLoadTests"/> counts (settings view, session-header item, workflow steps, and the
/// side-menu button/section that must now be absent), and what <see cref="GitStatusHeaderControlTests"/>
/// constructs a real header control against, where only <see cref="Actions"/> is ever touched.
/// </summary>
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
