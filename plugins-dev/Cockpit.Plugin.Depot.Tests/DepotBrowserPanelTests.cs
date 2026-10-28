extern alias UiAsm;

using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Material.Icons.Avalonia;
using NSubstitute;
using Cockpit.Plugins.Abstractions.UI.Files;
using DepotBrowserPanel = UiAsm::Cockpit.Plugin.Depot.UI.DepotBrowserPanel;
using DepotConnectionRegistration = UiAsm::Cockpit.Plugin.Depot.UI.DepotConnectionRegistration;
using NewMarkdownFileDialog = UiAsm::Cockpit.Plugin.Depot.UI.NewMarkdownFileDialog;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1568: one headless render of the file browser panel. Its own class, synchronous throughout, because the
// shared avalonia fixture is bound to one thread and an awaiting test migrates threads.
[Collection("avalonia")]
public class DepotBrowserPanelTests
{
    // The panel on fixture data, rendered headless: the full-width tree above a compact document detail. With
    // DEPOT_BROWSER_SCENE_PNG set to a path the frame is written there (AC-1235 style).
    [Fact]
    public void Panel_RendersTheTreeAndAnOpenedDocument()
    {
        var rig = new DepotBrowserModelTests.Rig();
        rig.Answers["browser-projects"] = DepotBrowserModelTests._Ok(new[] { new { project = "cockpit", name = "Cockpit", role = "Editor" } });
        rig.Answers["browser-tree"] = DepotBrowserModelTests._Ok(new
        {
            nodes = new object[]
            {
                new { path = "docs/Notes.md", kind = "document", size = 2048, modified = "2026-10-17T10:00:00+00:00", contentType = "text/markdown" },
                new { path = "img/mockup.png", kind = "artifact", size = 51200, modified = "2026-10-16T09:00:00+00:00", contentType = "image/png", shareId = "s1", shareUrl = "https://depot.example.com/artifact/cockpit/s1", isPublic = false },
            },
            canShare = true,
        });
        rig.Answers["browser-read"] = DepotBrowserModelTests._Ok("# Notes\n\n```csharp\nConsole.WriteLine(1);\n```\n\nThe first paragraph stays in the compact detail.\n## A following heading\nA second paragraph does not.");
        rig.Host.GetProjectMemoryRowsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Cockpit.Plugins.Abstractions.Projects.ProjectMemoryRow>>([]));
        var markdownView = new Border { Child = new TextBlock { Text = "Full markdown view" } };
        rig.Host.CreateMarkdownView(Arg.Any<string>()).Returns(markdownView);
        rig.Host.Storage.Get<string>("openFilesIn").Returns("below");
        Avalonia.Threading.AvaloniaSynchronizationContext.InstallIfNeeded();
        var panel = new DepotBrowserPanel(rig.Host, [new DepotConnectionRegistration("c1", "Work", "https://depot.example.com")]);
        var window = new Window { Width = 1000, Height = 640, Content = panel };
        window.Show();

