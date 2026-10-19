using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Layout;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Plugins;
using Cockpit.TestSupport;

namespace Cockpit.Journeys;

// AC-1510: two machines, each a state root of its own, through the bundled Depot plugin against a fake account space.
// A's theme reaches B at its next start, and nothing the white list does not carry comes along (acceptance 1); a change
// made while Depot cannot be reached is said once and arrives when it can again (acceptance 3).
[Collection(JourneyCollection.Alone)]
public sealed class SettingsSyncJourney
{
    [Fact]
    public async Task A_theme_set_on_one_machine_reaches_the_other_and_an_offline_change_arrives_once_back_online()
    {
        var space = new _ProfileSpace();
        await using var depot = await LoopbackHttpServer.StartAsync(space.HandleAsync);
        var url = depot.BaseUrl.TrimEnd('/');
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"journey-settings-sync-{Guid.NewGuid():N}")).FullName;
        try
        {
            await using (var a = JourneyHost.Desktop(stateRoot: Directory.CreateDirectory(Path.Combine(work, "a")).FullName))
            {
                await _SignedInAsync(a, url);
                var layout = a.Services.GetRequiredService<ILayoutSettingsStore>();
                await layout.SaveAsync(await layout.LoadAsync() with { ThemeMode = ThemeMode.Light });
                await a.Services.GetRequiredService<SettingsCarrierSync>().RunOnceAsync();
            }

            Assert.Equal("Light", space.Document?["Layout"]?["ThemeMode"]?.GetValue<string>());

            // The other side of the white list: what this build does not carry is never taken from the carrier.
            var document = space.Document ?? throw new InvalidOperationException("A pushed nothing.");
            document["SomeNewSection"] = new JsonObject { ["Token"] = "journey-secret" };
            document["Layout"]?.AsObject().Add("SidebarWidth", 999);
            space.Document = document;

            var b = Directory.CreateDirectory(Path.Combine(work, "b")).FullName;
            await using (var first = JourneyHost.Desktop(stateRoot: b))
            {
                await _SignedInAsync(first, url);
                await first.Services.GetRequiredService<SettingsCarrierSync>().RunOnceAsync();
                await HeadlessAvalonia.RunAsync(() => Until.CollectionHolds(first.Cockpit.Toasts, () =>
                    first.Cockpit.Toasts.Any(toast => toast.Message == "1 setting from your other machine take effect at the next start.")));
            }

            await using var restarted = JourneyHost.Desktop(stateRoot: b);
            await restarted.StartDesktopAsync();
            var settings = JsonNode.Parse(await File.ReadAllTextAsync(CockpitConfigPath.For(b)));
            Assert.Equal("Light", settings?["Layout"]?["ThemeMode"]?.GetValue<string>());
            await HeadlessAvalonia.RunAsync(() => Until.Holds(restarted.Cockpit, () => restarted.Cockpit.ThemeMode == ThemeMode.Light));
            Assert.Null(settings?["SomeNewSection"]);
            Assert.NotEqual(999, settings?["Layout"]?["SidebarWidth"]?.GetValue<double>());

            // Offline: the change waits, the operator is told once, and it goes up as soon as Depot answers again.
            var sync = restarted.Services.GetRequiredService<SettingsCarrierSync>();
            await sync.RunOnceAsync();
            space.Offline = true;
            var terminal = restarted.Services.GetRequiredService<ITerminalSettingsStore>();
            await terminal.SaveAsync(await terminal.LoadAsync() with { FontSize = 16 });
            await HeadlessAvalonia.RunAsync(() => Until.CollectionHolds(restarted.Cockpit.Toasts, () =>
                restarted.Cockpit.Toasts.Any(toast => toast.Message.StartsWith("Your settings could not be synced with Depot: Journey", StringComparison.Ordinal))));
            Assert.NotEqual(16, space.Document?["Terminal"]?["FontSize"]?.GetValue<int>());

            space.Offline = false;
            await sync.RunOnceAsync();
            Assert.Equal(16, space.Document?["Terminal"]?["FontSize"]?.GetValue<int>());
        }
        finally
        {
            JourneyHost.RemoveStateRoot(work);
        }
    }

    // The bundled Depot plugin with one connection and the host holding its sign-in, as RestoreJourney seeds it.
    private static async Task _SignedInAsync(JourneyHost cockpit, string url)
    {
        var connections = JsonSerializer.Serialize(new[] { new { Id = "journey", Name = "Journey", Url = url } });
        await cockpit.Services.GetRequiredService<IPluginRegistrationStore>().SaveDataAsync("depot", new Dictionary<string, string> { ["connections"] = connections });
        await cockpit.Services.GetRequiredService<IMcpOAuthTokenStore>().SaveAsync(
            "journey",
            "Depot: Journey",
            new McpOAuthToken { AccessToken = _ProfileSpace.Bearer, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Scope = "depot", ResourceUrl = $"{url}/mcp" });
        await cockpit.StartDesktopAsync();
    }

    // Depot's /cockpit/profile (DEP-168) for the one account the bearer names, with If-Match on the SHA-256 of the bytes.
    private sealed class _ProfileSpace
    {
        public const string Bearer = "journey-depot-bearer";

        private byte[]? _stored;

        public bool Offline { get; set; }

        public JsonObject? Document
        {
            get => _stored is null ? null : JsonNode.Parse(_stored) as JsonObject;
            set => _stored = value is null ? null : JsonSerializer.SerializeToUtf8Bytes(value);
        }

        public async Task HandleAsync(HttpContext context)
        {
            var request = context.Request;
            if (Offline)
            {
                context.Response.StatusCode = 503;
                return;
            }

            if (request.Headers.Authorization != $"Bearer {Bearer}" || request.Path != "/cockpit/profile")
            {
                context.Response.StatusCode = request.Path.StartsWithSegments("/cockpit") ? 401 : 404;
                return;
            }

            var current = _stored is null ? null : Convert.ToHexStringLower(SHA256.HashData(_stored));
            if (HttpMethods.IsGet(request.Method))
            {
                if (_stored is null)
                {
                    context.Response.StatusCode = 204;
                    return;
                }

                await context.Response.Body.WriteAsync(_stored);
                return;
            }

            var expected = request.Headers.IfMatch.FirstOrDefault()?.Trim('"');
            var createOnly = request.Headers.IfNoneMatch == "*";
            if (createOnly ? current is not null : expected is null || expected != current)
            {
                context.Response.StatusCode = expected is null && !createOnly ? 428 : 412;
                return;
            }

            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body);
            _stored = body.ToArray();
        }
    }
}
