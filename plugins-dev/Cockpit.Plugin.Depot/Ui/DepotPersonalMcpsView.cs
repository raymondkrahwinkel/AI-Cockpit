using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Depot.UI;

// One list of personal MCPs for the settings and the first start; Authorize opens Depot, and the backend part waits for the result.
internal sealed class DepotPersonalMcpsView : UserControl
{
    private readonly IPluginUiChannel _channel;
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly TextBlock _message = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private DepotPersonalList? _state;
    private bool _failed;
    private IDisposable? _subscription;

    // The first start reads the list itself, to know whether the step is needed, so it loads on attach only when asked to.
    public DepotPersonalMcpsView(IPluginUiChannel channel, bool loadOnAttach = true)
    {
        _channel = channel;
        _message.Bind(TextBlock.ForegroundProperty, _message.GetResourceObservable("CockpitStatusErrorBrush"));
        var intro = new TextBlock { Text = "Your organization connected these. Each one needs your own sign-in before your agents can use it.", FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var fine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new MaterialIcon { Kind = MaterialIconKind.ShieldOutline, Width = 13, Height = 13, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "Your browser opens. Depot keeps the sign-in; Cockpit never sees it.", FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
            },
        };
        Content = new StackPanel { Spacing = 8, Children = { intro, _list, _message, fine } };
        AttachedToVisualTree += (_, _) =>
        {
            _subscription = _channel.Subscribe(DepotPersonalChannel.Changed, evt => Dispatcher.UIThread.Post(() => { _ = LoadAsync(refresh: false); }));
            if (loadOnAttach)
            {
                _ = LoadAsync(refresh: true);
            }
        };
        DetachedFromVisualTree += (_, _) => _subscription?.Dispose();
    }

    // True once a read has told that something is open, for a host that hides the step when nothing is.
    internal bool HasOpen => _state?.Connections.Any(connection => connection.Mcps.Any(mcp => DepotPersonalStatus.NeedsAuthorization(mcp.Status))) == true;

