using System.Net;
using System.Text;
using Cockpit.Plugin.RemoteFiles.Contracts;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.RemoteFiles.Tests;

// Category D: real DAV response shapes and conditional requests are an external protocol contract.
public sealed class WebDavTests
{
    [Theory]
    [InlineData("nextcloud.xml")]
    [InlineData("apache.xml")]
    public async Task Dav_listing_and_conditional_put_follow_the_server_contract(string fixture)
    {
        using var handler = new DavHandler(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)));
        var connection = new RemoteConnection("dav", "DAV", RemoteFilesChannel.WebDav, "", 443, "tester", ReadOnly: false, DavUrl: "https://dav.example/dav/");
        var source = new RemoteFileSource(true, _ => Task.FromResult<IRemoteSession>(new WebDavSession(connection, new RemoteSecrets("sentinel-secret"), new RemoteTrust(null), handler)));
        var listing = await source.ListAsync(FileRef.Root("dav"), null, default);
        var entry = Assert.Single(listing.Entries);
        Assert.Equal("hello world.md", entry.Name);
        Assert.Equal("\"original\"", entry.Version);
        var file = new FileRef("dav", entry.Id, entry.Path);
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("edited"));
        Assert.Equal(FileWriteStatus.Written, (await source.WriteAsync(file, bytes, entry.Version, false, default)).Status);
        bytes.Position = 0;
        handler.Conflict = true;
        Assert.Equal(FileWriteStatus.Conflict, (await source.WriteAsync(file, bytes, entry.Version, false, default)).Status);
        handler.Exists = false;
        bytes.Position = 0;
        Assert.Equal(FileWriteStatus.Conflict, (await source.WriteAsync(file, bytes, null, true, default)).Status);
        handler.Unauthorized = true;
        var error = await Assert.ThrowsAsync<FileSourceException>(() => source.ListAsync(FileRef.Root("dav"), null, default));
        Assert.Equal(FileSourceErrorKind.SignIn, error.Kind);
        Assert.DoesNotContain("sentinel", error.ToString());
        Assert.Equal(3, handler.Puts);
    }

    private sealed class DavHandler(string xml) : HttpMessageHandler
    {
        public bool Conflict { get; set; }
        public bool Exists { get; set; } = true;
        public bool Unauthorized { get; set; }
        public int Puts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unauthorized)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("sentinel-secret") });
            }
            if (request.Method == HttpMethod.Put)
            {
                Assert.Equal(Exists ? "\"original\"" : null, request.Headers.IfMatch.SingleOrDefault()?.ToString());
                Assert.Equal(Exists ? null : "*", request.Headers.IfNoneMatch.SingleOrDefault()?.ToString());
                Puts++;
                var response = new HttpResponseMessage(Conflict ? HttpStatusCode.PreconditionFailed : HttpStatusCode.NoContent);
                response.Headers.ETag = new("\"updated\"");
                return Task.FromResult(response);
            }
            Assert.Equal("PROPFIND", request.Method.Method);
            return Task.FromResult(new HttpResponseMessage(!Exists && request.Headers.GetValues("Depth").Single() == "0" ? HttpStatusCode.NotFound : HttpStatusCode.MultiStatus)
                { Content = new StringContent(xml) });
        }

        protected override void Dispose(bool disposing)
        {
            // Each source call closes its client; the fake server spans the whole contract exchange.
        }
    }
}
