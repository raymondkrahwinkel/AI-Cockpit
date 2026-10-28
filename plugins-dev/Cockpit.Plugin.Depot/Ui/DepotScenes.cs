using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Cockpit.Plugin.Depot.Contracts;

namespace Cockpit.Plugin.Depot.UI;

// Fixed, network-free models for `--screenshot --scene plugin:<name>`. They drive the real browser panel.
internal static class DepotScenes
{
    private static readonly DepotBrowserProject _Project = new("krahwinkel-it/cockpit", "krahwinkel-it/cockpit", "Editor");
    private static readonly DateTimeOffset _Modified = new(2026, 10, 24, 16, 12, 0, TimeSpan.Zero);
    private static readonly byte[] _ImageBytes = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");

    public static IReadOnlyDictionary<string, Func<Control>> All { get; } = new Dictionary<string, Func<Control>>
    {
        ["artifact-tab-image"] = () => new ArtifactWorkspace(new DepotBrowserNode("Designs/mockup.gif", DepotBrowserKind.Artifact, 43, _Modified, "image/gif"), _ImageBytes),
        ["artifact-tab-text"] = () => new ArtifactWorkspace(new DepotBrowserNode("Notes/release.txt", DepotBrowserKind.Artifact, 19, _Modified, "text/plain"), "Cockpit artifact text"u8.ToArray()),
        ["depot-browser"] = () => _Scene(new DepotBrowserSceneModel(
            _Project,
            new DepotBrowserTree(
            [
                new("Ideas/depot-sync.md", DepotBrowserKind.Document, 2048, _Modified, "text/markdown"),
                new("Ideas/voice-notes.md", DepotBrowserKind.Document, 912, _Modified.AddDays(-1), "text/markdown"),
                new("Mockups/AC-1749/v003.html", DepotBrowserKind.Artifact, 74845, _Modified.AddDays(2), "text/html"),
            ],
            true),
            "Ideas/depot-sync.md",
            new DepotDocumentExcerpt("Depot-sync for sessions", "A session can write its transcript to Depot so the project memory remains available outside the conversation."))),
        ["depot-browser-settings"] = () => _Scene(new DepotBrowserSceneModel(
            _Project,
            new DepotBrowserTree(
            [
                new("Ideas/depot-sync.md", DepotBrowserKind.Document, 196, _Modified, "text/markdown"),
                new("Ideas/edge-cases.md", DepotBrowserKind.Document, 407, _Modified, "text/markdown"),
                new("Plan/AC-1708-dock-panels.md", DepotBrowserKind.Document, 116, _Modified, "text/markdown"),
                new("Notes.md", DepotBrowserKind.Document, 68, _Modified, "text/markdown"),
            ],
            true),
            "Ideas/depot-sync.md",
            new DepotDocumentExcerpt("Depot-sync for sessions", "Idea: a session can write its transcript to Depot."),
            new Dictionary<string, string> { ["Ideas/depot-sync.md"] = "◌ preview", ["Notes.md"] = "● open" },
            BelowTree: true)),
        ["depot-browser-artifact"] = () => _Scene(new DepotBrowserSceneModel(
            _Project,
            new DepotBrowserTree(
            [
                new("Designs/AC-1350/remote-connect-v2/desk-dark.png", DepotBrowserKind.Artifact, 236544, _Modified, "image/png", "share-1", "https://depot.krahwinkel-it.nl/artifact/cockpit/share-1"),
                new("Designs/AC-1350/remote-connect-v2/desk-light.png", DepotBrowserKind.Artifact, 234496, _Modified, "image/png"),
                new("Notes.md", DepotBrowserKind.Document, 11264, _Modified.AddDays(-2), "text/markdown"),
            ],
            true),
            "Designs/AC-1350/remote-connect-v2/desk-dark.png",
            null)),
        ["depot-browser-empty"] = () => _Scene(new DepotBrowserSceneModel(_Project, new DepotBrowserTree([], true), null, null)),
        ["depot-settings-sync"] = _SettingsSyncScene,
        ["depot-new-meeting-note"] = () => DepotMeetingNoteScene.Dialog(created: false),
        ["depot-meeting-note-created"] = () => DepotMeetingNoteScene.Dialog(created: true),
        ["depot-new-markdown"] = () =>
        {
            var dialog = new NewMarkdownFileDialog("krahwinkel-it/cockpit", "Ideas", ["", "Ideas", "Plan"], _ => false, _ => Task.CompletedTask)
                { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };
            var name = dialog.GetLogicalDescendants().OfType<TextBox>().Single();
            name.Text = "md-editor-in-cockpit";
            return dialog;
        },
    };

