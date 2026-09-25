using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.Plugin.LocalCi.Contracts;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.LocalCi.UI;

// The last local run in this session's checkout, in this session's header (AC-1394: the UI part — LocalCiPlugin,
// the backend part, keeps the tracker and answers what this control asks over the plugin's channel). A strip has
// room for an indicator and not for a panel, so it is one short line with the whole story on its tooltip — and
// nothing at all until a run has happened, which is most sessions most of the time.
// Deliberately not the session's statusline: that line carries the ticket the session is working on, and a build
// result written over it trades a fact that lasts all day for one that lasts until the next run.
internal sealed class LocalCiSessionBadge : UserControl
{
    private readonly ICockpitUiHost _host;
    private readonly IPluginSessionContext _session;
    private readonly TextBlock _label = new();

    private IDisposable? _runChanged;
    private int _loadToken;

    public LocalCiSessionBadge(ICockpitUiHost host, IPluginSessionContext session)
    {
        _host = host;
        _session = session;

        Content = _label;
        IsVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _session.WorkingDirectoryChanged += _OnWorkingDirectoryChanged;
        _runChanged = _host.Channel.Subscribe(LocalCiChannel.RunChanged, _OnRunChanged);
        _ = _ShowAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _session.WorkingDirectoryChanged -= _OnWorkingDirectoryChanged;
        _runChanged?.Dispose();
        _runChanged = null;
    }

    private void _OnWorkingDirectoryChanged(object? sender, EventArgs e) => _ = _ShowAsync();

    // Raised on the backend's publishing thread, not the UI thread — marshal before touching a control.
    private void _OnRunChanged(PluginChannelEvent channelEvent) => Dispatcher.UIThread.Post(() => _ = _ShowAsync());

    private async Task _ShowAsync()
    {
        if (_session.WorkingDirectory is not { Length: > 0 } checkout)
        {
            IsVisible = false;
            return;
        }

        // Guard against overlapping loads (a signal burst, or the directory arriving mid-read): only the latest wins.
        var token = ++_loadToken;
        try
        {
            var payload = JsonSerializer.SerializeToElement(new LocalCiProjectRequest(checkout), LocalCiChannel.Json);
            var answer = await _host.Channel.InvokeAsync(LocalCiChannel.LastRun, payload);
            var summary = answer.Deserialize<LocalCiRunSummary>(LocalCiChannel.Json);
            if (token != _loadToken)
            {
                return;
            }

            if (summary is null)
            {
                IsVisible = false;
                return;
            }

            IsVisible = true;
            _label.Text = summary.Outcome switch
            {
                "Passed" => $"local: {summary.JobId} ✓",
                "Failed" => $"local: {summary.JobId} ✗",
                _ => $"local: {summary.JobId} —",
            };
            ToolTip.SetTip(_label, summary.Headline);
        }
        catch (Exception)
        {
            // Best-effort: the badge keeps its last-known state and the next trigger tries again.
            if (token == _loadToken)
            {
                IsVisible = false;
            }
        }
    }
}
