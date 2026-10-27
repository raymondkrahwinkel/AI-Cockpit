using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Projects;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewTests;

// AC-1601: the B2 detail pane, measured on the real markup.
[Collection("avalonia")]
public sealed class Ac1601ProjectDetailPaneTests
{
    private static readonly Project _local = Project.Create("Flow") with
    {
        DefaultProfileLabel = "Claude · Opus",
        SourceDirectories = [new(@"C:\Projects\Flow")],
        AdditionalInfo = [new ProjectInfoField("Repository", "https://example.test/flow")],
        Jobs = [new ProjectJob("Check the nightly pipelines.", "Reads only")],
    };

    private static (ProjectCardActions Actions, List<string> Log) _Actions()
    {
        var log = new List<string>();
        ICommand Record(string name) => new RelayCommand<object?>(argument => log.Add($"{name}:{_Describe(argument)}"));
        return (new ProjectCardActions(
            Record("Start"), Record("StartWithOptions"), Record("StartJob"), Record("Edit"),
            Record("OpenFolder"), Record("ToggleSharing"), Record("SyncNow")), log);
    }

    private static string _Describe(object? argument) => argument switch
    {
        Project project => project.Id,
        ProjectJobChoice choice => choice.Job.Prompt,
        _ => argument?.ToString() ?? string.Empty,
    };

    private static void _Show(ProjectListItemViewModel item, Action<Window> check) => HeadlessAvalonia.Run(() =>
    {
        var window = new Window { Width = 400, Height = 1600, Content = new ProjectDetailPane { DataContext = item } };
        window.Show();
        try
        {
            window.UpdateLayout();
            check(window);
        }
        finally
        {
            window.Close();
        }
    });

    private static Button _Button(Window window, string label) =>
        window.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible
            && button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == label));

    // Layer 2 (view). The pane offers everything: Start starts at once, only its ▾ item goes through the options path, and
    // every action the old card and its ⋯ held (inventory #10–20 and #29) can be pressed.
    [Fact]
    public void TheDetailPane_OffersEverything()
    {
        _AssertSplitStart();
        _AssertEveryActionReachable();
    }

    private static void _AssertSplitStart()
    {
        var (actions, log) = _Actions();
        _Show(new ProjectListItemViewModel(_local, 0, actions: actions), window =>
        {
            var split = window.GetVisualDescendants().OfType<SplitButton>().Single(button => button.IsEffectivelyVisible);
            split.Command!.Execute(split.CommandParameter);
            Assert.Equal([$"Start:{_local.Id}"], log);

            // The menu inherits the pane's data context only once it is open.
            split.Flyout!.ShowAt(split);
            Dispatcher.UIThread.RunJobs();
            var item = Assert.IsType<MenuFlyout>(split.Flyout).Items.OfType<MenuItem>().Single(menuItem => (string?)menuItem.Header == "Start, but change something first…");
            item.Command!.Execute(item.CommandParameter);
            Assert.Equal([$"Start:{_local.Id}", $"StartWithOptions:{_local.Id}"], log);
        });
    }

    private static void _AssertEveryActionReachable()
    {
        var offer = new SharedProjectOffer("depot:api", "API Gateway", Organization: new SharedProjectOrganizationInfo("synvolution", "Synvolution", "Editor"));
        foreach (var (label, bound, expected) in new (string, bool?, string)[]
        {
            ("Open folder", true, $"OpenFolder:{_local.Id}"),
            ("Edit project…", true, $"Edit:{_local.Id}"),
            ("Sync now", true, $"SyncNow:{_local.Id}"),
            ("Stop sharing…", true, $"ToggleSharing:{_local.Id}"),
            ("Share with your team…", false, $"ToggleSharing:{_local.Id}"),
            ("Run", true, "StartJob:Check the nightly pipelines."),
            ("Remove from this machine…", true, $"Remove:{_local.Id}"),
            ("Hide on this machine", null, "Hide:depot:api"),
        })
        {
            var (actions, log) = _Actions();
            ICommand Record(string name) => new RelayCommand<object?>(argument => log.Add($"{name}:{_Describe(argument)}"));
            var item = bound is null
                ? new ProjectListItemViewModel(offer) { Hide = Record("Hide") }
                : new ProjectListItemViewModel(_local, 0, isInOrganization: bound.Value, actions: actions)
                {
                    Jobs = [new ProjectJobChoice(_local, _local.Jobs[0])],
                    Remove = Record("Remove"),
                };
            _Show(item, window =>
            {
                var button = _Button(window, label);
                button.Command!.Execute(button.CommandParameter);
                Assert.Equal([expected], log);
            });
        }

        _Show(new ProjectListItemViewModel(_local, 0), window =>
            Assert.True(window.GetVisualDescendants().OfType<ProjectInfoList>().Single().IsEffectivelyVisible, "Additional info shows under Details"));
    }
}
