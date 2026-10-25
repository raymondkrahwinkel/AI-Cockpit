using System.Text.Json;
using Avalonia.Threading;
using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Productivity.UI;

internal sealed class OverviewController(IPluginUiChannel channel, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action? Changed;

    public DateOnly From { get; private set; } = _Monday(DateOnly.FromDateTime(time.GetLocalNow().DateTime));

    public DateOnly To { get; private set; } = _Monday(DateOnly.FromDateTime(time.GetLocalNow().DateTime)).AddDays(4);

    public ProductivityOverviewSnapshot? Snapshot { get; private set; }

    public string? Error { get; private set; }

    public async Task RefreshAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var request = new ProductivityOverviewRequest(_AtLocalTime(From), _AtLocalTime(To.AddDays(1)));
            Snapshot = await _AskAsync<ProductivityOverviewSnapshot>(ProductivityChannel.Overview, request).ConfigureAwait(false);
            Error = null;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
        finally
        {
            _gate.Release();
            _PublishChanged();
        }
    }

    public void Dispose() => _gate.Dispose();

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
