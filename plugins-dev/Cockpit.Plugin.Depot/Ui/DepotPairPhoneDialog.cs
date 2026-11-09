using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.UI;
using QRCoder;

namespace Cockpit.Plugin.Depot.UI;

// AC-2224: "Pair phone…" on a connection's row. Shows the QR code with its five-minute countdown, then follows the pairing
// until the phone's handshake held. Closing the dialog discards the code, and with it the secret.
internal sealed class DepotPairPhoneDialog : UserControl
{
    private readonly IPluginUiChannel _channel;
    private readonly string _connectionId;
    private readonly Border _qrFrame;
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Image _qr = new() { Width = 232, Height = 232, Stretch = Stretch.Uniform };
    private readonly TextBlock _countdown = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly Button _link = new() { Content = "Open Account › Mobile app in Depot ↗", Classes = { "Ghost" }, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
    private readonly Button _close = new() { Content = "Cancel", Classes = { "Ghost" } };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _expiresAt;
    private string? _linkUrl;
    private bool _polling;
    private bool _finished;
    private IDisposable? _statusColour;

    public DepotPairPhoneDialog(IPluginUiChannel channel, string connectionId, string connectionName)
    {
        _channel = channel;
        _connectionId = connectionId;
        _qrFrame = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _qr,
            IsVisible = false,
        };
        _qrFrame.Bind(Border.CornerRadiusProperty, this.GetResourceObservable("CockpitControlRadius"));
        _countdown.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        _link.Click += async (_, _) =>
        {
            if (_linkUrl is not null && TopLevel.GetTopLevel(this) is { } top)
            {
                await top.Launcher.LaunchUriAsync(new Uri(_linkUrl)).ConfigureAwait(true);
            }
        };
        _close.Click += (_, _) => (TopLevel.GetTopLevel(this) as Window)?.Close();
        _hint.Text = $"Scan this code with the Cockpit app on your phone to pair it with {connectionName}. Only this Cockpit and that phone can read what they send each other.";
        _hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        var experimentalText = new TextBlock { Text = "Experimental", FontSize = 11, FontWeight = FontWeight.SemiBold };
        experimentalText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitStatusWaitingBrush"));
        var experimental = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = experimentalText,
        };
        experimental.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CockpitStatusWaitingBrush"));
        experimental.Bind(Border.CornerRadiusProperty, this.GetResourceObservable("CockpitControlRadius"));
        ToolTip.SetTip(experimental, "The mobile app is experimental and may change or break between releases.");
        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                _Dock(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _close } }, Dock.Bottom),
                new StackPanel { Spacing = 12, Children = { experimental, _hint, _qrFrame, _countdown, _status, _link } },
            },
        };
        _timer.Tick += async (_, _) => await _TickAsync().ConfigureAwait(true);
        AttachedToVisualTree += async (_, _) => await _StartAsync().ConfigureAwait(true);
        DetachedFromVisualTree += async (_, _) => await _CancelAsync().ConfigureAwait(true);
    }

    // What the answer of PairStart or PairStatus means for the dialog.
    private void _Show(DepotPairingStatus status)
    {
        if (status.Qr is { } text)
        {
            using var data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.M);
            using var stream = new MemoryStream(new PngByteQRCode(data).GetGraphic(8));
            _qr.Source = new Bitmap(stream);
        }

        _expiresAt = status.ExpiresAt ?? _expiresAt;
        _linkUrl = status.Link;
        _link.IsVisible = status.Link is not null;
        var open = status.State is DepotPairingState.Open or DepotPairingState.Waiting;
        if (!open)
        {
            _qr.Source = null;
        }

        _hint.IsVisible = open || status.State == DepotPairingState.None;
        _qrFrame.IsVisible = open && _qr.Source is not null;
        _countdown.IsVisible = open;
        _status.Text = status.State switch
        {
            DepotPairingState.None => "Asking Depot for a pairing code…",
            DepotPairingState.Open => string.Empty,
            DepotPairingState.Waiting => "Waiting for phone…",
            DepotPairingState.Paired => $"Paired: {status.DeviceName}",
            _ => status.Message ?? "Pairing failed.",
        };
        _status.IsVisible = _status.Text.Length > 0;
        _status.FontWeight = status.State == DepotPairingState.Paired ? FontWeight.SemiBold : FontWeight.Normal;
        _statusColour?.Dispose();
        _statusColour = _status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(status.State switch
        {
            DepotPairingState.Paired => "CockpitStatusDoneBrush",
            DepotPairingState.Waiting => "CockpitStatusWaitingBrush",
            DepotPairingState.Expired or DepotPairingState.Failed => "CockpitStatusErrorBrush",
            _ => "CockpitTextPrimaryBrush",
        }));
        _finished = !open && status.State != DepotPairingState.None;
        _close.Content = status.State == DepotPairingState.Paired ? "Done" : _finished ? "Close" : "Cancel";
        _close.Classes.Set("Accent", status.State == DepotPairingState.Paired);
        _close.Classes.Set("Ghost", status.State != DepotPairingState.Paired);
        _UpdateCountdown();
    }

    private async Task _StartAsync()
    {
        _Show(new DepotPairingStatus(DepotPairingState.None));
        try
        {
            _Show(await _InvokeAsync(DepotMobileChannel.PairStart).ConfigureAwait(true));
            if (!_finished)
            {
                _timer.Start();
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Depot: starting a pairing for '{_connectionId}' failed: {exception}");
            _Show(new DepotPairingStatus(DepotPairingState.Failed, "Pairing could not start."));
        }
    }

    private async Task _TickAsync()
    {
        _UpdateCountdown();
        if (_polling)
        {
            return;
        }

        _polling = true;
        try
        {
            _Show(await _InvokeAsync(DepotMobileChannel.PairStatus).ConfigureAwait(true));
            if (_finished)
            {
                _timer.Stop();
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Depot: reading the pairing for '{_connectionId}' failed: {exception}");
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task _CancelAsync()
    {
        _timer.Stop();
        try
        {
            await _channel.InvokeAsync(DepotMobileChannel.PairCancel,
                JsonSerializer.SerializeToElement(new DepotMobileRequest(_connectionId), DepotChannel.Json)).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Depot: discarding the pairing for '{_connectionId}' failed: {exception}");
        }
    }

    private void _UpdateCountdown()
    {
        var left = (_expiresAt ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
        _countdown.Text = left > TimeSpan.Zero ? $"Expires in {(int)left.TotalMinutes}:{left.Seconds:00}" : "Expired";
    }

    private async Task<DepotPairingStatus> _InvokeAsync(string action)
    {
        var answer = await _channel.InvokeAsync(action,
            JsonSerializer.SerializeToElement(new DepotMobileRequest(_connectionId), DepotChannel.Json)).ConfigureAwait(true);
        return answer.Deserialize<DepotPairingStatus>(DepotChannel.Json) ?? new DepotPairingStatus(DepotPairingState.Failed);
    }

    private static Control _Dock(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
