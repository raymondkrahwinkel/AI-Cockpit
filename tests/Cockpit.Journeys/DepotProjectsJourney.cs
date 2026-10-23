using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Mcp;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Journeys;

// AC-1634: the bundled Depot plugin's own project discovery is REST-only on a current Depot. The fake deliberately
// has no working MCP endpoint, so either picker regresses visibly when a plugin-owned list_projects call returns.
[Collection(JourneyCollection.Alone)]
public sealed class DepotProjectsJourney
{
    [Fact]
    public async Task ProjectDiscoveryDefinitionLogoMemorySinkAndEventNudge_WorkWithoutDepotMcpFileTools()
    {
        await using var depot = await _FakeDepot.StartAsync();
        await using var cockpit = JourneyHost.Desktop();
        var url = depot.BaseUrl.TrimEnd('/');
        var connections = JsonSerializer.Serialize(new[] { new { Id = "journey", Name = "Journey", Url = url } });
        await cockpit.Services.GetRequiredService<IPluginRegistrationStore>().SaveDataAsync(
            "depot", new Dictionary<string, string> { ["connections"] = connections });
        await cockpit.Services.GetRequiredService<IMcpOAuthTokenStore>().SaveAsync(
            "journey",
            "Depot: Journey",
            new McpOAuthToken
            {
                AccessToken = Bearer,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                Scope = "depot",
                ResourceUrl = $"{url}/mcp",
            });
        await cockpit.StartDesktopAsync();

        var memory = cockpit.Services.GetRequiredService<IProjectMemorySourceRegistry>().Sources.Single(source => source.Scheme == "depot");
        var shared = cockpit.Services.GetRequiredService<ISharedProjectSourceRegistry>().Sources.Single(source => source.Key == "depot");
        var locations = await memory.ListLocationsAsync!(CancellationToken.None);
        var projects = await shared.ListAsync(CancellationToken.None);

        Assert.True(locations.Outcome == ProjectMemorySourceLocationsOutcome.Success, locations.Error);
        Assert.Equal("acme/cockpit", Assert.Single(locations.Locations).Value);
        var listedProject = Assert.Single(projects.Projects);
        Assert.Equal(("Cockpit from Depot", "Editor", false), (listedProject.Name, listedProject.Role, listedProject.CanWriteBack));

        const string localProjectId = "bound-depot-project";
        await cockpit.Services.GetRequiredService<IProjectEditor>().AddBoundProjectAsync(new Project(localProjectId, "Bound Depot project")
        {
            MemoryRef = "depot:acme/cockpit",
            SharedSourceName = shared.SourceName,
        });
        var ownership = cockpit.Services.GetRequiredService<IProjectOwnershipRegistry>();

        var opening = await shared.PrepareBindingAsync("depot:acme/cockpit", CancellationToken.None);
        Assert.True(opening.Succeeded, opening.Error);
        var readOnlyEditor = await _EditorAsync(cockpit, shared, opening.Binding!, ownership.Resolve(localProjectId)!);
        Assert.Contains("Editor access is read-only", readOnlyEditor.NameOrigin.ReadOnlyReason, StringComparison.Ordinal);
        Assert.False(readOnlyEditor.SaveCommand.CanExecute(null));

        await depot.AllowWritesAsync();
        await Until.CollectionHolds(cockpit.Cockpit.Projects.RecentCards,
            () => ownership.Resolve(localProjectId)?[HostProjectField.Name]?.IsEditable == true);

        var writable = await shared.PrepareBindingAsync("depot:acme/cockpit", CancellationToken.None);
        Assert.True(writable.Succeeded, writable.Error);
        var firstEditor = await _EditorAsync(cockpit, shared, writable.Binding!, ownership.Resolve(localProjectId)!);
        Assert.True(firstEditor.SaveCommand.CanExecute(null));
        firstEditor.Name = "Saved from Cockpit";
        await firstEditor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Saved from Cockpit", depot.DefinitionName);

        var stale = await shared.PrepareBindingAsync("depot:acme/cockpit", CancellationToken.None);
        Assert.True(stale.Succeeded, stale.Error);
        depot.ChangeFromSecondClient("Changed by a second client");
        var staleEditor = await _EditorAsync(cockpit, shared, stale.Binding!, ownership.Resolve(localProjectId)!);
        SharedProjectBinding? conflictScreen = null;
        staleEditor.ConflictRequested += (_, latest) =>
        {
            conflictScreen = latest;
            return Task.FromResult<ProjectDefinitionConflictResolution?>(null);
        };
        staleEditor.Name = "Stale Cockpit edit";

        await staleEditor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Changed by a second client", conflictScreen?.Name);
        Assert.Equal("Changed by a second client", depot.DefinitionName);

        await cockpit.Services.GetRequiredService<DepotSyncWatcher>().CheckNowAsync(localProjectId);

        await depot.ChangeDefinitionAsync();
        await Until.CollectionHolds(cockpit.Cockpit.Projects.RecentCards, () =>
            cockpit.Cockpit.Projects.RecentCards.SingleOrDefault(card => card.Project.Id == localProjectId)?.HasRemoteChanges == true);

        Assert.True(cockpit.Cockpit.Projects.RecentCards.Single(card => card.Project.Id == localProjectId).HasRemoteChanges);

        var logoBaseline = await shared.PrepareBindingAsync("depot:acme/cockpit", CancellationToken.None);
        Assert.True(logoBaseline.Succeeded, logoBaseline.Error);
        var baseline = Assert.IsType<SharedProjectBinding>(logoBaseline.Binding);
        Assert.NotNull(baseline.Checksum);
        var logoBytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var logoWrite = await shared.WriteBackAsync(
            "depot:acme/cockpit",
            new SharedProjectDefinitionEdit(
                baseline.Name,
                baseline.Description,
                baseline.BehaviorPrompt,
                baseline.IsolateInWorktreeByDefault,
                baseline.EnabledMcpServerNames,
                SharedProjectLogoEdit.Replace(logoBytes)),
            baseline.Checksum,
            CancellationToken.None);
        Assert.Equal(SharedProjectWriteBackOutcome.Success, logoWrite.Outcome);

        var reopened = await shared.PrepareBindingAsync("depot:acme/cockpit", CancellationToken.None);
        Assert.True(reopened.Succeeded, reopened.Error);
        Assert.Equal(logoBytes, reopened.Binding?.LogoBytes);

        const string note = "remember this through REST\n";
        var appended = await memory.AppendNoteAsync!("acme/cockpit", note, CancellationToken.None);
        Assert.Equal(ProjectMemoryAppendOutcome.Success, appended.Outcome);
        Assert.Equal(note, depot.Inbox);
    }

