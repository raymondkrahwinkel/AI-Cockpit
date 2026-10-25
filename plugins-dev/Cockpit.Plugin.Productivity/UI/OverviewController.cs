using System.Text.Json;
using Avalonia.Threading;
using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Productivity.UI;

internal sealed class OverviewController(IPluginUiChannel channel, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = time;
    private DateOnly _lastValidFrom = _Monday(DateOnly.FromDateTime(time.GetLocalNow().DateTime));
    private DateOnly _lastValidTo = _Monday(DateOnly.FromDateTime(time.GetLocalNow().DateTime)).AddDays(4);
    private string _lastValidRange = "Werkweek";
    private bool _disposed;
    private long _refreshGeneration;

    public event Action? Changed;

    public DateOnly From { get; private set; } = _Monday(DateOnly.FromDateTime(time.GetLocalNow().DateTime));

    public DateOnly To { get; private set; } = _Monday(DateOnly.FromDateTime(time.GetLocalNow().DateTime)).AddDays(4);

    public string SelectedRange { get; private set; } = "Werkweek";

    public bool IncludeAgentWithoutYou { get; private set; }

    public ProductivityOverviewSnapshot? Snapshot { get; private set; }

    public string? Error { get; private set; }

    public void SetIncludeAgentWithoutYou(bool include)
    {
        IncludeAgentWithoutYou = include;
        _PublishChanged();
    }

    public async Task SelectRangeAsync(string range)
    {
        SelectedRange = range;
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        (From, To) = range switch
        {
            "Uur" or "Dag" => (today, today),
            "Werkweek" => (_Monday(today), _Monday(today).AddDays(4)),
            "Week" => (_Monday(today), _Monday(today).AddDays(6)),
            "Maand" => (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1)),
            _ => (From, To),
        };
        if (range == "Eigen bereik")
        {
            _PublishChanged();
            return;
        }

        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task SetCustomRangeAsync(DateOnly from, DateOnly to)
    {
        if (from > to)
        {
            Error = "De begindatum moet op of vóór de einddatum liggen.";
            _PublishChanged();
            return;
        }

        SelectedRange = "Eigen bereik";
        From = from;
        To = to;
        await RefreshAsync().ConfigureAwait(false);
    }

    public void ReportMissingCustomRange()
    {
        Error = "Kies zowel een begin- als einddatum.";
        _PublishChanged();
    }

    public async Task MoveAsync(int direction)
    {
        var days = SelectedRange switch
        {
            "Uur" or "Dag" => 1,
            "Werkweek" => 7,
            "Week" => 7,
            "Maand" => 0,
            _ => Math.Max(1, To.DayNumber - From.DayNumber + 1),
        };
        if (SelectedRange == "Maand")
        {
            From = From.AddMonths(direction);
            To = From.AddMonths(1).AddDays(-1);
        }
        else
        {
            From = From.AddDays(days * direction);
            To = To.AddDays(days * direction);
        }

        await RefreshAsync().ConfigureAwait(false);
    }

    public async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _refreshGeneration);
        var requestedFrom = From;
        var requestedTo = To;
        var requestedRange = SelectedRange;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || generation.Equals(Volatile.Read(ref _refreshGeneration)) is false)
            {
                return;
            }

            var request = new ProductivityOverviewRequest(_AtLocalTime(requestedFrom), _AtLocalTime(requestedTo.AddDays(1)));
            var snapshot = await _AskAsync<ProductivityOverviewSnapshot>(ProductivityChannel.Overview, request).ConfigureAwait(false);
            if (_disposed || generation.Equals(Volatile.Read(ref _refreshGeneration)) is false)
            {
                return;
            }

            Snapshot = snapshot;
            Error = null;
            _lastValidFrom = requestedFrom;
            _lastValidTo = requestedTo;
            _lastValidRange = requestedRange;
        }
        catch (Exception exception)
        {
            if (_disposed || generation.Equals(Volatile.Read(ref _refreshGeneration)) is false)
            {
                return;
            }

            Error = exception.Message;
            From = _lastValidFrom;
            To = _lastValidTo;
            SelectedRange = _lastValidRange;
        }
        finally
        {
            _gate.Release();
            if (generation == Volatile.Read(ref _refreshGeneration))
            {
                _PublishChanged();
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _refreshGeneration);
        Changed = null;
    }

    private async Task<T> _AskAsync<T>(string action, object request)
    {
        var payload = JsonSerializer.SerializeToElement(request, request.GetType(), ProductivityChannel.Json);
        var answer = await channel.InvokeAsync(action, payload).ConfigureAwait(false);
        return answer.Deserialize<T>(ProductivityChannel.Json)
            ?? throw new InvalidOperationException($"The '{action}' response was empty.");
    }

    private static DateOnly _Monday(DateOnly day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    private static DateTimeOffset _AtLocalTime(DateOnly date)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private void _PublishChanged()
    {
        if (_disposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Changed?.Invoke();
        }
        else
        {
            Dispatcher.UIThread.Post(() => Changed?.Invoke());
        }
    }
}
