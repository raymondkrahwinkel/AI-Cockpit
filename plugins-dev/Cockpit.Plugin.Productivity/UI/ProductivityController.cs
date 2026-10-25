using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.TimeTracking;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Productivity.UI;

internal sealed class ProductivityController(IPluginUiChannel channel, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SideMenuButtonBadge? _badge;

    public event Action? Changed;

    public DateOnly SelectedDate { get; private set; } = DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    public IReadOnlyList<CockpitTimeEntry> Entries { get; private set; } = [];

    public CockpitRunningStopwatch? Stopwatch { get; private set; }

    public TimeSpan TodayTotal { get; private set; }

    public AttentionTime? Day { get; private set; }

    public string? Error { get; private set; }

    public string? ProjectFallback { get; private set; }

    public bool IsToday => SelectedDate == _Today();

    public bool CanStartStopwatch => IsToday && Stopwatch is null;

    public string? StartDisabledReason => Stopwatch is not null
        ? "Er loopt al een stopwatch. Stop die eerst."
        : IsToday ? null : "Een stopwatch kan alleen voor vandaag starten.";

    public void AttachBadge(SideMenuButtonBadge badge)
    {
        _badge = badge;
        _PublishChanged();
    }

    public void UpdateProjectFallback(string? workingDirectory)
    {
        ProjectFallback = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(workingDirectory));
        _PublishChanged();
    }

    public async Task RefreshAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _RefreshLockedAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _PublishChanged();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SelectDateAsync(DateOnly date)
    {
        SelectedDate = date;
        await RefreshAsync().ConfigureAwait(false);
    }

    public Task<bool> AddAsync(string description, string durationText, string? projectId) =>
        _SaveAsync(null, description, durationText, projectId, _EntryStart());

    public Task<bool> UpdateAsync(CockpitTimeEntry entry, string description, string durationText, string? projectId) =>
        _SaveAsync(entry.Id, description, durationText, projectId, entry.StartedAt);

    public Task<bool> DeleteAsync(Guid id) =>
        _MutateAsync(ProductivityChannel.Delete, new ProductivityDeleteRequest(id));

    public async Task<bool> StartStopwatchAsync(string description, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return _Reject("Geef de stopwatch een omschrijving.");
        }

        if (StartDisabledReason is { } reason)
        {
            return _Reject(reason);
        }

        return await _MutateAsync(
            ProductivityChannel.StartStopwatch,
            new ProductivityStopwatchRequest(description.Trim(), _Project(projectId))).ConfigureAwait(false);
    }

    public Task<bool> StopStopwatchAsync() =>
        _MutateAsync(ProductivityChannel.StopStopwatch, new { });

    public async Task<bool> UpdateStopwatchAsync(string description, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return _Reject("Geef de stopwatch een omschrijving.");
        }

        return await _MutateAsync(
            ProductivityChannel.UpdateStopwatch,
            new ProductivityStopwatchRequest(description.Trim(), _Project(projectId))).ConfigureAwait(false);
    }

    public void ClearError()
    {
        Error = null;
        _PublishChanged();
    }

    public void Dispose() => _gate.Dispose();

    private async Task<bool> _SaveAsync(Guid? id, string description, string durationText, string? projectId, DateTimeOffset startedAt)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return _Reject("Geef de regel een omschrijving.");
        }

        if (DurationText.Parse(durationText) is not { } duration)
        {
            return _Reject("Voer een geldige duur in, bijvoorbeeld 0:15, 15m of 1,5u.");
        }

        var action = id is null ? ProductivityChannel.Add : ProductivityChannel.Update;
        return await _MutateAsync(action, new ProductivitySaveRequest(id, description.Trim(), startedAt, duration, _Project(projectId))).ConfigureAwait(false);
    }

    private async Task<bool> _MutateAsync(string action, object request)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _AskAsync<JsonElement>(action, request).ConfigureAwait(false);
            await _RefreshLockedAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _PublishChanged();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task _RefreshLockedAsync()
    {
        var (from, to) = _Range(SelectedDate);
        var (todayFrom, todayTo) = _Range(_Today());
        var snapshot = await _AskAsync<ProductivitySnapshot>(
            ProductivityChannel.Snapshot,
            new ProductivitySnapshotRequest(from, to, todayFrom, todayTo)).ConfigureAwait(false);
        Entries = [.. snapshot.Entries.OrderBy(entry => entry.StartedAt)];
        TodayTotal = snapshot.TodayTotal;
        Day = snapshot.Day;
        Stopwatch = snapshot.Stopwatch;
        Error = null;
        _PublishChanged();
    }

    private async Task<T> _AskAsync<T>(string action, object request)
    {
        var payload = JsonSerializer.SerializeToElement(request, request.GetType(), ProductivityChannel.Json);
        var answer = await channel.InvokeAsync(action, payload).ConfigureAwait(false);
        return answer.Deserialize<T>(ProductivityChannel.Json)
            ?? throw new InvalidOperationException($"The '{action}' response was empty.");
    }

    private (DateTimeOffset From, DateTimeOffset To) _Range(DateOnly date) =>
        (_AtLocalTime(date, TimeOnly.MinValue), _AtLocalTime(date.AddDays(1), TimeOnly.MinValue));

    private DateTimeOffset _EntryStart()
    {
        var now = time.GetLocalNow();
        return _AtLocalTime(SelectedDate, TimeOnly.FromTimeSpan(now.TimeOfDay));
    }

    private static DateTimeOffset _AtLocalTime(DateOnly date, TimeOnly clock)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(clock), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private DateOnly _Today() => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    private string? _Project(string? projectId) =>
        string.IsNullOrWhiteSpace(projectId) ? ProjectFallback : projectId.Trim();

    private bool _Reject(string message)
    {
        Error = message;
        _PublishChanged();
        return false;
    }

    private void _PublishChanged()
    {
        void Publish()
        {
            if (_badge is { } badge)
            {
                var totalMinutes = (long)TodayTotal.TotalMinutes;
                badge.Text = string.Create(CultureInfo.InvariantCulture, $"{totalMinutes / 60}u{totalMinutes % 60:00} vandaag");
            }

            Changed?.Invoke();
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Publish();
        }
        else
        {
            Dispatcher.UIThread.Post(Publish);
        }
    }
}
