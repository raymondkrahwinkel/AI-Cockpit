using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Cockpit.Plugin.Proxmox.Engine;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using NSubstitute;

namespace Cockpit.Plugin.Proxmox.Tests;

// AC-1571: the settings view asks the backend part for a host's certificate, so a server's copy of the view reads it
// where the server connects from. The answer is the fingerprint of the certificate that host presents.
public sealed class ProxmoxFingerprintChannelTests
{
    [Fact]
    public async Task FetchFingerprint_ReadsTheCertificateTheHostPresents_InTheBackendPart()
    {
        using var certificate = _SelfSigned();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serve = _ServeOnceAsync(listener, certificate);
        var handlers = new Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>>();
        var channel = Substitute.For<IPluginBackendChannel>();
        channel.Handle(Arg.Any<string>(), Arg.Any<Func<JsonElement, CancellationToken, Task<JsonElement>>>())
            .Returns(call =>
            {
                handlers[call.ArgAt<string>(0)] = call.ArgAt<Func<JsonElement, CancellationToken, Task<JsonElement>>>(1);
                return Substitute.For<IDisposable>();
            });
        var host = Substitute.For<ICockpitHost>();
        host.Channel.Returns(channel);
        host.Storage.Returns(new FakePluginStorage());
        using var plugin = new ProxmoxPlugin();
        plugin.Initialize(host);

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // The wire format the view sends and reads, not the linked contract types (both assemblies carry a copy).
        var answer = await handlers["fetch-fingerprint"](JsonDocument.Parse($$"""{"host":"127.0.0.1","port":{{port}}}""").RootElement, CancellationToken.None);
        await serve;

        Assert.Equal(
            (ProxmoxCertificateProbe.Fingerprint(certificate), JsonValueKind.Null),
            (answer.GetProperty("fingerprint").GetString(), answer.GetProperty("error").ValueKind));
    }

    private static X509Certificate2 _SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=proxmox.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    private static async Task _ServeOnceAsync(TcpListener listener, X509Certificate2 certificate)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var tls = new SslStream(client.GetStream());
        await tls.AuthenticateAsServerAsync(certificate);
    }
}
