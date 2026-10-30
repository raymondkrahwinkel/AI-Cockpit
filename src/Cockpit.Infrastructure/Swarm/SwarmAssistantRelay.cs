using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Core.Assistant;
using Cockpit.Core.Mcp;
using Cockpit.Core.Swarm;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Swarm;

internal sealed record SwarmAssistantMessage(Guid Id, string Text, byte[][] Images, AgentMessage? Inbox = null, bool StartOnly = false);

internal sealed class SwarmAssistantRelay : BackgroundService, ISwarmAssistantRelay, ISingletonService
{
    internal const string Outbox = "cockpit-assistant@swarm";
    private readonly ISwarmLeadership _leadership;
    private readonly IServiceProvider _services;
    private readonly SemaphoreSlim _receive = new(1, 1);
    private readonly Dictionary<(SwarmMemberId, Guid), string> _accepted = [];

    public SwarmAssistantRelay(ISwarmLeadership leadership, IServiceProvider services)
    {
        _leadership = leadership;
        _services = services;
        Start = ct => services.GetRequiredService<AssistantSessionHost>().EnsureLocalStartedAsync(ct);
        Transport = PostAsync;
    }

    internal Func<CancellationToken, Task<IAssistantSession?>> Start { get; set; }
    internal Func<SwarmMember, SwarmAssistantMessage, CancellationToken, Task<bool>> Transport { get; set; }

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default) =>
        (await _services.GetRequiredService<SwarmMembershipStore>().LoadAsync(ct))
            .PublicKeyOf(_services.GetRequiredService<SwarmIdentity>().MemberId) is not null;

    public Task EnsureStartedAsync(CancellationToken ct = default) => SendAsync(new(Guid.NewGuid(), "", [], StartOnly: true), ct);

    public Task SendAsync(string text, IReadOnlyList<byte[]> images, CancellationToken ct = default) =>
        SendAsync(new(Guid.NewGuid(), text, images.ToArray()), ct);

    internal async Task SendAsync(SwarmAssistantMessage message, CancellationToken ct)
    {
        SwarmMember? attempted = null;
        var self = _services.GetRequiredService<SwarmIdentity>().MemberId;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var target = attempted ?? _leadership.Current;
            if (target is not null)
            {
                try
                {
                    var accepted = target.Id == self
                        ? await ReceiveAsync(self, message, ct)
                        : await Transport(target, message, ct);
                    if (accepted) return;
                    attempted = null;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // A lost acknowledgement must be resolved at the original receiver before choosing another leader.
                    attempted = target;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    internal async Task<bool> ReceiveAsync(SwarmMemberId source, SwarmAssistantMessage message, CancellationToken ct)
    {
        await _receive.WaitAsync(ct);
        try
        {
            var factory = _services.GetService<IAssistantSessionFactory>();
            return await (factory?.OnOwnerThreadAsync(() => ReceiveOnOwnerAsync(source, message, ct))
                ?? ReceiveOnOwnerAsync(source, message, ct));
        }
        finally
        {
            _receive.Release();
        }
    }

    private async Task<bool> ReceiveOnOwnerAsync(SwarmMemberId source, SwarmAssistantMessage message, CancellationToken ct)
    {
        var stamp = JsonSerializer.Serialize(message);
        if (_accepted.TryGetValue((source, message.Id), out var previous))
        {
            if (previous != stamp) throw new InvalidDataException("The message id already names different content.");
            return true;
        }
        if (!_leadership.IsSelf) return false;
        var member = (await _services.GetRequiredService<SwarmMembershipStore>().LoadAsync(ct))
            .Members.SingleOrDefault(member => member.Id == source);
        if (member is null) return false;
        var session = message.Inbox is null ? await Start(ct) : null;
        return _leadership.WithLeader(leader =>
        {
            if (leader?.Id != _services.GetRequiredService<SwarmIdentity>().MemberId) return false;
            if (message.Inbox is { } mail)
            {
                var inbox = _services.GetRequiredService<IAgentMessageInbox>();
                if (inbox.Deliver(NodeSessionAddress.For(member.Name, mail.FromPaneId), AssistantIdentity.PaneId,
                    mail.Kind, $"[from {member.Name}] {mail.Body}", mail.SenderProfile, mail.SenderProjectId,
                    mail.ProfileWide, mail.SubjectPaneId).Message is null) return false;
            }
            else
            {
                if (session is not { IsSessionReady: true }) return false;
                if (message.StartOnly) return true;
                if (!session.CanTakeAPrompt) return false;
                foreach (var image in message.Images) session.AddPastedImage(image);
                session.InjectAndSubmit($"[from {member.Name}] {message.Text}");
            }
            _accepted.Add((source, message.Id), stamp);
            return true;
        });
    }

    private async Task<bool> PostAsync(SwarmMember member, SwarmAssistantMessage message, CancellationToken ct)
    {
        var server = (await _services.GetRequiredService<IMcpServerStore>().LoadAsync(ct)).Single(item =>
            item.Name == NodeServerName.For(member.Name, NodeServerName.SessionsServerName));
        var network = _services.GetRequiredService<SwarmNetworkPolicy>();
        using var handler = new SocketsHttpHandler { ConnectCallback = network.ConnectAsync };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && NodeCertificatePin.FingerprintOf(certificate) == server.PinnedCertificateFingerprint;
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(new Uri(server.Url!).GetLeftPart(UriPartial.Authority)),
            Timeout = NodeConnectionFailure.DefaultBudget,
        };
        client.DefaultRequestHeaders.Authorization = new("Bearer", server.ApiKey);
        using var response = await client.PostAsJsonAsync("swarm/assistant", message, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public static void Map(IEndpointRouteBuilder routes, IServiceProvider services)
    {
        var group = SwarmNetworkPolicy.MapGroup(routes, services).MapGroup("/assistant");
        group.AddEndpointFilter(async (context, next) =>
        {
            var caller = McpRequestContext.CurrentNodeCaller;
            if (caller is not { ByConnectKey: true, Capability: ConnectKeyCapability.Admin }) return Results.Unauthorized();
            var member = (await services.GetRequiredService<SwarmMembershipStore>().LoadAsync(context.HttpContext.RequestAborted))
                .Members.SingleOrDefault(member => SwarmKick.KeyLabel(member) == caller.Label);
            if (member is null) return Results.StatusCode(403);
            context.HttpContext.Items[typeof(SwarmAssistantRelay)] = member;
            return await next(context);
        });
        group.MapPost("", (Func<HttpContext, Task<IResult>>)(async context =>
        {
            var member = (SwarmMember)context.Items[typeof(SwarmAssistantRelay)]!;
            var message = await context.Request.ReadFromJsonAsync<SwarmAssistantMessage>(context.RequestAborted);
            if (message is null || message.Id == Guid.Empty || message.Text is null || message.Images is null
                || message.Images.Any(image => image is null)) return Results.BadRequest();
            return await services.GetRequiredService<SwarmAssistantRelay>().ReceiveAsync(member.Id, message, context.RequestAborted)
                ? Results.Ok() : Results.Conflict();
        }));
        group.MapGet("/transcript", (Func<Task<IResult>>)(async () =>
        {
            if (!services.GetRequiredService<ISwarmLeadership>().IsSelf) return Results.Conflict();
            if (services.GetRequiredService<ISessionRegistry>().Assistant is not { } assistant) return Results.NotFound();
            var snapshot = await assistant.ReadRowsAtAsync(() => services.GetRequiredService<IBackendEventLog>().LastSeq);
            return Results.Json(new { totalEntries = snapshot?.Rows.Count ?? 0, entries = Array.Empty<object>(), rows = snapshot?.Rows, seq = snapshot?.Seq });
        }));
        group.MapPost("/permissions/{toolUseId}", async (string toolUseId, SwarmPermissionAnswer body) =>
        {
            if (body.Scope is not null and not "once") return Results.BadRequest();
            if (!services.GetRequiredService<ISwarmLeadership>().IsSelf) return Results.Conflict();
            if (services.GetRequiredService<ISessionRegistry>().Assistant is not { } assistant) return Results.NotFound();
            var answered = await assistant.RespondToPermissionByIdAsync(toolUseId, body.Allow);
            return Results.Json(new { answered });
        });
        group.MapPost("/interrupt", (Func<Task<IResult>>)(async () =>
        {
            if (!services.GetRequiredService<ISwarmLeadership>().IsSelf) return Results.Conflict();
            if (services.GetRequiredService<ISessionRegistry>().Assistant is not { } assistant) return Results.NotFound();
            await assistant.UseControlAsync(control => control.InterruptAsync());
            return Results.Json(new { interrupted = true });
        }));
    }

    private sealed record SwarmPermissionAnswer(bool Allow, string? Scope = null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var inbox = _services.GetRequiredService<IAgentMessageInbox>();
        while (!stoppingToken.IsCancellationRequested)
        {
            var batch = inbox.TakeForDelivery(Outbox, 1);
            if (batch.Messages.FirstOrDefault() is { } mail)
            {
                try
                {
                    await SendAsync(new(Guid.Parse(mail.Id), "", [], mail), stoppingToken);
                    inbox.ConfirmDelivered(Outbox, [mail.Id]);
                }
                catch (Exception ex)
                {
                    inbox.ReturnUndelivered(Outbox, [mail.Id]);
                    if (stoppingToken.IsCancellationRequested) break;
                    _services.GetRequiredService<ILogger<SwarmAssistantRelay>>().LogWarning(ex, "Swarm assistant mail remains queued.");
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }
}
