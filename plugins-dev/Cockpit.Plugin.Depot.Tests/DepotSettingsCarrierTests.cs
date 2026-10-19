using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Cockpit.Plugin.Depot.Backup;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

// AC-1510: the settings carrier speaks Depot's /cockpit/profile (DEP-168): a write names what it replaces, and one that
// names a stale version is refused rather than written over another machine's.
public sealed class DepotSettingsCarrierTests
{
    [Fact]
    public async Task A_write_lands_only_over_the_version_it_names()
    {
        var space = new _FakeProfile();
        var connection = new DepotConnectionRegistration("c1", "Home", "http://depot.test/");
        var carrier = new DepotSettingsCarrier(Substitute.For<ICockpitHost>(), () => [connection], connection.Id, () => space).Registration(connection);
        byte[] first = Encoding.UTF8.GetBytes("""{"Layout":{"ThemeMode":"Light"}}"""), second = Encoding.UTF8.GetBytes("""{"Layout":{"ThemeMode":"Dark"}}""");
        var write = carrier.WriteIfUnchangedAsync ?? throw new InvalidOperationException("The Depot carrier has no conditional write.");

        Assert.Null(await carrier.ReadAsync(CancellationToken.None));
        Assert.True(await write(first, null, CancellationToken.None));
        Assert.False(await write(second, null, CancellationToken.None));
        Assert.True(await write(second, _Version(first), CancellationToken.None));
        Assert.False(await write(first, _Version(first), CancellationToken.None));
        Assert.Equal(second, await carrier.ReadAsync(CancellationToken.None));

        space.SignedOut = true;
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => carrier.ReadAsync(CancellationToken.None));
        Assert.Contains("sign-in needed", refused.Message, StringComparison.Ordinal);
    }

    private static string _Version(byte[] blob) => Convert.ToHexStringLower(SHA256.HashData(blob));

    // Depot's /cockpit/profile as CockpitEndpoints answers it: 204 when empty, 428 without a precondition, 412 when stale.
    private sealed class _FakeProfile : HttpMessageHandler
    {
        private byte[]? _stored;

        public bool SignedOut { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (SignedOut)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            Assert.Equal("/cockpit/profile", request.RequestUri?.AbsolutePath);
            if (request.Method == HttpMethod.Get)
            {
                return _stored is null ? new HttpResponseMessage(HttpStatusCode.NoContent) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_stored) };
            }

            var expected = request.Headers.IfMatch.FirstOrDefault()?.Tag.Trim('"');
            var createOnly = request.Headers.IfNoneMatch.Contains(EntityTagHeaderValue.Any);
            if (expected is null && !createOnly)
            {
                return new HttpResponseMessage(HttpStatusCode.PreconditionRequired);
            }

            var current = _stored is null ? null : _Version(_stored);
            if (createOnly ? current is not null : current != expected)
            {
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            }

            _stored = await (request.Content ?? throw new InvalidOperationException("No body.")).ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
