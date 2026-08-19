using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.GitStatus.Tests;

// An `ICockpitHost` that records every contribution a plugin registers through it (AC-522) — what
// `GitStatusPluginLoadTests` counts (settings view, session-header item, workflow steps, and the
// side-menu button/section that must now be absent), and what `GitStatusHeaderControlTests`
// constructs a real header control against, where only `Actions` is ever touched.
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

    // The intents the header badge sends (AC-961), and whether a handler is pretended to exist for them.
    public List<PluginIntent> SentIntents { get; } = [];

    public HashSet<string> HandledIntents { get; } = [];

    public bool CanSendIntent(string targetPluginId, string action) => HandledIntents.Contains($"{targetPluginId}/{action}");

    public Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data)
    {
        SentIntents.Add(new PluginIntent("git-status", targetPluginId, action, data));
        return Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string>());
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
