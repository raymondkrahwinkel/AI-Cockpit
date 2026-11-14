using Cockpit.Plugin.Matomo.Contracts;
using Cockpit.Plugin.Matomo.Mcp;
using Cockpit.Plugin.Matomo.Settings;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Matomo.Tests;

public sealed class MatomoReadOnlyTests
{
    private const string Token = "s3cr3t-view-token";

    [Fact]
    public async Task EveryTool_OnlyReads_NeverLeaksTheToken_AndRefusesSitesThatAreNotOffered()
    {
        IPluginStorage storage = new MemoryStorage();
        storage.Set(MatomoKeys.Url, "https://matomo.test");
        storage.SetSecret(MatomoKeys.AuthToken, Token);
        storage.Set(MatomoKeys.OfferedSites, new List<MatomoSite> { new(1, "Shop") });
        storage.Set(MatomoKeys.DefaultSite, 1);
        var handler = new RecordingHandler();
        var tools = new MatomoMcpTools(new MatomoSettings(storage), new MatomoClient(new HttpClient(handler)));

        var answers = new List<string>
        {
            await tools.ListSites(),
            await tools.ListFields(),
            await tools.RunReport("2026-11-01", "today", ["Actions_getPageUrls"], ["nb_visits"], "nb_visits", 5),
            await tools.Top("7daysAgo", "yesterday", "pages"),
            await tools.Realtime(10),
        };
        var requestsForOfferedSite = handler.Requests.Count;
        answers.Add(await tools.ListFields("2"));
        answers.Add(await tools.RunReport("2026-11-01", "today", ["Actions_getPageUrls"], site: "2"));

        Assert.Equal(requestsForOfferedSite, handler.Requests.Count);
        Assert.Equal(5, requestsForOfferedSite);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/index.php", request.Path);
            Assert.DoesNotContain(Token, request.Query);
            Assert.Contains(Token, request.Body);
            Assert.Contains(request.Form["method"], MatomoClient.AllowedMethods);
        });
        Assert.All(answers, answer => Assert.DoesNotContain(Token, answer));
        Assert.Contains("site_not_offered", answers[^1]);
        Assert.Contains("\"ok\":true", answers[2]);
    }

    private sealed record Seen(HttpMethod Method, string Path, string Query, string Body, Dictionary<string, string> Form);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = body.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
            Requests.Add(new Seen(request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, body, form));
            var json = form["method"] switch
            {
                "SitesManager.getSitesWithAtLeastViewAccess" => """[{"idsite":"1","name":"Shop","main_url":"https://shop.test"},{"idsite":"2","name":"Other"}]""",
                "API.getReportMetadata" => """[{"uniqueId":"Actions_getPageUrls","name":"Page URLs","category":"Behaviour","dimension":"Page URL","metrics":{"nb_visits":"Visits"}}]""",
                "API.getProcessedReport" => """{"reportData":[{"label":"/","nb_visits":3},{"label":"/cart","nb_visits":1}]}""",
                _ => """[{"visits":2,"visitors":2,"actions":5,"visitsConverted":0}]""",
            };
            return new HttpResponseMessage { Content = new StringContent(json) };
        }
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) && value is T typed ? typed : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }
}
