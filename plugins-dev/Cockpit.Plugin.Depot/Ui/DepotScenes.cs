using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Depot.Contracts;

namespace Cockpit.Plugin.Depot.UI;

// Fixed, network-free models for `--screenshot --scene plugin:<name>`. They drive the real browser panel.
internal static class DepotScenes
{
    private static readonly DepotBrowserProject _Project = new("krahwinkel-it/cockpit", "krahwinkel-it/cockpit", "Editor");
    private static readonly DateTimeOffset _Modified = new(2026, 10, 24, 16, 12, 0, TimeSpan.Zero);

    public static IReadOnlyDictionary<string, Func<Control>> All { get; } = new Dictionary<string, Func<Control>>
    {
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

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}

internal sealed record DepotBrowserSceneModel(
    DepotBrowserProject Project,
    DepotBrowserTree Tree,
    string? SelectedPath,
    DepotDocumentExcerpt? Excerpt);
