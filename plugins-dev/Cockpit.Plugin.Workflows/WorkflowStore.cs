using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows;

/// <summary>
/// Where the flows live: the plugin's own slice of <c>cockpit.json</c>, as JSON text. They are saved as text
/// rather than as objects on purpose — a flow you can read, diff and paste into a file is a flow you can share
/// and put in git, which is half of what makes one worth building.
/// </summary>
internal sealed class WorkflowStore(IPluginStorage storage)
{
    private const string Key = "workflows";

    public IReadOnlyList<Workflow> Load() => WorkflowJson.ReadAll(storage.Get<string>(Key));

    public void Save(IReadOnlyList<Workflow> workflows) => storage.Set(Key, WorkflowJson.WriteAll(workflows));
}
