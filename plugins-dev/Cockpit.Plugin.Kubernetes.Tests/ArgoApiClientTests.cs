using System.Net;
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

}
