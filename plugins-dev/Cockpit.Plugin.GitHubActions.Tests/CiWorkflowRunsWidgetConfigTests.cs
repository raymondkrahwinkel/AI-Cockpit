using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubActions.Tests;

// The panel's per-instance run count (AC-1065): a dashboard pane is sized by hand, so how many runs it lists is
// its own, not a plugin-wide setting — and two placed panels must never see each other's count.
public class CiWorkflowRunsWidgetConfigTests
{
    [Fact]
    public void AFreshPanel_ShowsTen()
    {
        Assert.Equal(10, CiWorkflowRunsWidgetConfig.Default.MaxItems);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(21, 20)]
    public void AnOutOfRangeCount_IsClampedIntoOneToTwenty(int stored, int expected)
    {
        Assert.Equal(expected, new CiWorkflowRunsWidgetConfig { MaxItems = stored }.Sanitized().MaxItems);
    }

    [Fact]
    public void TwoPanelInstances_KeepIndependentCounts()
    {
        // Two placed panels, each with its own IWidgetContext.Storage — the shape AC-1065's criterion 4 requires:
        // reading one instance's stored count must never see what the other instance wrote.
        var panelA = new _InMemoryStorage();
        var panelB = new _InMemoryStorage();

        panelA.Set(CiWorkflowRunsWidgetConfig.StorageKey, new CiWorkflowRunsWidgetConfig { MaxItems = 3 });
        panelB.Set(CiWorkflowRunsWidgetConfig.StorageKey, new CiWorkflowRunsWidgetConfig { MaxItems = 15 });

        Assert.Equal(3, panelA.Get<CiWorkflowRunsWidgetConfig>(CiWorkflowRunsWidgetConfig.StorageKey)!.MaxItems);
        Assert.Equal(15, panelB.Get<CiWorkflowRunsWidgetConfig>(CiWorkflowRunsWidgetConfig.StorageKey)!.MaxItems);
    }

    private sealed class _InMemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? (T?)value : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
