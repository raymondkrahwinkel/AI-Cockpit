using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.Files;
using Material.Icons;
using Material.Icons.Avalonia;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Files;

// Mockup v2 5a-c (AC-1582): the live connection dropped, it came back, or the sign-in expired. One banner in the AC-1755
// composition; the conflict banner takes its place when the offline lines overlapped.
internal sealed partial class MarkdownFileWorkspace
{
    private readonly MaterialIcon _liveIcon = new() { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _liveText = new() { Name = "LiveBannerText", FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _retry = new() { Content = "Retry", Classes = { "Compact" } };
    private readonly Button _signIn = new() { Content = "Sign in again", Classes = { "Compact", "Accent" } };
    private readonly Button _dismiss = new() { Content = "Dismiss", Classes = { "Compact", "Ghost" } };
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private Border? _liveBanner;
    private LiveFileState? _bannerState;
    private DateTime _bannerSince;
    private DateTime _nextTry;
    private int _localCount;
    private int _heldLines;
    private int? _backLive;
    private bool? _bannerDone;

    // Offline or signed out: the text is only here until the source has it again.
    private bool _IsHeld => _IsLiveLight || _live?.Status.State == LiveFileState.SignInNeeded;

    private Border _BuildLiveBanner()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _retry, _signIn, _dismiss } };
        DockPanel.SetDock(actions, Dock.Right);
        DockPanel.SetDock(_liveIcon, Dock.Left);
        _liveBanner = new Border
        {
            Name = "LiveRecoveryBanner", Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 0, 0, 1), IsVisible = false,
            Child = new DockPanel { LastChildFill = true, Children = { actions, _liveIcon, _liveText } },
        };
        _liveText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextPrimaryBrush"));
        _retry.Click += async (_, _) => await _ReconnectFromUiAsync(signIn: false).ConfigureAwait(true);
        _signIn.Click += async (_, _) => await _ReconnectFromUiAsync(signIn: true).ConfigureAwait(true);
        _dismiss.Click += (_, _) =>
        {
            _backLive = null;
            _UpdateState();
        };
        _countdown.Tick += (_, _) => _UpdateLiveBanner();
        DockPanel.SetDock(_liveBanner, Dock.Top);
        return _liveBanner;
    }

    private async Task _ReconnectFromUiAsync(bool signIn)
    {
        try
        {
            if (_live is { } live)
            {
                await live.ReconnectAsync(signIn).ConfigureAwait(true);
            }
            if (!signIn && _IsLiveLight && IsDirty)
            {
                _liveLightTimer.Stop();
                await SaveAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Reconnecting live file workspace {WorkspaceId} failed; its text stays here.", _context.WorkspaceId);
            _error = signIn ? $"Signing in to {_sourceName} did not finish. Your text is still here." : $"{_sourceName} could not be reached. Your text is still here.";
            _UpdateState();
        }
    }

    // Called after _UpdateLiveLight, which counts the lines only held here.
    private void _UpdateLiveBanner()
    {
        if (_liveBanner is null || _closed)
        {
            return;
        }
        var live = _live?.Status;
        var state = _loaded ? live?.State : null;
        var held = state is LiveFileState.Offline or LiveFileState.SignInNeeded;
        if (state != _bannerState)
        {
            if (held && _bannerState is not (LiveFileState.Offline or LiveFileState.SignInNeeded))
            {
                _heldLines = 0;
                _backLive = null;
            }
            else if (state == LiveFileState.Live && _bannerState is LiveFileState.Offline or LiveFileState.SignInNeeded)
            {
                _backLive = _heldLines;
            }
            _bannerState = state;
            _bannerSince = DateTime.Now;
        }
        if (held)
        {
            _heldLines = Math.Max(_heldLines, _localCount);
        }
        var back = state == LiveFileState.Live && _backLive is not null;
        _liveBanner.IsVisible = _theirs is null && (held || back);
        _retry.IsVisible = state == LiveFileState.Offline;
        _signIn.IsVisible = state == LiveFileState.SignInNeeded;
        _dismiss.IsVisible = back && !held;
        _liveIcon.IsVisible = state == LiveFileState.Offline || back;
        _liveIcon.Kind = back ? MaterialIconKind.CheckCircleOutline : MaterialIconKind.CloudOffOutline;
        if (_bannerDone != back)
        {
            _bannerDone = back;
            _liveBanner.Bind(Border.BackgroundProperty, this.GetResourceObservable(back ? "CockpitStatusDoneChipTintBrush" : "CockpitStatusWaitingRowTintBrush"));
            _liveBanner.Bind(Border.BorderBrushProperty, this.GetResourceObservable(back ? "CockpitStatusDoneRingTintBrush" : "CockpitStatusWaitingRingTintBrush"));
            _liveIcon.Bind(MaterialIcon.ForegroundProperty, this.GetResourceObservable(back ? "CockpitStatusDoneBrush" : "CockpitStatusWaitingBrush"));
        }
        _liveText.Inlines?.Clear();
        var at = _bannerSince.ToString("HH:mm");
        if (state == LiveFileState.Offline)
        {
            var next = _liveLightTimer.IsEnabled ? $" Next try in {Math.Max(1, (int)Math.Ceiling((_nextTry - DateTime.Now).TotalSeconds))} s." : "";
            _liveText.Inlines?.Add(new Run("The live connection to the server dropped") { FontWeight = FontWeight.SemiBold });
            _liveText.Inlines?.Add(new Run($" at {at}. Keep writing: your text stays in this Cockpit and is merged into the note when {_sourceName} is back.{next}"));
        }
        else if (state == LiveFileState.SignInNeeded && live?.Reason is { Length: > 0 } reason)
        {
            _liveText.Inlines?.Add(new Run(reason));
        }
        else if (state == LiveFileState.SignInNeeded)
        {
            var account = live?.Account is { Length: > 0 } name ? $" as {name}" : "";
            _liveText.Inlines?.Add(new Run($"Your {_sourceName} sign-in has expired") { FontWeight = FontWeight.SemiBold });
            _liveText.Inlines?.Add(new Run($" — {_sourceName} stopped the live session at {at}. Nothing was lost; your text is still here. Sign in again{account} to rejoin the note."));
        }
        else if (back && _backLive is { } lines)
        {
            _liveText.Inlines?.Add(new Run("Back live.") { FontWeight = FontWeight.SemiBold });
            if (lines > 0)
            {
                _liveText.Inlines?.Add(new Run(lines == 1 ? " Your 1 offline line was merged into the note; nothing overlapped."
                    : $" Your {lines} offline lines were merged into the note; nothing overlapped."));
            }
        }
        if (_liveBanner.IsVisible && state == LiveFileState.Offline && _liveLightTimer.IsEnabled)
        {
            _countdown.Start();
        }
        else
        {
            _countdown.Stop();
        }
    }
}
