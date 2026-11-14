using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Matomo.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Matomo.UI;

internal sealed class MatomoSettingsControl : UserControl, IPluginSettingsView
{
    private readonly IPluginStorage _storage;
    private readonly IPluginUiChannel _channel;
    private readonly CheckBox _mcp;
    private readonly TextBox _url = new() { PlaceholderText = "e.g. https://matomo.example.com" };
    private readonly TextBox _token = new() { PasswordChar = '•', PlaceholderText = "Leave blank to keep the stored token" };
    private readonly TextBlock _status = new() { Classes = { "optionsHint" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _siteRows = new() { Spacing = 4 };
    private readonly List<MatomoSite> _sites;
    private readonly HashSet<int> _offered;
    private int? _default;

    public MatomoSettingsControl(IPluginStorage storage, IPluginUiChannel channel, MatomoTestAnswer? shown = null)
    {
        _storage = storage;
        _channel = channel;
        _sites = storage.Get<List<MatomoSite>>(MatomoKeys.OfferedSites) ?? [];
        _offered = _sites.Select(site => site.Id).ToHashSet();
        _default = storage.Get<int?>(MatomoKeys.DefaultSite);
        _mcp = new CheckBox { Content = "Offer the cockpit-matomo MCP server to sessions", IsChecked = storage.Get<bool?>(MatomoKeys.McpEnabled) ?? false };
        _url.Text = storage.Get<string>(MatomoKeys.Url) ?? string.Empty;

        var test = new Button { Content = "Test connection" };
        test.Click += async (_, _) => await _TestAsync(test);

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _mcp,
                _Field("Matomo URL", _url),
                _Field("Auth token", _token),
                new TextBlock { Text = "Use a token of a Matomo user with view access only, and turn on \"Only allow secure requests\" for it.", Opacity = 0.7, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                test,
                _status,
                new TextBlock { Text = "Sites agents may read", FontWeight = FontWeight.SemiBold },
                _siteRows,
            },
        };
        _RenderSites();
        if (shown is not null)
        {
            _Show(shown);
        }
    }

    public bool TryStage(out Action? commit, out string? error)
    {
        var url = (_url.Text ?? string.Empty).Trim();
        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            commit = null;
            error = "The Matomo URL must start with http:// or https://.";
            return false;
        }

        commit = () =>
        {
            var storage = _storage;
            storage.Set(MatomoKeys.Url, url);
            if (!string.IsNullOrWhiteSpace(_token.Text))
            {
                storage.SetSecret(MatomoKeys.AuthToken, _token.Text);
            }

            storage.Set(MatomoKeys.McpEnabled, _mcp.IsChecked ?? false);
            storage.Set(MatomoKeys.OfferedSites, _sites.Where(site => _offered.Contains(site.Id)).ToList());
            storage.Set(MatomoKeys.DefaultSite, _default is { } id && _offered.Contains(id) ? id : (int?)null);
        };
        error = null;
        return true;
    }

    private async Task _TestAsync(Button trigger)
    {
        trigger.IsEnabled = false;
        try
        {
            var request = new MatomoTestRequest((_url.Text ?? string.Empty).Trim(), _token.Text);
            var answer = (await _channel.InvokeAsync(MatomoChannel.TestConnection, JsonSerializer.SerializeToElement(request, MatomoChannel.Json)))
                .Deserialize<MatomoTestAnswer>(MatomoChannel.Json);
            _Show(answer ?? new MatomoTestAnswer(MatomoStatus.Unreachable, "No answer from the plugin.", null, []));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _status.Text = $"The test failed ({ex.GetType().Name}).";
            _status.Classes.Set("error", true);
            _status.IsVisible = true;
        }
        finally
        {
            trigger.IsEnabled = true;
        }
    }

    private void _Show(MatomoTestAnswer answer)
    {
        _status.Text = answer.Message;
        _status.Classes.Set("error", answer.Status is MatomoStatus.TokenRejected or MatomoStatus.Unreachable);
        _status.IsVisible = true;
        if (answer.Status == MatomoStatus.Connected)
        {
            _sites.Clear();
            _sites.AddRange(answer.Sites);
            _offered.IntersectWith(_sites.Select(site => site.Id));
            _RenderSites();
        }
    }

    private void _RenderSites()
    {
        _siteRows.Children.Clear();
        if (_sites.Count == 0)
        {
            _siteRows.Children.Add(new TextBlock { Text = "Run Test connection to list the sites.", Opacity = 0.7, FontSize = 11 });
            return;
        }

        foreach (var site in _sites)
        {
            var offered = new CheckBox { Content = $"{site.Name} (id {site.Id})", IsChecked = _offered.Contains(site.Id) };
            var isDefault = new RadioButton { Content = "default", GroupName = "matomoDefaultSite", IsChecked = _default == site.Id, Margin = new(12, 0, 0, 0) };
            offered.IsCheckedChanged += (_, _) =>
            {
                if (offered.IsChecked == true) { _offered.Add(site.Id); } else { _offered.Remove(site.Id); }
            };
            isDefault.IsCheckedChanged += (_, _) =>
            {
                if (isDefault.IsChecked == true)
                {
                    _default = site.Id;
                    offered.IsChecked = true;
                }
            };
            _siteRows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { offered, isDefault } });
        }
    }

    private static StackPanel _Field(string label, Control field) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 }, field },
    };
}
