extern alias UiAsm;

using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.UI;
using NSubstitute;
using DepotBrowserModel = UiAsm::Cockpit.Plugin.Depot.UI.DepotBrowserModel;
using DepotBrowserNode = UiAsm::Cockpit.Plugin.Depot.Contracts.DepotBrowserNode;
using DepotBrowserStatus = UiAsm::Cockpit.Plugin.Depot.Contracts.DepotBrowserStatus;
using DepotConnectionRegistration = UiAsm::Cockpit.Plugin.Depot.UI.DepotConnectionRegistration;
using DepotTreeBuilder = UiAsm::Cockpit.Plugin.Depot.UI.DepotTreeBuilder;
using DepotTreeKind = UiAsm::Cockpit.Plugin.Depot.UI.DepotTreeKind;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1568: the UI half of the Depot file browser — what the model asks the backend over the channel, what it does
// with the answers.
public class DepotBrowserModelTests
{
    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    // The UI host with a channel that answers each action from `answers` and remembers what it was asked.
    internal sealed class Rig
    {
        public Dictionary<string, object> Answers { get; } = new();
        public List<(string Action, JsonElement Payload)> Invokes { get; } = [];
        public ICockpitUiHost Host { get; } = Substitute.For<ICockpitUiHost>();

        public Rig()
        {
            var channel = Substitute.For<IPluginUiChannel>();
            channel.InvokeAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Invokes.Add((call.ArgAt<string>(0), call.ArgAt<JsonElement>(1).Clone()));
                return Task.FromResult(JsonSerializer.SerializeToElement(Answers[call.ArgAt<string>(0)], JsonSerializerOptions.Web));
            });
            Host.Channel.Returns(channel);
        }

        public DepotBrowserModel Model(Func<HttpRequestMessage, HttpResponseMessage>? http = null) =>
            new(Host, new DepotConnectionRegistration("c1", "Work", "https://depot.example.com"),
                new HttpClient(new StubHandler(http ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)))));
    }

    internal static object _Ok(object? data) => new { status = "ok", message = (string?)null, data };

    private static DepotBrowserNode _Node(string path, string kind, string? contentType = null, string? shareId = null, bool isPublic = false) =>
        new(path, kind, 10, null, contentType, shareId, shareId is null ? null : "https://d/artifact/p/" + shareId, isPublic);

    [Fact]
    public async Task OpeningADocument_ReadsItAndHandsTheMarkdownToTheHostsRenderer()
    {
        var rig = new Rig();
        rig.Answers["browser-read"] = _Ok("# Notes\nbody");
        var rendered = new Border();
        rig.Host.CreateMarkdownView("# Notes\nbody").Returns(rendered);

        var answer = await rig.Model().RenderDocumentAsync("cockpit", _Node("docs/Notes.md", "document"));

        Assert.Same(rendered, answer.Data);
        Assert.Equal("docs/Notes.md", rig.Invokes.Single().Payload.GetProperty("path").GetString());

        // The same nodes nest by their slashes: folders first, the document under its folder.
        var tree = DepotTreeBuilder.Build([_Node("docs/Notes.md", "document"), _Node("img/a.png", "artifact"), _Node("top.md", "document")]);
        Assert.Equal(["docs", "img", "top.md"], tree.Select(item => item.Name));
        Assert.Equal(DepotTreeKind.Folder, tree[0].Kind);
        Assert.Equal("docs/Notes.md", tree[0].Children.Single().Path);
    }

    [Fact]
    public async Task Download_OffersTheLastPathComponentOnly_SoNothingLandsOutsideTheChosenFolder()
    {
        var rig = new Rig();
        rig.Answers["browser-download-url"] = _Ok("https://depot.example.com/blob/download?sig=secret");
        var root = Directory.CreateTempSubdirectory();
        var chosen = Directory.CreateDirectory(Path.Combine(root.FullName, "a", "b", "chosen"));
        try
        {
            string? offered = null;
            var answer = await rig.Model(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) })
                .DownloadAsync("cockpit", _Node("../../x.png", "artifact", "image/png"), name =>
                {
                    offered = name;
                    return Task.FromResult<Stream?>(File.Create(Path.Combine(chosen.FullName, name)));
                });

            Assert.True(answer.Data);
            Assert.Equal("x.png", offered);
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(chosen.FullName, "x.png")));
            Assert.DoesNotContain(Directory.GetFiles(root.FullName, "x.png", SearchOption.AllDirectories), file => Path.GetDirectoryName(file) != chosen.FullName);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Download_WithAnExpiredLink_ReportsWhyAndNeverPrintsTheUrlOrAsksWhereToSave()
    {
        var rig = new Rig();
        rig.Answers["browser-download-url"] = _Ok("https://depot.example.com/blob/download?sig=secret");
        var asked = false;

        var answer = await rig.Model(_ => new HttpResponseMessage(HttpStatusCode.Gone))
            .DownloadAsync("cockpit", _Node("img/a.png", "artifact", "image/png"), _ =>
            {
                asked = true;
                return Task.FromResult<Stream?>(null);
            });

        Assert.Equal(DepotBrowserStatus.Error, answer.Status);
        Assert.Contains("expired", answer.Message);
        Assert.DoesNotContain("secret", answer.Message);
        Assert.False(asked);
    }

    [Fact]
    public async Task Sharing_AsksBeforePublic_NotBeforeMembers_CopiesTheNewLink_AndUnshareAsksToo()
    {
        var rig = new Rig();
        rig.Answers["browser-share"] = _Ok(new { shareId = "s1", url = "https://d/artifact/p/s1", isPublic = false });
        rig.Answers["browser-unshare"] = _Ok(true);
        var model = rig.Model();
        var node = _Node("img/a.png", "artifact", "image/png");

        rig.Host.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var declined = await model.ShareAsync("cockpit", node, isPublic: true, copyLink: true);
        Assert.Null(declined);
        Assert.Empty(rig.Invokes);

        rig.Host.ClearReceivedCalls();
        var members = await model.ShareAsync("cockpit", node, isPublic: false, copyLink: true);
        Assert.Equal("members", rig.Invokes.Single().Payload.GetProperty("mode").GetString());
        await rig.Host.DidNotReceive().ConfirmAsync(Arg.Is<string>(title => title.Contains("public")), Arg.Any<string>(), Arg.Any<string>());
        await rig.Host.Received(1).SetClipboardTextAsync("https://d/artifact/p/s1");
        Assert.Equal("s1", members!.Data!.ShareId);

        rig.Host.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        await model.ShareAsync("cockpit", node, isPublic: true, copyLink: false);
        Assert.Equal("public", rig.Invokes[^1].Payload.GetProperty("mode").GetString());

        Assert.Null(await model.UnshareAsync("cockpit", node));
        var shared = _Node("img/a.png", "artifact", "image/png", shareId: "s1");
        await model.UnshareAsync("cockpit", shared);
        Assert.Equal(("browser-unshare", "s1"), (rig.Invokes[^1].Action, rig.Invokes[^1].Payload.GetProperty("shareId").GetString()));
    }
}
