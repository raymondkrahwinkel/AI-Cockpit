extern alias UiAsm;

using DepotConnectionRegistration = UiAsm::Cockpit.Plugin.Depot.UI.DepotConnectionRegistration;
using DepotTranscriptLinks = UiAsm::Cockpit.Plugin.Depot.UI.DepotTranscriptLinks;

namespace Cockpit.Plugin.Depot.Tests;

public sealed class DepotTranscriptLinkTests
{
    [Fact]
    public void RecognizesOnlyLinkedDepotUrlsAndDepotPaths()
    {
        var connections = new[] { new DepotConnectionRegistration("one", "Linked", "https://linked.example.org") };

        var urls = connections.Select(connection => connection.Url).ToList();
        // AC-1955: an /artifact/ link is a Depot web page, which the system browser opens like any other link.
        Assert.False(DepotTranscriptLinks.TryMatch("https://linked.example.org/artifact/acme/cockpit/file-1", urls, out var urlStart, out var urlLength));
        Assert.Equal(0, urlStart);
        Assert.Equal(0, urlLength);
        Assert.True(DepotTranscriptLinks.TryMatch("https://linked.example.org/n/7Kq3xP", urls, out var noteStart, out var noteLength));
        Assert.Equal(0, noteStart);
        Assert.Equal("https://linked.example.org/n/7Kq3xP".Length, noteLength);
        Assert.True(DepotTranscriptLinks.TryMatch("acme/cockpit › docs/Readme.md", urls, out var pathStart, out var pathLength));
        Assert.Equal(0, pathStart);
        Assert.Equal("acme/cockpit › docs/Readme.md".Length, pathLength);
        Assert.False(DepotTranscriptLinks.TryMatch("Options › Sync", urls, out _, out _));
        const string text = "Zie krahwinkel-it/cockpit › Plan/x.md voor details";
        Assert.True(DepotTranscriptLinks.TryMatch(text, urls, out var textStart, out var textLength));
        Assert.Equal("krahwinkel-it/cockpit › Plan/x.md", text.Substring(textStart, textLength));
        Assert.False(DepotTranscriptLinks.TryMatch("https://depot.example.org/artifact/acme/cockpit/file-1", urls, out _, out _));
    }
}
