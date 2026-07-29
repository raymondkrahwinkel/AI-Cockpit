using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.TestSupport;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// AC-128: attach_message_images_to_issue keys on the transport-verified caller pane, not the agent-declared
/// <c>session</c>, so an agent cannot read another session's current-turn images by naming its id (confused deputy)
/// and upload them to an issue.
/// </summary>
public class YouTrackAttachToolsTests
{
    [Fact]
    public async Task AttachMessageImages_KeysOnTheVerifiedCallerPane_NotTheAgentSuppliedSession()
    {
        var sessions = Substitute.For<ICockpitSessionObserver>();
        sessions.GetCurrentTurnImages(Arg.Any<string>()).Returns([]); // no images -> the tool returns before uploading
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns("verified-pane");
        host.Sessions.Returns(sessions);
        var tools = new YouTrackAttachTools(host, new YouTrackSettings(Substitute.For<IPluginStorage>()));

        // The agent spoofs another session's id in the tool argument.
        await tools.AttachMessageImagesToIssue("AC-1", session: "victim-pane");

        // The images are read for the verified caller, never the spoofed id.
        sessions.Received().GetCurrentTurnImages("verified-pane");
        sessions.DidNotReceive().GetCurrentTurnImages("victim-pane");
    }

    [Fact]
    public async Task AttachMessageImages_OmittingPath_IsByteForByteTheOriginalNoImagesMessage()
    {
        var host = new FakeCockpitHost();
        var tools = new YouTrackAttachTools(host, new YouTrackSettings(new InMemoryPluginStorage()));

        var result = await tools.AttachMessageImagesToIssue("AC-1", session: "pane-1");

        Assert.Equal("The current message carried no images to attach.", result);
    }
}

/// <summary>
/// AC-170: the path argument is a genuine outbound channel, so it is checked against an explicit allow-list (the
/// terminal-paste folder and the calling session's own working directory), by canonical containment rather than a
/// raw-string prefix, and its content — never its extension — decides whether it is really an image.
/// </summary>
public class YouTrackAttachToolsPathTests : IAsyncLifetime
{
    // Real PNG signature bytes (see ImageContentSniffer) — enough for the sniffer, does not need to be a decodable image.
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    private LoopbackHttpServer? _server;
    private string _prefix = string.Empty;
    private int _requests;

    public async Task InitializeAsync()
    {
        _server = await LoopbackHttpServer.StartAsync(context =>
        {
            Interlocked.Increment(ref _requests);
            return context.Response.WriteAsync("""{ "id": "1" }""");
        });
        _prefix = _server.BaseUrl;
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Path_UnderThePasteDirectory_Attaches()
    {
        var (host, tools) = _MakeTools(workingDirectory: null);
        var pasteDir = Path.Combine(Path.GetTempPath(), "exclr8-terminal-paste");
        Directory.CreateDirectory(pasteDir);
        var file = Path.Combine(pasteDir, $"paste-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(file, PngBytes);
        try
        {
            var result = await tools.AttachMessageImagesToIssue("AC-1", session: "pane-1", path: file);

            Assert.Equal("Attached 1 image to AC-1.", result);
            Assert.Equal(1, _requests);
            Assert.Empty(host.Toasts);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Path_UnderTheSessionWorkingDirectory_Attaches()
    {
        var workingDirectory = Directory.CreateTempSubdirectory("ac170-workdir-").FullName;
        var (host, tools) = _MakeTools(workingDirectory);
        var file = Path.Combine(workingDirectory, "screenshot.png");
        await File.WriteAllBytesAsync(file, PngBytes);
        try
        {
            var result = await tools.AttachMessageImagesToIssue("AC-1", session: "pane-1", path: file);

            Assert.Equal("Attached 1 image to AC-1.", result);
            Assert.Equal(1, _requests);
            Assert.Empty(host.Toasts);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Path_OutsideBothAllowedRoots_IsRejected()
    {
        var workingDirectory = Directory.CreateTempSubdirectory("ac170-workdir-").FullName;
        var (_, tools) = _MakeTools(workingDirectory);
        var outsideDir = Directory.CreateTempSubdirectory("ac170-outside-").FullName;
        var file = Path.Combine(outsideDir, "screenshot.png");
        await File.WriteAllBytesAsync(file, PngBytes);
        try
        {
            var result = await tools.AttachMessageImagesToIssue("AC-1", session: "pane-1", path: file);

            Assert.Contains("outside the folders this tool may attach from", result);
            Assert.Equal(0, _requests);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public async Task Path_EscapingViaDotDot_IsRejectedEvenThoughTheRawStringStartsWithAnAllowedRoot()
    {
        var workingDirectory = Directory.CreateTempSubdirectory("ac170-workdir-").FullName;
        var (_, tools) = _MakeTools(workingDirectory);

        // A sibling of the working directory, reached from inside it via "..": the raw string is prefixed by the
        // allowed root, but the canonical path it resolves to is not — this must be caught on the resolved path.
        var siblingDir = Directory.CreateTempSubdirectory("ac170-sibling-").FullName;
        var file = Path.Combine(siblingDir, "escaped.png");
        await File.WriteAllBytesAsync(file, PngBytes);
        var escapingPath = Path.Combine(workingDirectory, "..", Path.GetFileName(siblingDir), "escaped.png");
        try
        {
            var result = await tools.AttachMessageImagesToIssue("AC-1", session: "pane-1", path: escapingPath);

            Assert.Contains("outside the folders this tool may attach from", result);
            Assert.Equal(0, _requests);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
            Directory.Delete(siblingDir, recursive: true);
        }
    }

    [Fact]
    public async Task Path_NonImageContentWithAnImageExtension_IsRejected()
    {
        var workingDirectory = Directory.CreateTempSubdirectory("ac170-workdir-").FullName;
        var (_, tools) = _MakeTools(workingDirectory);
        var file = Path.Combine(workingDirectory, "not-really-an-image.png");
        await File.WriteAllTextAsync(file, "this is plain text, not an image, despite the extension");
        try
        {
            var result = await tools.AttachMessageImagesToIssue("AC-1", session: "pane-1", path: file);

            Assert.Contains("not a recognized image file", result);
            Assert.Equal(0, _requests);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private (FakeCockpitHost Host, YouTrackAttachTools Tools) _MakeTools(string? workingDirectory)
    {
        var host = new FakeCockpitHost();
        host.Observer.ActiveSessionWorkingDirectory = workingDirectory;
        var settings = new YouTrackSettings(new InMemoryPluginStorage())
        {
            Instances = [new YouTrackInstance("Personal", $"{_prefix}api", "token", "AC")],
        };
        return (host, new YouTrackAttachTools(host, settings));
    }
}
