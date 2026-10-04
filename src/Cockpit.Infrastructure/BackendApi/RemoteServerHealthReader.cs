using Cockpit.Core.Abstractions.Remote;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1457: one server's health as the desktop reads it. Asked on connect, on every open of the tab, after an action, and
// then every minute while a tab shows it, every five while none does, so the badge still follows a login that expired.
internal sealed class RemoteServerHealthReader(BackendApiClient client, string server, ILogger logger) : IRemoteServerHealth
{
    private static readonly TimeSpan Watched = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Unwatched = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Slice = TimeSpan.FromSeconds(5);

    private volatile RemoteServerHealth? _current;

    public RemoteServerHealth? Current => _current;

    public bool IsWatched { get; set; }

    public event EventHandler? Changed;

    public void Start(CancellationToken stop) => _ = _LoopAsync(stop);

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var answer = await client.GetAsync<HealthDto>("api/v1/health", cancellationToken).ConfigureAwait(false);
            _current = _Map(answer);
        }
        catch (BackendApiException exception)
        {
            // A key that may not read the health has nothing to show; any other answer keeps what was last read.
            logger.LogInformation("Server {Server} did not give its health ({Status}).", server, exception.Status);
            if (exception.Status is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
            {
                _current = null;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.Text.Json.JsonException or ObjectDisposedException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A request that timed out is a server that did not answer; only the caller's own token ends the loop.
            logger.LogInformation(exception, "Server {Server} did not answer its health route.", server);
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> RunActionAsync(string section, string actionId, CancellationToken cancellationToken = default)
    {
        var succeeded = false;
        try
        {
            var answer = await client.SendAsync<ActionDto>(
                HttpMethod.Post,
                $"api/v1/health/{Uri.EscapeDataString(section)}/actions/{Uri.EscapeDataString(actionId)}",
                null,
                cancellationToken).ConfigureAwait(false);
            succeeded = answer.Succeeded;
        }
        catch (Exception exception) when (exception is BackendApiException or HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            logger.LogInformation(exception, "Server {Server} did not run health action {Action}.", server, actionId);
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return succeeded;
    }

    private async Task _LoopAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await RefreshAsync(stop).ConfigureAwait(false);
                for (var waited = TimeSpan.Zero; waited < (IsWatched ? Watched : Unwatched); waited += Slice)
                {
                    await Task.Delay(Slice, stop).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static RemoteServerHealth _Map(HealthDto dto) => new(
        [.. (dto.Profiles ?? []).Select(profile => new RemoteProfileHealth(profile.Label, profile.Provider, profile.SignIn, profile.LastCheck, profile.ExpiredSince, profile.AnnouncedAt))],
        new RemoteServerFacts(
            dto.Server?.Version,
            dto.Server?.Image,
            dto.Server?.StartedAt,
            dto.Server?.Address,
            dto.Server?.Assistant?.Holder,
            [.. (dto.Server?.Keys ?? []).Select(key => new RemoteKeyHealth(key.Label, key.Capability))]),
        [.. (dto.Sections ?? []).Select(section => new RemoteHealthSection(
            section.Name,
            section.Healthy,
            [.. (section.Rows ?? []).Select(row => new RemoteHealthRow(row.Label, row.Status != "ok", row.At, row.ActionId))]))]);

    private sealed record HealthDto(List<ProfileDto>? Profiles, ServerDto? Server, List<SectionDto>? Sections);

    private sealed record ProfileDto(string Label, string Provider, string SignIn, DateTimeOffset? LastCheck, DateTimeOffset? ExpiredSince, DateTimeOffset? AnnouncedAt);

    private sealed record ServerDto(string? Version, string? Image, DateTimeOffset? StartedAt, string? Address, AssistantDto? Assistant, List<KeyDto>? Keys);

    private sealed record AssistantDto(string? Holder);

    private sealed record KeyDto(string Label, string Capability);

    private sealed record SectionDto(string Name, bool Healthy, List<RowDto>? Rows);

    private sealed record RowDto(string Label, string Status, DateTimeOffset? At, string? ActionId);

    private sealed record ActionDto(bool Succeeded);
}
