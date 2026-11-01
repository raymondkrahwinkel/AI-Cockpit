using Avalonia.Controls;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugin.Workflows.UI.Canvas;
using Cockpit.Plugins.Abstractions.Profiles;

namespace Cockpit.Plugin.Workflows.UI;

internal static class WorkflowsScenes
{
    public static IReadOnlyDictionary<string, Func<Control>> Views { get; } = new Dictionary<string, Func<Control>>
    {
        ["workflow-personal-login"] = () => _StartSession("Personal"),
        ["workflow-api-key"] = () => _StartSession("API key"),
        ["workflow-profile-expression"] = () => _StartSession("{profile}"),
    };

    private static Control _StartSession(string profile)
    {
        var start = new WorkflowNode
        {
            Id = "start",
            TypeId = "cockpit.start-session",
            Name = "Start session",
            Parameters = { ["Profile"] = profile, ["Prompt"] = "Review the incoming request." },
        };
        var workflow = new Workflow
        {
            Id = "review",
            Name = "Review incoming requests",
            Nodes =
            {
                new WorkflowNode { Id = "trigger", TypeId = "cockpit.text-match", Name = "Text appears" },
                start,
            },
        };
        var dialog = new NodeDialog(workflow,
        [
            new PluginProfileInfo("Personal", "Plugin", "") { UsesPersonalLogin = true },
            new PluginProfileInfo("API key", "Plugin", "") { UsesPersonalLogin = false },
        ]);
        dialog.Show(start, [], [], [], []);
        return dialog;
    }
}
