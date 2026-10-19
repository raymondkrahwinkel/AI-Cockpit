using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Cockpit.Plugin.Depot.Backup;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1508: a backup in a Depot account space comes back byte for byte, and one Depot did not keep whole is never a backup.
public sealed class DepotBackupTargetTests
{
    [Theory]
    [InlineData("whole", null)]
    [InlineData("corrupted", "did not arrive whole")]
    [InlineData("too-large", "larger than this Depot accepts (10485760 bytes)")]
    [InlineData("signed-out", "sign-in needed")]
    public async Task A_backup_comes_back_byte_for_byte_or_is_refused_with_nothing_left_listed(string depot, string? refusal)
    {
        var space = new _FakeAccountSpace(depot);
        var connection = new DepotConnectionRegistration("c1", "Home", "http://depot.test/");
        var target = new DepotBackupTarget(Substitute.For<ICockpitHost>(), () => [connection], connection.Id, () => space);
        var archive = RandomNumberGenerator.GetBytes(4096);

        if (refusal is null)
        {
            // The host reads its stream again after handing it over, so the target may not have closed it.
            using var handed = new MemoryStream(archive);
            var id = await target.WriteAsync("cockpit-backup.zip", handed);
            Assert.True(handed.CanRead);

            var entry = Assert.Single(await target.ListAsync());
            Assert.Equal((id, archive.LongLength), (entry.Id, entry.Size));
            await using var back = await target.OpenReadAsync(id);
            using var read = new MemoryStream();
            await back.CopyToAsync(read);
            Assert.Equal(archive, read.ToArray());
            return;
        }

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => target.WriteAsync("cockpit-backup.zip", new MemoryStream(archive)));
        Assert.Contains(refusal, refused.Message, StringComparison.Ordinal);
        Assert.Empty(space.Stored);
    }

    // Depot's /cockpit/backups (DEP-168) as far as the target uses it; `depot` says how this one misbehaves.
    private sealed class _FakeAccountSpace(string depot) : HttpMessageHandler
    {
        public Dictionary<Guid, byte[]> Stored { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (depot == "signed-out")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var id = Guid.TryParse(path[(path.LastIndexOf('/') + 1)..], out var parsed) ? parsed : (Guid?)null;
            if (request.Method == HttpMethod.Post)
            {
                if (depot == "too-large")
                {
                    return new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
                    {
                        Content = JsonContent.Create(new { detail = "The backup is larger than this Depot accepts (10485760 bytes)." }),
                    };
                }

                var data = await (request.Content ?? throw new InvalidOperationException("No body.")).ReadAsByteArrayAsync(cancellationToken);
                if (depot == "corrupted")
                {
                    data[0] ^= 0xFF;
                }

                var added = Guid.NewGuid();
                Stored[added] = data;
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(_Info(added, data)) };
            }

            if (request.Method == HttpMethod.Delete && id is { } gone)
            {
                return new HttpResponseMessage(Stored.Remove(gone) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
            }

            if (id is { } wanted)
            {
                return Stored.TryGetValue(wanted, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Stored.Select(pair => _Info(pair.Key, pair.Value))) };
        }

        private static object _Info(Guid id, byte[] data) =>
            new { id, size = data.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(data)), createdAt = DateTime.UtcNow };
    }
}
