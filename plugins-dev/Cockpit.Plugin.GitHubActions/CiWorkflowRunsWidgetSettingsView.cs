using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.GitHubActions;

// One dock-panel instance's settings: how many recent workflow runs the pane shows (AC-1065), mirroring
// GitHubPullRequestsWidgetSettingsView. Reads and writes through the instance's own `IWidgetContext.Storage`, so
// two panels placed side by side keep separate counts.
internal sealed class CiWorkflowRunsWidgetSettingsView : UserControl, IPluginSettingsView
{
    private readonly IWidgetContext _context;
    private readonly NumericUpDown _maxItems = new()
    {
        Minimum = CiWorkflowRunsWidgetConfig.MinItems,
        Maximum = CiWorkflowRunsWidgetConfig.MaxItemsAllowed,
        Increment = 1,
        FormatString = "0",
        Width = 120,
    };

    public CiWorkflowRunsWidgetSettingsView(IWidgetContext context)
    {
        _context = context;

        var config = (context.Storage.Get<CiWorkflowRunsWidgetConfig>(CiWorkflowRunsWidgetConfig.StorageKey)
            ?? CiWorkflowRunsWidgetConfig.Default).Sanitized();
        _maxItems.Value = config.MaxItems;

        Content = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(4),
            Children =
            {
                new TextBlock { Text = "Workflow runs to show", FontWeight = FontWeight.SemiBold },
                _maxItems,
                new TextBlock
                {
                    Text = "How many of the branch's newest workflow runs this pane lists (1–20).",
                    FontSize = 12,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
    }

    public bool TryStage(out Action? commit, out string? error)
    {
        commit = () => _context.Storage.Set(CiWorkflowRunsWidgetConfig.StorageKey, new CiWorkflowRunsWidgetConfig
        {
            MaxItems = (int)(_maxItems.Value ?? CiWorkflowRunsWidgetConfig.Default.MaxItems),
        }.Sanitized());
        error = null;
        return true;
    }
}
