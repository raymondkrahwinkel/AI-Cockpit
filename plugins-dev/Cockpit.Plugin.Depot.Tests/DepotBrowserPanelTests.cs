extern alias UiAsm;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using NSubstitute;
using DepotBrowserPanel = UiAsm::Cockpit.Plugin.Depot.UI.DepotBrowserPanel;
using DepotConnectionRegistration = UiAsm::Cockpit.Plugin.Depot.UI.DepotConnectionRegistration;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1568: one headless render of the file browser panel. Its own class, synchronous throughout, because the
// shared avalonia fixture is bound to one thread and an awaiting test migrates threads.
[Collection("avalonia")]
public class DepotBrowserPanelTests
{
    // The panel on fixture data, rendered headless: the tree beside an opened document. With DEPOT_BROWSER_SCENE_PNG
    // set to a path the frame is written there (AC-1235 style); the assertion is that something was drawn.
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
        rig.Answers["browser-read"] = DepotBrowserModelTests._Ok("# Notes");
        rig.Host.GetProjectMemoryRowsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Cockpit.Plugins.Abstractions.Projects.ProjectMemoryRow>>([]));
        rig.Host.CreateMarkdownView(Arg.Any<string>()).Returns(new TextBlock { Text = "Notes body" });
        Avalonia.Threading.AvaloniaSynchronizationContext.InstallIfNeeded();
        var panel = new DepotBrowserPanel(rig.Host, [new DepotConnectionRegistration("c1", "Work", "https://depot.example.com")]);
        var window = new Window { Width = 1000, Height = 640, Content = panel };
        window.Show();

        _Until(() => panel.GetVisualDescendants().OfType<TreeViewItem>().Any());
        _ = panel.LoadTreeAsync("docs/Notes.md");
        _Until(() => panel.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "Notes body"));

        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        if (Environment.GetEnvironmentVariable("DEPOT_BROWSER_SCENE_PNG") is { Length: > 0 } path)
        {
            using var stream = File.Create(path);
            frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        Assert.True(frame.PixelSize.Width > 0);
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
