using System.Net;
using System.Text;
using Cockpit.Core.ModelIntelligence;
using Cockpit.Infrastructure.ModelIntelligence;

namespace Cockpit.Infrastructure.Tests.ModelIntelligence;

public sealed class OpenRouterModelIntelligenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetSnapshotsAsync_PreservesPublicFactsAndCacheGuarantees()
    {
        var fetchedAt = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var first = new OpenRouterModelIntelligence(_root, new _Handler(_Fixture), new _Clock(fetchedAt));
        var fetched = await first.GetSnapshotsAsync();

        var fallback = new OpenRouterModelIntelligence(_root, new _Handler(() => throw new HttpRequestException()), new _Clock(fetchedAt.AddHours(25)));
        var cached = await fallback.GetSnapshotsAsync();

        var snapshot = Assert.Single(cached);
        Assert.Equal(fetchedAt, snapshot.FetchedAt);
        Assert.Equal(fetched[0].Price, snapshot.Price);
        Assert.Equal(57.6m, snapshot.Indices.Intelligence);
        Assert.Null(snapshot.Indices.Coding);
        Assert.Single(snapshot.PromptPriceTiers);
        var window = new PriceWindow([DayOfWeek.Friday], new TimeOnly(16, 0), TimeOnly.MinValue, new ModelPrice(0.5m, 1m));

        Assert.True(window.IsActiveAt(new DateTimeOffset(2026, 10, 23, 23, 30, 0, TimeSpan.Zero)));
        Assert.False(window.IsActiveAt(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero)));
        Assert.False(window.IsActiveAt(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero)));
        var root = Path.Combine(_root, "injected-root");
        var service = new OpenRouterModelIntelligence(root, new _Handler(_Fixture));

        await service.GetSnapshotsAsync();

        Assert.True(File.Exists(Path.Combine(root, "model-intelligence", "openrouter-models.json")));
        Assert.Empty(Directory.GetFiles(_root, "openrouter-models.json", SearchOption.TopDirectoryOnly));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string _Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ModelIntelligence", "Fixtures", "openrouter-models.json"));

    private sealed class _Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class _Handler(Func<string> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content(), Encoding.UTF8, "application/json") });
    }
}