    private static Task<ProjectDialogViewModel> _EditorAsync(
        JourneyHost cockpit, ISharedProjectSource source, SharedProjectBinding baseline,
        IReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?> fieldOwnership) =>
        ProjectDialogViewModel.CreateAsync(
            Project.Create(baseline.Name),
            cockpit.Services.GetRequiredService<ISessionProfileStore>(),
            cockpit.Services.GetRequiredService<IMcpServerCatalog>(),
            fieldOwnership: fieldOwnership,
            sharedWriteBack: new ProjectSharedWriteBackContext(source, "depot:acme/cockpit", baseline));

    private const string Bearer = "journey-depot-projects-bearer";

    private sealed class _NoListProjectsTools
    {
        [McpServerTool(Name = "ping"), Description("A harmless tool; list_projects is deliberately absent.")]
        public static string Ping() => "pong";
    }

    private sealed class _FakeDepot(WebApplication app, string baseUrl, _DefinitionState state) : IAsyncDisposable
    {
        public string BaseUrl { get; } = baseUrl;
        public string DefinitionName => state.Name;
        public string Inbox => state.Inbox;

        public void ChangeFromSecondClient(string name) => state.ChangeFromSecondClient(name);

        public async Task AllowWritesAsync()
        {
            var events = app.Services.GetRequiredService<_ProjectEvents>();
            await events.Subscribed.Task.WaitAsync(Until.Ceiling);
            state.AllowWrites();
            await app.Services.GetRequiredService<IHubContext<_ProjectsHub>>().Clients.Group("projects").SendAsync(
                "event",
                new { topic = "projects", kind = "accessChanged", address = (string?)null, version = 1L });
        }

        public static async Task<_FakeDepot> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Services.AddMcpServer().WithHttpTransport().WithTools<_NoListProjectsTools>();
            builder.Services.AddSignalR();
            builder.Services.AddSingleton<_ProjectEvents>();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var state = new _DefinitionState();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/cockpit")
                    || context.Request.Path.StartsWithSegments("/mcp")
                    || context.Request.Path.StartsWithSegments("/sync"))
                {
                    if (context.Request.Headers.Authorization != $"Bearer {Bearer}")
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                }