    public async Task LoadAsync(bool refresh)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new DepotPersonalListRequest(refresh), DepotChannel.Json);
            _state = (await _channel.InvokeAsync(DepotPersonalChannel.List, payload).ConfigureAwait(true)).Deserialize<DepotPersonalList>(DepotChannel.Json);
            _failed = _state is null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _failed = true;
        }

        _Render();
    }

    // Asks the backend part to wait, then opens the address it gives. Null when it worked, else the sentence for the operator.
    internal static async Task<string?> AuthorizeAsync(IPluginUiChannel channel, Func<Uri, Task> launch, DepotPersonalTarget target)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(target, DepotChannel.Json);
            var answer = (await channel.InvokeAsync(DepotPersonalChannel.Authorize, payload).ConfigureAwait(true)).Deserialize<DepotPersonalAuthorizeAnswer>(DepotChannel.Json);
            if (answer?.Url is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var address))
            {
                return answer?.Message ?? "Depot gave no address to open.";
            }

            try
            {
                await launch(address).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await channel.InvokeAsync(DepotPersonalChannel.Cancel, JsonSerializer.SerializeToElement(true, DepotChannel.Json)).ConfigureAwait(true);
                return $"The browser could not be opened: {exception.Message}";
            }

            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"Authorizing failed: {exception.Message}";
        }
    }

    private async Task _AuthorizeAsync(DepotPersonalTarget target)
    {
        var failure = await AuthorizeAsync(_channel, uri => DepotUi.LaunchAsync(uri, this), target).ConfigureAwait(true);
        _Say(failure);
        await LoadAsync(refresh: false).ConfigureAwait(true);
    }

    private async Task _OpenAgainAsync(Uri address)
    {
        try
        {
            await DepotUi.LaunchAsync(address, this).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _Say($"The browser could not be opened: {exception.Message}");
        }
    }

    private async Task _CancelAsync()
    {
        try
        {
            await _channel.InvokeAsync(DepotPersonalChannel.Cancel, JsonSerializer.SerializeToElement(true, DepotChannel.Json)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _Say($"Cancelling failed: {exception.Message}");
        }
    }

    private void _Say(string? text)
    {
        _message.Text = text;
        _message.IsVisible = !string.IsNullOrEmpty(text);
    }

    private void _Render()
    {
        _list.Children.Clear();
        if (_failed || _state is null)
        {
            var retry = new Button { Content = "Try again", Classes = { "Compact" } };
            retry.Click += async (_, _) => await LoadAsync(refresh: true).ConfigureAwait(true);
            _list.Children.Add(_Card(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Couldn't load this list from Depot", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "What you authorized earlier keeps working.", FontSize = 11, Opacity = 0.7 },
                    retry,
                },
            }));
            return;
        }

        var several = _state.Connections.Count(connection => connection.Mcps.Count > 0) > 1;
        var accent = true;
        foreach (var connection in _state.Connections.Where(connection => connection.Mcps.Count > 0))
        {
            if (several)
            {
                _list.Children.Add(new TextBlock { Text = connection.ConnectionName, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) });
            }

            foreach (var mcp in connection.Mcps)
            {
                _list.Children.Add(_Row(connection.ConnectionId, mcp, ref accent));
            }
        }

        if (_list.Children.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "Nothing to authorize. Your organization has no personal MCPs.", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        }
    }

    // One primary button in the list (the first one that can be pressed), none while a browser is being waited for.
    private Border _Row(string connectionId, DepotPersonalMcp mcp, ref bool accent)
    {
        var waiting = _state?.WaitingId == mcp.Id;
        var busy = _state?.WaitingId is not null;
        var target = new DepotPersonalTarget(connectionId, mcp.Id);
        var (status, brush) = waiting
            ? ("Waiting for your browser", "CockpitStatusWaitingBrush")
            : mcp.Status switch
            {
                DepotPersonalStatus.Active => ("Authorized", "CockpitStatusDoneBrush"),
                DepotPersonalStatus.Expired => ("Authorization expired", "CockpitStatusErrorBrush"),
                DepotPersonalStatus.Unreachable => ("Unreachable. Nothing to do on your side.", "CockpitTextSecondaryBrush"),
                _ => ("Not authorized yet", "CockpitTextSecondaryBrush"),
            };
        var statusText = new TextBlock { Text = status, FontSize = 11 };
        statusText.Bind(TextBlock.ForegroundProperty, statusText.GetResourceObservable(brush));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (waiting)
        {
            var again = new Button { Content = "Open again", Classes = { "Compact" } };
            again.Click += async (_, _) => await _OpenAgainAsync(new Uri(mcp.AuthorizeUrl)).ConfigureAwait(true);
            var cancel = new Button { Content = "Cancel", Classes = { "Compact", "Ghost" } };
            cancel.Click += async (_, _) => await _CancelAsync().ConfigureAwait(true);
            buttons.Children.Add(again);
            buttons.Children.Add(cancel);
        }
        else if (DepotPersonalStatus.NeedsAuthorization(mcp.Status))
        {
            var authorize = new Button
            {
                Content = mcp.Status == DepotPersonalStatus.Expired ? "Reauthorize" : "Authorize",
                IsEnabled = !busy,
                Classes = { "Compact" },
            };
            if (accent && !busy)
            {
                authorize.Classes.Add("Accent");
                accent = false;
            }

            authorize.Click += async (_, _) => await _AuthorizeAsync(target).ConfigureAwait(true);
            buttons.Children.Add(authorize);
        }

        var icon = new MaterialIcon { Kind = MaterialIconKind.KeyVariant, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = mcp.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, statusText },
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10, Children = { icon, text, buttons } };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(buttons, 2);
        return _Card(grid);
    }

    private Border _Card(Control child)
    {
        var card = new Border { Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Child = child };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("CockpitSecondaryBgBrush"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("CockpitHairlineBrush"));
        return card;
    }
}
