using System.Net;
using System.Text;
using k8s;
using k8s.Autorest;
using Cockpit.Plugin.Kubernetes.Argo;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cockpit.Plugin.Kubernetes.Tests;

// AC-576 phase 3, AC 8 + 9: the token is attached to exactly one outgoing request and must never surface again —
// not in a returned body, and above all not in any of the error strings this client can produce. Every failure
// branch below is checked against the literal configured token, not just spot-checked on one of them.
public class ArgoApiClientTests
{
    private const string Token = "argocd-secret-token-value";
    private const string Namespace = "argocd";

    [Fact]
    public async Task GetAsync_Success_ParsesTheBody()
    {
        var client = _ClientReturning(HttpStatusCode.OK, """{"Version":"v3.3.2"}""");

        var (body, error) = await ArgoApiClient.GetAsync(client, Namespace, Token, "api/version", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("v3.3.2", body!["Version"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetAsync_BackendError_ReportsStatusOnly_NeverTheToken()
    {
        var client = _ClientReturning(HttpStatusCode.Unauthorized, string.Empty);

        var (body, error) = await ArgoApiClient.GetAsync(client, Namespace, Token, "api/v1/applications", CancellationToken.None);

        Assert.Null(body);
        Assert.Contains("401", error);
        Assert.DoesNotContain(Token, error);
    }

    [Fact]
    public async Task GetAsync_ApiserverRefusesTheProxyCall_ReportsStatusOnly_NeverTheToken()
    {
        var client = Substitute.For<IKubernetes>();
        var coreV1 = Substitute.For<ICoreV1Operations>();
        client.CoreV1.Returns(coreV1);
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        coreV1.ConnectGetNamespacedServiceProxyWithPathWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpOperationException($"Bearer {Token} rejected") { Response = new HttpResponseMessageWrapper(response, string.Empty) });

        var (body, error) = await ArgoApiClient.GetAsync(client, Namespace, Token, "api/v1/applications", CancellationToken.None);

        Assert.Null(body);
        Assert.Contains("403", error);
        Assert.DoesNotContain(Token, error);
    }

    [Fact]
    public async Task GetAsync_ApiserverRefusesTheProxyCall_NoResponseOnTheException_StillCleanAndNoToken()
    {
        var client = Substitute.For<IKubernetes>();
        var coreV1 = Substitute.For<ICoreV1Operations>();
        client.CoreV1.Returns(coreV1);
        coreV1.ConnectGetNamespacedServiceProxyWithPathWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpOperationException($"Bearer {Token} rejected"));

        var (body, error) = await ArgoApiClient.GetAsync(client, Namespace, Token, "api/v1/applications", CancellationToken.None);

        Assert.Null(body);
        Assert.NotNull(error);
        Assert.DoesNotContain(Token, error);
    }

    [Fact]
    public async Task GetAsync_TransportFailure_ReportsAGenericMessage_NeverTheTokenOrTheException()
    {
        var client = Substitute.For<IKubernetes>();
        var coreV1 = Substitute.For<ICoreV1Operations>();
        client.CoreV1.Returns(coreV1);
        coreV1.ConnectGetNamespacedServiceProxyWithPathWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException($"connect to https://argocd-server.internal with Bearer {Token} failed"));

        var (body, error) = await ArgoApiClient.GetAsync(client, Namespace, Token, "api/v1/applications", CancellationToken.None);

        Assert.Null(body);
        Assert.DoesNotContain(Token, error);
        Assert.DoesNotContain("argocd-server.internal", error);
    }

    [Fact]
    public async Task GetAsync_Cancelled_ReportsCancellation_NeverTheToken()
    {
        var client = Substitute.For<IKubernetes>();
        var coreV1 = Substitute.For<ICoreV1Operations>();
        client.CoreV1.Returns(coreV1);
        coreV1.ConnectGetNamespacedServiceProxyWithPathWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var (body, error) = await ArgoApiClient.GetAsync(client, Namespace, Token, "api/v1/applications", CancellationToken.None);

        Assert.Null(body);
        Assert.DoesNotContain(Token, error);
    }

    private static IKubernetes _ClientReturning(HttpStatusCode statusCode, string content)
    {
        var client = Substitute.For<IKubernetes>();
        var coreV1 = Substitute.For<ICoreV1Operations>();
        client.CoreV1.Returns(coreV1);

        var response = new HttpOperationResponse<Stream>
        {
            Body = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            Response = new HttpResponseMessage(statusCode),
        };
        coreV1.ConnectGetNamespacedServiceProxyWithPathWithHttpMessagesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, IReadOnlyList<string>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(response));

        return client;
    }
}