        _Until(() => panel.GetVisualDescendants().OfType<TreeViewItem>().Any());
        var loading = panel.LoadTreeAsync("docs/Notes.md");
        _Until(() => loading.IsCompleted && panel.GetVisualDescendants().OfType<TextBlock>()
            .Any(block => block.Inlines?.OfType<Run>().Any(run => run.Text?.Contains("The first paragraph stays in the compact detail.", StringComparison.Ordinal) == true) == true));

        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        if (Environment.GetEnvironmentVariable("DEPOT_BROWSER_SCENE_PNG") is { Length: > 0 } path)
        {
            using var stream = File.Create(path);
            frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        Assert.True(frame.PixelSize.Width > 0);
        Assert.True(_UploadButton(panel).IsVisible);
        var selected = Assert.Single(panel.GetVisualDescendants().OfType<TreeViewItem>(), item => item.IsSelected);
        var rowHeader = Assert.IsType<StackPanel>(selected.Header);
        var rowName = Assert.Single(rowHeader.Children.OfType<TextBlock>(), block => block.Text == "Notes.md");
        var rowMetadata = Assert.Single(rowHeader.Children.OfType<TextBlock>(), block => block.Text?.Contains("2 KB", StringComparison.Ordinal) == true);
        Assert.Equal(13, rowName.FontSize);
        Assert.Equal(Avalonia.Media.FontWeight.Normal, rowName.FontWeight);
        Assert.Equal(11, rowMetadata.FontSize);
        Assert.Equal("#FF656C78", rowMetadata.Foreground?.ToString()?.ToUpperInvariant());
        var selectedLayout = Assert.Single(selected.GetVisualDescendants().OfType<Border>(), border => border.Name == "PART_LayoutRoot");
        Assert.Equal("#292563EB", selectedLayout.Background?.ToString()?.ToUpperInvariant());
        var detail = (_Button(panel, "Download") ?? throw new InvalidOperationException("The Download button was not rendered."))
            .GetVisualAncestors().OfType<StackPanel>().First(candidate => candidate.Parent is ScrollViewer);
        var detailHeading = Assert.IsType<StackPanel>(detail.Children[0]);
        Assert.Contains(detailHeading.Children.OfType<MaterialIcon>(), icon => icon.Width == 16 && icon.Height == 16);
        Assert.DoesNotContain(markdownView, panel.GetVisualDescendants());
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<TextBlock>().SelectMany(block => block.Inlines?.OfType<Run>() ?? []), run => run.Text?.Contains("Console.WriteLine", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<TextBlock>().SelectMany(block => block.Inlines?.OfType<Run>() ?? []), run => run.Text?.Contains("A following heading", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<TextBlock>().SelectMany(block => block.Inlines?.OfType<Run>() ?? []), run => run.Text?.Contains("A second paragraph does not.", StringComparison.Ordinal) == true);
        (_Button(panel, "Open") ?? throw new InvalidOperationException("Open is missing.")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _ = rig.Host.Received().OpenFileWorkspaceAsync("depot.markdown", Arg.Is<FileWorkspaceOpenOptions>(options =>
            options.File.Path == "docs/Notes.md" && options.InstanceKey == "depot:c1:cockpit:docs/Notes.md" && !options.Edit && !options.CreateNew));
        (_Button(panel, "✎ Edit") ?? throw new InvalidOperationException("Edit is missing.")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _ = rig.Host.Received().OpenFileWorkspaceAsync("depot.markdown", Arg.Is<FileWorkspaceOpenOptions>(options => options.Edit && !options.CreateNew));
        _ClickByStand(rig, selected);
        Assert.True((_Button(panel, "New Markdown file") ?? throw new InvalidOperationException("New file is missing.")).IsVisible);
        _NewFileValidation();
    }

    // AC-1573: the backend's reading of the role (`canShare`) decides whether the Upload button is there at all.
    [Fact]
    public void Panel_HidesUpload_ForAViewer()
    {
        var rig = new DepotBrowserModelTests.Rig();
        rig.Answers["browser-projects"] = DepotBrowserModelTests._Ok(new[] { new { project = "cockpit", name = "Cockpit", role = "Viewer" } });
        rig.Answers["browser-tree"] = DepotBrowserModelTests._Ok(new
        {
            nodes = new object[] { new { path = "docs/Notes.md", kind = "document", size = 2048, modified = "2026-10-17T10:00:00+00:00", contentType = "text/markdown" } },
            canShare = false,
        });
        rig.Host.GetProjectMemoryRowsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Cockpit.Plugins.Abstractions.Projects.ProjectMemoryRow>>([]));
        Avalonia.Threading.AvaloniaSynchronizationContext.InstallIfNeeded();
        var panel = new DepotBrowserPanel(rig.Host, [new DepotConnectionRegistration("c1", "Work", "https://depot.example.com")]);
        var window = new Window { Width = 1000, Height = 640, Content = panel };
        window.Show();

        _Until(() => panel.GetVisualDescendants().OfType<TreeViewItem>().Any());

        Assert.False(_UploadButton(panel).IsVisible);
        Assert.False((_Button(panel, "New Markdown file") ?? throw new InvalidOperationException("New file is missing.")).IsVisible);
        Assert.Null(_Button(panel, "✎ Edit"));
    }

    // AC-1781: a single click is the preview tab and a double-click a kept one; "Below the tree" only selects on a single click.
    private static void _ClickByStand(DepotBrowserModelTests.Rig rig, TreeViewItem row)
    {
        rig.Host.ClearReceivedCalls();
        rig.Host.Storage.Get<string>("openFilesIn").Returns("preview");
        _Tap(row, InputElement.TappedEvent);
        _ = rig.Host.Received().OpenFileWorkspaceAsync("depot.markdown", Arg.Is<FileWorkspaceOpenOptions>(options => options.Preview && !options.Edit));
        _Tap(row, InputElement.DoubleTappedEvent);
        _ = rig.Host.Received().OpenFileWorkspaceAsync("depot.markdown", Arg.Is<FileWorkspaceOpenOptions>(options => !options.Preview && !options.Edit));
        rig.Host.ClearReceivedCalls();
        rig.Host.Storage.Get<string>("openFilesIn").Returns("below");
        _Tap(row, InputElement.TappedEvent);
        _ = rig.Host.DidNotReceive().OpenFileWorkspaceAsync(Arg.Any<string>(), Arg.Any<FileWorkspaceOpenOptions>());
        _Tap(row, InputElement.DoubleTappedEvent);
        _ = rig.Host.Received().OpenFileWorkspaceAsync("depot.markdown", Arg.Is<FileWorkspaceOpenOptions>(options => !options.Preview));
    }

    private static void _Tap(TreeViewItem row, RoutedEvent<TappedEventArgs> gesture)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var released = new PointerEventArgs(InputElement.PointerReleasedEvent, row, pointer, null, default, 0, new PointerPointProperties(), KeyModifiers.None);
        row.RaiseEvent(new TappedEventArgs(gesture, released));
    }

    private static void _NewFileValidation()
    {
        string? opened = null;
        var dialog = new NewMarkdownFileDialog("Cockpit", "docs", ["", "docs"], path => path == "docs/taken.md", path =>
        {
            opened = path;
            return Task.CompletedTask;
        });
        var window = new Window { Width = 480, Height = 480, Content = dialog };
        window.Show();
        var name = dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "MarkdownFileName");
        var create = _Button(dialog, "Create") ?? throw new InvalidOperationException("Create is missing.");
        Assert.False(create.IsEnabled);
        name.Text = "../escape";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(create.IsEnabled);
        name.Text = ".md";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(create.IsEnabled);
        name.Text = "taken.md";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(create.IsEnabled);
        name.Text = "new-note";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(create.IsEnabled);
        create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("docs/new-note.md", opened);
        Assert.False(window.IsVisible);
    }

