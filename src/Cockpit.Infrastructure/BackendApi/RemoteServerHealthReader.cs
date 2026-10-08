using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1457: one server's health as the desktop reads it. Asked on connect, on every open of the tab, after an action, and
// then at the desktop-configured interval while a tab shows it or stays in the background.
internal sealed class RemoteServerHealthReader(BackendApiClient client, string server, INotificationSettingsStore settingsStore, ILogger logger) : IRemoteServerHealth
{
    private volatile RemoteServerHealth? _current;
    private readonly Lock _waitGate = new();
    private int _isWatched;
    private CancellationTokenSource? _delayCancellation;

    public RemoteServerHealth? Current => _current;

    public bool IsWatched
    {
        get => Volatile.Read(ref _isWatched) != 0;
        set
        {
            if (Interlocked.Exchange(ref _isWatched, value ? 1 : 0) == 0 && value)
            {
                lock (_waitGate)
                {
                    _delayCancellation?.Cancel();
                }
            }
        }
    }

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
                var interval = await _IntervalAsync(stop).ConfigureAwait(false);
                await _WaitAsync(interval, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<TimeSpan> _IntervalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            return IsWatched ? settings.RemoteHealthWatchedInterval : settings.RemoteHealthBackgroundInterval;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not read the remote health refresh interval; using the default.");
            return IsWatched ? NotificationSettings.DefaultRemoteHealthWatchedInterval : NotificationSettings.DefaultRemoteHealthBackgroundInterval;
        }
    }

    private async Task _WaitAsync(TimeSpan interval, CancellationToken stop)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_waitGate)
        {
            _delayCancellation = cancellation;
        }

        try
        {
            await Task.Delay(interval, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_waitGate)
            {
                if (ReferenceEquals(_delayCancellation, cancellation))
                {
                    _delayCancellation = null;
                }
            }
        }
    }

    private static RemoteServerHealth _Map(HealthDto dto) => new(
        [.. (dto.Profiles ?? []).Select(profile => new RemoteProfileHealth(profile.Label, profile.Provider, profile.SignIn, profile.LastCheck, profile.ExpiredSince, profile.AnnouncedAt, profile.Credential))],
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
            [.. (section.Rows ?? []).Select(row => new RemoteHealthRow(row.Label, row.Status != "ok", row.At, row.ActionId, row.Schedule, row.TimeZone))]))]);

    private sealed record HealthDto(List<ProfileDto>? Profiles, ServerDto? Server, List<SectionDto>? Sections);

    private sealed record ProfileDto(string Label, string Provider, string SignIn, DateTimeOffset? LastCheck, DateTimeOffset? ExpiredSince, DateTimeOffset? AnnouncedAt, string? Credential = null);

    private sealed record ServerDto(string? Version, string? Image, DateTimeOffset? StartedAt, string? Address, AssistantDto? Assistant, List<KeyDto>? Keys);

    private sealed record AssistantDto(string? Holder);

    private sealed record KeyDto(string Label, string Capability);

    private sealed record SectionDto(string Name, bool Healthy, List<RowDto>? Rows);

    private sealed record RowDto(string Label, string Status, DateTimeOffset? At, string? ActionId, string? Schedule, string? TimeZone);

    private sealed record ActionDto(bool Succeeded);
}
