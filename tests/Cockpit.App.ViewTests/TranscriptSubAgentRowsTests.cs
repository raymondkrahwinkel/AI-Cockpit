using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public sealed class TranscriptSubAgentRowsTests
{
    private const int RowCount = 1_281;

    [Fact]
    public void AllocationMeter_DetectsBothANoOpAndAKnownTenMegabytes()
    {
        _ = _Allocated(static () => { });
        _ = _Allocated(_AllocateTenMegabytes);

        Assert.InRange(_Allocated(static () => { }), 0, 64 * 1024);
        Assert.InRange(_Allocated(_AllocateTenMegabytes), 10 * 1024 * 1024, 11 * 1024 * 1024);
    }

    [Fact]
    public void RowsWithoutSubAgents_CreateNoSubAgentCollectionsOrBackingLists()
    {
        TranscriptEntryViewModel[]? rows = null;
        var allocated = _Allocated(() => rows = Enumerable.Range(0, RowCount)
            .Select(index => _ToolRow(index.ToString()))
            .ToArray());

        Assert.True(allocated > 0);
        Assert.NotNull(rows);
        Assert.All(rows, row => Assert.Equal("0 sub-agent events", row.SubAgentSummaryText));
        Assert.Equal((Collections: 0, Lists: 0), _ObjectCounts(rows));
    }

    [Fact]
    public void RenderingRowsWithoutSubAgents_DoesNotCreateTheirCollections()
    {
        HeadlessAvalonia.Run(() =>
        {
            var rows = Enumerable.Range(0, 8).Select(index => _ToolRow(index.ToString())).ToArray();
            var panel = new StackPanel();
            foreach (var row in rows)
            {
                panel.Children.Add(new TranscriptRowView { DataContext = row });
            }

            var window = new Window
            {
                Width = 400,
                Height = 600,
                Content = panel,
            };

            window.Show();
            window.UpdateLayout();

            Assert.Equal((Collections: 0, Lists: 0), _ObjectCounts(rows));
            window.Close();
        });
    }

    [Fact]
    public void RowsWithSubAgents_KeepTheirSummaryAndExpandBehavior()
    {
        var row = _ToolRow("anchor");
        row.SubAgentRows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "nested"));

        Assert.True(row.HasSubAgentRows);
        Assert.Equal("1 sub-agent event", row.SubAgentSummaryText);

        row.ToggleSubAgentExpandedCommand.Execute(null);

        Assert.True(row.IsSubAgentExpanded);
    }

    private static TranscriptEntryViewModel _ToolRow(string text) =>
        new(TranscriptEntryKind.ToolUse, text) { ToolName = "Task" };

    private static long _Allocated(Action action)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        action();
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    private static void _AllocateTenMegabytes()
    {
        var allocation = new byte[10 * 1024 * 1024];
        GC.KeepAlive(allocation);
    }

    private static (int Collections, int Lists) _ObjectCounts(IEnumerable<TranscriptEntryViewModel> rows)
    {
        var collections = rows
            .Select(row => _SubAgentRowsField is null ? row.SubAgentRows : _SubAgentRowsField.GetValue(row))
            .OfType<ObservableCollection<TranscriptEntryViewModel>>()
            .ToArray();

        return (collections.Length, collections.Count(collection => _ItemsProperty.GetValue(collection) is List<TranscriptEntryViewModel>));
    }

    private static readonly FieldInfo? _SubAgentRowsField = typeof(TranscriptEntryViewModel)
        .GetField("_subAgentRows", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly PropertyInfo _ItemsProperty = typeof(Collection<TranscriptEntryViewModel>)
        .GetProperty("Items", BindingFlags.NonPublic | BindingFlags.Instance)!;
}