    [Fact]
    public void Panel_RefreshesTheSelectedArtifactsDetail_AfterSharingAndStoppingSharing()
    {
        var rig = new DepotBrowserModelTests.Rig();
        var shared = false;
        using var preview = new _HeldPreview();
        rig.Answer = (action, _) => action switch
        {
            "browser-projects" => DepotBrowserModelTests._Ok(new[] { new { project = "cockpit", name = "Cockpit", role = "Editor" } }),
            "browser-tree" => _Tree(shared),
            "browser-share" => DepotBrowserModelTests._Ok(new { shareId = "s1", url = "https://depot.example.com/artifact/cockpit/s1", isPublic = false }),
            "browser-unshare" => DepotBrowserModelTests._Ok(true),
            "browser-download-url" => DepotBrowserModelTests._Ok(preview.Url),
            _ => throw new InvalidOperationException($"Unexpected browser action: {action}"),
        };
        rig.Host.GetProjectMemoryRowsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Cockpit.Plugins.Abstractions.Projects.ProjectMemoryRow>>([]));
        rig.Host.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        Avalonia.Threading.AvaloniaSynchronizationContext.InstallIfNeeded();
        var panel = new DepotBrowserPanel(rig.Host, [new DepotConnectionRegistration("c1", "Work", "https://depot.example.com")]);
        var window = new Window { Width = 1000, Height = 640, Content = panel };
        window.Show();

        var loading = panel.LoadTreeAsync("img/mockup.txt");
        _Until(() => loading.IsCompleted && _Button(panel, "Create link") is not null);
        Assert.NotNull(_Button(panel, "Members only"));
        Assert.NotNull(_Button(panel, "Public"));

        // AC-1659: the reload must empty the detail before the rebuilt tree reselects the file. Headless, the reselection
        // only lands on the next layout pass, so the end state cannot tell the order; the moment the tree is replaced can.
        var detail = (_Button(panel, "Create link") ?? throw new InvalidOperationException("The Create link button was not rendered."))
            .GetVisualAncestors().OfType<StackPanel>().First(candidate => candidate.Parent is ScrollViewer);
        var staleAtRebuild = 0;
        panel.GetVisualDescendants().OfType<TreeView>().Single().PropertyChanged += (_, change) =>
        {
            if (change.Property == ItemsControl.ItemsSourceProperty && detail.Children.Count > 0)
            {
                staleAtRebuild++;
            }
        };

        shared = true;
        var create = _Button(panel, "Create link") ?? throw new InvalidOperationException("The Create link button was not rendered.");
        create.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _Until(() => _Button(panel, "Stop sharing") is not null);

        Assert.Contains(panel.GetVisualDescendants().OfType<TextBox>(), box => box.Text == "https://depot.example.com/artifact/cockpit/s1");
        Assert.Contains(panel.GetVisualDescendants().OfType<TreeViewItem>(), item => item.IsSelected);
        _SaveFrame(window, "DEPOT_BROWSER_SHARED_SCENE_PNG");

        shared = false;
        var stop = _Button(panel, "Stop sharing") ?? throw new InvalidOperationException("The Stop sharing button was not rendered.");
        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _Until(() => _Button(panel, "Create link") is not null);