    private static Control _Scene(DepotBrowserSceneModel model)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("40,*") };
        grid.Children.Add(new Border
        {
            BorderBrush = _Brush("CockpitHairlineSoftBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 0),
            Child = new TextBlock
            {
                Text = "Depot files",
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        var panel = new DepotBrowserPanel(model);
        Grid.SetRow(panel, 1);
        grid.Children.Add(panel);
        return grid;
    }

    private static Control _SettingsSyncScene() => new ScrollViewer
    {
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Depot connections", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Each connection is offered to a session using Depot's own OAuth sign-in — the plugin never holds a token.", TextWrapping = TextWrapping.Wrap },
                _SettingsConnection("Work", "https://depot.example.com", true),
                _SettingsConnection("Personal", "https://depot.example.net", false),
            },
        },
    };

    private static Control _SettingsConnection(string name, string url, bool synchronise) => new Border
    {
        BorderBrush = _Brush("CockpitHairlineSoftBrush"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(12),
        Child = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = name, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = url },
                new ToggleSwitch { Content = "Synchronise settings via this server", IsChecked = synchronise },
                new TextBlock { Text = "Settings use the AC-540 whitelist; keys and tokens never go along. Selecting this server turns sync off for the others. Active after restart.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
            },
        },
    };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}

internal sealed record DepotBrowserSceneModel(
    DepotBrowserProject Project,
    DepotBrowserTree Tree,
    string? SelectedPath,
    DepotDocumentExcerpt? Excerpt,
    IReadOnlyDictionary<string, string>? Badges = null,
    bool BelowTree = false);

// AC-1579: the "New meeting note" dialog on fixture data (mockup 2a and 2b), with nothing asked of Depot.
internal sealed class DepotMeetingNoteScene : IDepotMeetingNotes
{
    private static readonly DepotMeetingNoteProject _Depot = new("krahwinkel-it/depot", "depot", "Krahwinkel IT", "Owner", "Meetings", true, null);

    public static NewMeetingNoteDialog Dialog(bool created)
    {
        var dialog = new NewMeetingNoteDialog(new DepotMeetingNoteScene(), (_, _) => Task.CompletedTask, new DateOnly(2026, 10, 19), TimeSpan.Zero)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
        };
        dialog.Prefill("Sprint review", "s", "j");
        if (created)
        {
            dialog.ShowCreated(_Depot, "Meetings/2026-10-19 Sprint review.md", ["Sanne", "Jithran"]);
        }

        return dialog;
    }

    public Task<DepotBrowserAnswer<DepotMeetingNoteOptions>> OptionsAsync() => Task.FromResult(
        DepotBrowserAnswer<DepotMeetingNoteOptions>.Success(new([_Depot], 0, [_Depot.Address])));

    public Task<DepotBrowserAnswer<DepotMeetingNoteProjectInfo>> ProjectAsync(string project) => Task.FromResult(
        DepotBrowserAnswer<DepotMeetingNoteProjectInfo>.Success(new(["Meetings"], [new("s", "Sanne", "Editor"), new("j", "Jithran", "Viewer"), new("m", "Mirthe", null)])));

    public Task<DepotBrowserAnswer<DepotMeetingNoteCheck>> CheckAsync(string project, string? folder, string topic, DateOnly date) => Task.FromResult(
        DepotBrowserAnswer<DepotMeetingNoteCheck>.Success(new(false, null, null, 0)));

    public Task<DepotBrowserAnswer<DepotMeetingNoteCreated>> CreateAsync(
        string project, string topic, string? folder, string template, DateOnly date, IReadOnlyList<string> attendees) => Task.FromResult(
        DepotBrowserAnswer<DepotMeetingNoteCreated>.Success(new(DepotMeetingNoteName.Build(folder, topic, date), false)));

    public Task<DepotBrowserAnswer<string>> LinkAsync(string project, string path) => Task.FromResult(
        DepotBrowserAnswer<string>.Success("https://depot.example.com/n/token"));
}