                await next();
            });
            app.MapGet("/cockpit/projects", state.WriteProjectsAsync);
            app.MapPut("/cockpit/projects/{org}/{slug}/definition", state.WriteDefinitionAsync);
            app.MapPost("/sync/{org}/{slug}/append", state.AppendAsync);
            app.MapPost("/sync/{org}/{slug}/uploads", state.RequestUploadsAsync);
            app.MapPost("/sync/{org}/{slug}/downloads", state.RequestDownloadsAsync);
            app.MapPost("/sync/{org}/{slug}/delete", state.DeleteAsync);
            app.MapPut("/blob/upload/{id}", state.UploadAsync);
            app.MapGet("/blob/download", state.DownloadAsync);
            app.MapGet("/cockpit/me", () => new { userId = "journey-user" });
            app.MapHub<_ProjectsHub>("/hubs/cockpit");
            app.MapMcp("/mcp");
            await app.StartAsync();

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("The fake Depot did not expose its loopback address.");
            return new _FakeDepot(app, addresses.Addresses.Single().TrimEnd('/'), state);
        }

        public async Task ChangeDefinitionAsync()
        {
            var events = app.Services.GetRequiredService<_ProjectEvents>();
            await events.Subscribed.Task.WaitAsync(Until.Ceiling);
            state.ChangeFromSecondClient("Changed by a Depot event");
            await app.Services.GetRequiredService<IHubContext<_ProjectsHub>>().Clients.Group("projects").SendAsync(
                "event",
                new { topic = "projects", kind = "definitionChanged", address = "acme/cockpit", version = 1L });
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

    }

    private sealed class _DefinitionState
    {
        private int _version = 1;
        private byte[] _logoBytes = [];

        public string Name { get; private set; } = "Cockpit from Depot";
        public string? Logo { get; private set; }
        public string Inbox { get; private set; } = string.Empty;
        public bool CanWrite { get; private set; }

        private string Checksum => $"definition-v{_version}";

        public void ChangeFromSecondClient(string name)
        {
            Name = name;
            _version++;
        }

        public void AllowWrites()
        {
            CanWrite = true;
            _version++;
        }

        public async Task WriteProjectsAsync(HttpContext context)
        {
            context.Response.Headers.ETag = $"\"projects-v{_version}\"";
            await context.Response.WriteAsJsonAsync(new
            {
                projects = new[]
                {
                    new
                    {
                        address = "acme/cockpit",
                        slug = "cockpit",
                        name = "Cockpit",
                        role = "Editor",
                        kind = "Project",
                        organization = new { slug = "acme", name = "Acme", role = "Member" },
                        cockpit = new
                        {
                            state = "ok",
                            checksum = Checksum,
                            updatedAt = DateTimeOffset.UtcNow,
                            definition = new { schemaVersion = 1, name = Name, logo = Logo },
                            canWrite = CanWrite,
                        },
                    },
                },
            });
        }

        public async Task WriteDefinitionAsync(HttpContext context)
        {
            var expected = context.Request.Headers.IfMatch.ToString();
            if (!string.Equals(expected, "*", StringComparison.Ordinal)
                && !string.Equals(expected, $"\"{Checksum}\"", StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
                return;
            }

            var definition = await context.Request.ReadFromJsonAsync<JsonElement>();
            Name = definition.GetProperty("name").GetString()
                ?? throw new InvalidOperationException("The journey definition has no name.");
            Logo = definition.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.String
                ? logo.GetString()
                : null;
            _version++;
            context.Response.Headers.ETag = $"\"{Checksum}\"";
            await context.Response.WriteAsJsonAsync(new { checksum = Checksum });
        }

        public async Task AppendAsync(HttpContext context)
        {
            var request = await context.Request.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Inbox.md", request.GetProperty("path").GetString());
            Inbox += request.GetProperty("content").GetString();
            await context.Response.WriteAsJsonAsync(new { path = "Inbox.md" });
        }

        public async Task RequestUploadsAsync(HttpContext context)
        {
            var request = await context.Request.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(".cockpit/logo.png", request.GetProperty("files")[0].GetProperty("path").GetString());
            await context.Response.WriteAsJsonAsync(new
            {
                uploads = new[] { new { uploadUrl = $"{context.Request.Scheme}://{context.Request.Host}/blob/upload/logo" } },
            });
        }

        public async Task RequestDownloadsAsync(HttpContext context)
        {
            var request = await context.Request.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(".cockpit/logo.png", request.GetProperty("prefix").GetString());
            await context.Response.WriteAsJsonAsync(new
            {
                files = new[] { new { downloadUrl = $"{context.Request.Scheme}://{context.Request.Host}/blob/download" } },
            });
        }

        public Task DeleteAsync(HttpContext context)
        {
            _logoBytes = [];
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        }

        public async Task UploadAsync(HttpContext context)
        {
            Assert.False(context.Request.Headers.ContainsKey("Authorization"));
            using var stream = new MemoryStream();
            await context.Request.Body.CopyToAsync(stream);
            _logoBytes = stream.ToArray();
            context.Response.StatusCode = StatusCodes.Status201Created;
        }

        public async Task DownloadAsync(HttpContext context)
        {
            Assert.False(context.Request.Headers.ContainsKey("Authorization"));
            await context.Response.Body.WriteAsync(_logoBytes);
        }
    }

    private sealed class _ProjectEvents
    {
        public TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class _ProjectsHub(_ProjectEvents events) : Hub
    {
        public async Task Subscribe(string topic)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, topic);
            events.Subscribed.TrySetResult();
        }
    }
}