        Assert.Contains(panel.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Not shared");
        Assert.Contains(panel.GetVisualDescendants().OfType<TreeViewItem>(), item => item.IsSelected);
        Assert.Equal(0, staleAtRebuild);
        _SaveFrame(window, "DEPOT_BROWSER_UNSHARED_SCENE_PNG");
    }

    // AC-1586: the Inbox tab (mockup 7b) on fixture data, as the side-menu button opens it. With DEPOT_INBOX_SCENE_PNG set
    // to a path the frame is written there; the assertion is that the connection's messages, and only those, are shown.
    [Fact]
    public void InboxTab_RendersTheConnectionsMessages()
    {
        var rig = new DepotBrowserModelTests.Rig();
        rig.Answers["browser-projects"] = DepotBrowserModelTests._Ok(Array.Empty<object>());
        rig.Answers["inbox-list"] = new
        {
            count = new { connectionId = "c1", state = "ok", unread = 3, perOrganization = new Dictionary<string, int> { ["krahwinkel-it"] = 2, ["wispslate"] = 1 } },
            messages = new object[]
            {
                _Message("m1", "NoteInvitation", "Sanne invites you to a note", false, "krahwinkel-it", "Krahwinkel IT", -1, new { state = "note", title = "Sprint review", path = "Meetings/2026-10-19 Sprint review.md", live = true, openUrl = "https://depot.example.com/n/t1" }),
                _Message("m2", "AccessRequest", "Lotte asks for access to depot", false, "krahwinkel-it", "Krahwinkel IT", -45, null),
                _Message("m3", "NoteInvitation", "Mark invites you to a note", false, "wispslate", "Wispslate", -90, new { state = "note", title = "Roadmap Q4", path = "Plans/Roadmap.md", live = false, openUrl = "https://depot.example.com/n/t2" }),
                _Message("m4", "OrganizationInvitation", "Added to krahwinkel-it/klanten", true, "krahwinkel-it", "Krahwinkel IT", -1500, null),
            },
            updatedAt = DateTimeOffset.UtcNow,
        };
        rig.Host.GetProjectMemoryRowsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Cockpit.Plugins.Abstractions.Projects.ProjectMemoryRow>>([]));
        Avalonia.Threading.AvaloniaSynchronizationContext.InstallIfNeeded();
        var panel = new DepotBrowserPanel(rig.Host, [new DepotConnectionRegistration("c1", "Work", "https://depot.example.com")], openInbox: true);
        var window = new Window { Width = 420, Height = 640, Content = panel };
        window.Show();

        _Until(() => panel.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Lotte asks for access to depot"));

        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        if (Environment.GetEnvironmentVariable("DEPOT_INBOX_SCENE_PNG") is { Length: > 0 } path)
        {
            using var stream = File.Create(path);
            frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        Assert.True(frame.PixelSize.Width > 0);
        Assert.Contains(panel.GetVisualDescendants().OfType<ToggleButton>(), chip => Equals(chip.Content, "Krahwinkel IT · 2"));
    }

    private static object _Message(string id, string kind, string summary, bool isRead, string slug, string organization, int minutesAgo, object? note) => new
    {
        id,
        kind,
        isRead,
        handled = false,
        createdAt = DateTimeOffset.Now.AddMinutes(minutesAgo),
        organization = new { slug, name = organization },
        summary,
        openUrl = $"https://depot.example.com/mailbox?id={id}",
        note,
    };

    private static Button _UploadButton(DepotBrowserPanel panel) =>
        panel.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Upload"));

    private static object _Tree(bool shared) => DepotBrowserModelTests._Ok(new
    {
        nodes = new object[]
        {
            new { path = "img/mockup.txt", kind = "artifact", size = 51200, modified = "2026-10-16T09:00:00+00:00", contentType = "text/plain", shareId = shared ? "s1" : null, shareUrl = shared ? "https://depot.example.com/artifact/cockpit/s1" : null, isPublic = false },
        },
        canShare = true,
    });

    private static Button? _Button(Control panel, string content) =>
        panel.GetVisualDescendants().OfType<Button>().SingleOrDefault(button => Equals(button.Content, content));

    private static void _SaveFrame(Window window, string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path)
        {
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
            using var stream = File.Create(path);
            frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    private sealed class _HeldPreview : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public _HeldPreview()
        {
            _listener.Start();
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/preview";

        public void Dispose()
        {
            _listener.Stop();
        }
    }

    // Synchronous on purpose: the panel's awaits come back through the dispatcher, which only runs while this polls.
    private static void _Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        Assert.True(condition(), "The panel did not reach the expected state.");
    }
}
