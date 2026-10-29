using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Depot.UI;

// AC-1581, mockup 3c: the tab for a note Depot would not give this account. Its key is the connection and the link's token,
// so the tab can only ever show what it is built from: the connection's name, never a title, a path or any text.
internal static class DepotSharedNote
{
    public const string WorkspaceTypeId = "depot.shared-note";
    public const string TabTitle = "Shared note";

    public static WorkspaceTypeRegistration Registration(ICockpitUiHost host, Func<IReadOnlyList<DepotConnectionRegistration>> connections) =>
        new(WorkspaceTypeId, TabTitle, context => new DepotSharedNoteView(host, context.InstanceKey, connections()))
        {
            IconKind = MaterialIconKind.LockOutline,
        };

    // Opens or focuses the tab; the returned action brings it forward again.
    public static async Task<Func<Task>> OpenAsync(ICockpitUiHost host, DepotConnectionRegistration connection, string token)
    {
        var options = new WorkspaceOpenOptions(InstanceKey: Key(connection.Id, token), Title: TabTitle);
        await host.OpenWorkspaceAsync(WorkspaceTypeId, options).ConfigureAwait(true);
        return () => host.OpenWorkspaceAsync(WorkspaceTypeId, options);
    }

    public static string Key(string connectionId, string token) => $"{connectionId}|{token}";

    public static bool TryParseKey(string? key, out string connectionId, out string token)
    {
        var split = key?.LastIndexOf('|') ?? -1;
        connectionId = key is not null && split > 0 ? key[..split] : string.Empty;
        token = key is not null && split > 0 ? key[(split + 1)..] : string.Empty;
        return split > 0 && Contracts.DepotNoteToken.IsValid(token);
    }
}

internal sealed class DepotSharedNoteView : UserControl
{
    public DepotSharedNoteView(ICockpitUiHost? host, string? instanceKey, IReadOnlyList<DepotConnectionRegistration> connections)
    {
        DepotConnectionRegistration? connection = null;
        var known = DepotSharedNote.TryParseKey(instanceKey, out var connectionId, out var token);
        if (known)
        {
            connection = connections.FirstOrDefault(candidate => candidate.Id == connectionId);
        }

        var open = new Button { Content = "Open in Depot ↗", Classes = { "Accent" }, HorizontalAlignment = HorizontalAlignment.Center, IsEnabled = connection is not null };
        open.Click += async (_, _) =>
        {
            if (connection is not null)
            {
                await _OpenInDepotAsync(connection, token).ConfigureAwait(true);
            }
        };
        var icon = new MaterialIcon { Kind = MaterialIconKind.LockOutline, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center };
        icon.Bind(MaterialIcon.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        var body = new StackPanel
        {
            MaxWidth = 420,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                icon,
                new TextBlock { Text = "You can't open this note", FontSize = 15.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 6), HorizontalAlignment = HorizontalAlignment.Center },
                _Secondary("It is in a Depot project you are not a member of. Its owner can add you in Depot; until then Cockpit shows neither its title nor its text."),
                open,
            },
        };
        body.Children[2].Margin = new Thickness(0, 0, 0, 14);
        var location = _Secondary(connection is null ? "Depot" : $"Depot / {connection.Name}");
        location.HorizontalAlignment = HorizontalAlignment.Left;
        location.TextAlignment = TextAlignment.Left;
        location.Margin = new Thickness(12, 0);
        var strip = new Border { Height = 38, BorderThickness = new Thickness(0, 0, 0, 1), Child = location };
        strip.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CockpitHairlineSoftBrush"));
        strip.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitChromeBgBrush"));
        var area = new Border { Child = body };
        area.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitSecondaryBgBrush"));
        var layout = new DockPanel();
        DockPanel.SetDock(strip, Dock.Top);
        layout.Children.Add(strip);
        layout.Children.Add(area);
        Content = layout;
    }

    // The visible text of the tab, for the check that nothing of the note is in it.
    internal IEnumerable<string> Texts => this.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? string.Empty);

    private TextBlock _Secondary(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, LineHeight = 18.75 };
        block.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        return block;
    }

    private async Task _OpenInDepotAsync(DepotConnectionRegistration connection, string token)
    {
        try
        {
            if (Uri.TryCreate($"{connection.Url.TrimEnd('/')}/n/{token}", UriKind.Absolute, out var uri)
                && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                await window.Launcher.LaunchUriAsync(uri).ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning($"Depot: opening a shared note in the browser failed: {exception.Message}");
        }
    }
}
