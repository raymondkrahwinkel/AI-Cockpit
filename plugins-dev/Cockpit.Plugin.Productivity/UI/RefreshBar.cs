using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Cockpit.Plugin.Productivity.Contracts;
using Material.Icons;

namespace Cockpit.Plugin.Productivity.UI;

// The ↻ button, a busy indicator and "last updated", plus the timer that refreshes a visible screen (AC-1816).
internal sealed class RefreshBar
{
    private readonly Control _owner;
    private readonly Func<Task> _refresh;
    private readonly Func<bool> _editing;
    private readonly Func<TimeSpan> _interval;
    private readonly Func<DateTimeOffset> _now;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Button _button = Ui.IconButton("RefreshButton", MaterialIconKind.Refresh, "Ververs nu");
    private readonly ProgressBar _busy = new() { Name = "RefreshBusy", IsIndeterminate = true, Width = 28, MinWidth = 0, Height = 4, MinHeight = 0, Opacity = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _age = Ui.Hint(string.Empty);
    private bool _wasVisible = true;
    private bool _refreshing;
    private DateTimeOffset? _last;

    public RefreshBar(Control owner, Func<Task> refresh, Func<bool> editing, Func<TimeSpan> interval, Func<DateTimeOffset> now)
    {
        _owner = owner;
        _refresh = refresh;
        _editing = editing;
        _interval = interval;
        _now = now;
        _age.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
        _age.VerticalAlignment = VerticalAlignment.Center;
        _age.Name = "RefreshedAt";
        _button.Click += (_, _) => _ = _RunAsync();
        View = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _age, _busy, _button } };
        _timer.Tick += (_, _) => _Tick();
        owner.AttachedToVisualTree += (_, _) => _timer.Start();
        owner.DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public Control View { get; }

    public void Update(bool refreshing, DateTimeOffset? last)
    {
        _refreshing = refreshing;
        _last = last;
        _button.IsEnabled = !refreshing;
        _busy.Opacity = refreshing ? 1 : 0;
        _age.Text = RefreshPolicy.Age(last, _now());
    }

    private void _Tick()
    {
        _age.Text = RefreshPolicy.Age(_last, _now());
        // A tab that just became visible again refreshes at once, whatever the interval.
        var reactivated = _owner.IsEffectivelyVisible && !_wasVisible;
        _wasVisible = _owner.IsEffectivelyVisible;
        if (_IsDue(reactivated ? TimeSpan.Zero : _interval()))
        {
            _ = _RunAsync();
        }
    }

    private bool _IsDue(TimeSpan interval) =>
        RefreshPolicy.IsDue(_owner.IsEffectivelyVisible, _editing() || _InputInProgress(), _refreshing, _last, _now(), interval);

    // A focused text box with text in it is an edit under way; an empty one is only waiting.
    private bool _InputInProgress() =>
        TopLevel.GetTopLevel(_owner)?.FocusManager?.GetFocusedElement() is TextBox { Text.Length: > 0 } box && _owner.IsVisualAncestorOf(box);

    private async Task _RunAsync()
    {
        try
        {
            await _refresh();
        }
        catch (ObjectDisposedException)
        {
            // The controller was disposed with the plugin; there is nothing left to refresh.
        }
    }
}
