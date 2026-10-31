extern alias UiAsm;

using Avalonia.Controls;
using Avalonia.VisualTree;
using NSubstitute;
using ArtifactWorkspace = UiAsm::Cockpit.Plugin.Depot.UI.ArtifactWorkspace;
using DepotBrowserKind = UiAsm::Cockpit.Plugin.Depot.Contracts.DepotBrowserKind;
using DepotBrowserNode = UiAsm::Cockpit.Plugin.Depot.Contracts.DepotBrowserNode;

namespace Cockpit.Plugin.Depot.Tests;

[Collection("avalonia")]
public class ArtifactWorkspaceTests
{
    [Fact]
    public void Viewer_RendersImagesTextAndTooLargeArtifacts()
    {
        var host = Substitute.For<Cockpit.Plugins.Abstractions.UI.ICockpitUiHost>();
        host.CreateMarkdownEditor(Arg.Any<string>()).Returns((Cockpit.Plugins.Abstractions.UI.IMarkdownEditor?)null);
        var image = new ArtifactWorkspace(host, new DepotBrowserNode("mockup.gif", DepotBrowserKind.Artifact, 43, null, "image/gif"), _ => Task.FromResult<byte[]?>(_Gif));
        var text = new ArtifactWorkspace(host, new DepotBrowserNode("notes.txt", DepotBrowserKind.Artifact, 5, null, "text/plain"), _ => Task.FromResult<byte[]?>("hello"u8.ToArray()));
        var large = new ArtifactWorkspace(host, new DepotBrowserNode("large.txt", DepotBrowserKind.Artifact, ArtifactWorkspace.MaxInlineBytes + 1, null, "text/plain"), _ => throw new InvalidOperationException("Too-large files must not load."));

        _Show(image);
        _Show(text);
        _Show(large);

        Assert.Contains(image.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "1 x 1 px");
        Assert.Contains(text.GetVisualDescendants().OfType<TextBox>(), block => block.Text == "hello");
        Assert.Contains(large.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains("2 MiB", StringComparison.Ordinal) == true);
        Assert.Contains(large.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Download"));
    }

    private static void _Show(Control control)
    {
        var window = new Window { Width = 900, Height = 600, Content = control };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static readonly byte[] _Gif = Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
}
