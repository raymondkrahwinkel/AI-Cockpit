using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One configured plugin store as shown in the Manage-stores dialog (#62): its URL plus the display
/// fields — name, icon and plugin count. These are enriched from the store's <c>index.json</c> once it has
/// been browsed (<see cref="PluginManagerViewModel.BrowseStoresAsync"/>), and derived from the URL until
/// then, so a freshly added store still reads as "owner/repo" rather than a raw link before its first fetch.
/// </summary>
public sealed partial class PluginStoreInfo : ObservableObject
{
    public string Url { get; }

    /// <summary>The store's display name — its <c>index.json</c> name once browsed, else derived from the URL.</summary>
    [ObservableProperty]
    private string _name;

    /// <summary>The store's icon glyph from its <c>index.json</c>, or null until browsed / when it sets none.</summary>
    [ObservableProperty]
    private string? _icon;

    /// <summary>How many plugins the store advertises — 0 until it has been browsed.</summary>
    [ObservableProperty]
    private int _pluginCount;

    /// <summary>False once a browse could not reach the store, so the row can say so instead of showing a stale count.</summary>
    [ObservableProperty]
    private bool _isReachable = true;

    /// <summary>True once the store has been browsed at least once — until then the count line stays quiet rather than claiming "No plugins yet".</summary>
    [ObservableProperty]
    private bool _isBrowsed;

    /// <summary>The store's real logo image once fetched from its <c>index.json</c> <c>iconUrl</c>, or null — the row then falls back to the store's own glyph (<see cref="ShowIconGlyph"/>) or a default icon (<see cref="ShowDefaultIcon"/>).</summary>
    [ObservableProperty]
    private Bitmap? _logo;

    public PluginStoreInfo(string url)
    {
        Url = url;
        _name = PluginStoreUrl.DeriveDisplayName(url);
    }

    /// <summary>Whether a real logo image has been fetched — the row shows it in place of the glyph.</summary>
    public bool HasLogo => Logo is not null;

    /// <summary>Whether the store advertised its own icon glyph — the row shows it instead of the default storefront icon.</summary>
    public bool HasCustomIcon => !string.IsNullOrWhiteSpace(Icon);

    /// <summary>Shows the store's own icon glyph — true only once no logo has loaded and the store declared its own icon.</summary>
    public bool ShowIconGlyph => !HasLogo && HasCustomIcon;

    /// <summary>Shows the default storefront icon — true only once neither a logo nor the store's own icon is available.</summary>
    public bool ShowDefaultIcon => !HasLogo && !HasCustomIcon;

    /// <summary>The count line under the name — quiet until browsed, then the plugin count or an unreachable note.</summary>
    public string CountText => !IsBrowsed
        ? "Not browsed yet"
        : !IsReachable
            ? "Unreachable"
            : PluginCount switch
            {
                0 => "No plugins",
                1 => "1 plugin",
                _ => $"{PluginCount} plugins",
            };

    partial void OnIconChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCustomIcon));
        OnPropertyChanged(nameof(ShowIconGlyph));
        OnPropertyChanged(nameof(ShowDefaultIcon));
    }

    partial void OnLogoChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasLogo));
        OnPropertyChanged(nameof(ShowIconGlyph));
        OnPropertyChanged(nameof(ShowDefaultIcon));
    }

    partial void OnPluginCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    partial void OnIsReachableChanged(bool value) => OnPropertyChanged(nameof(CountText));

    partial void OnIsBrowsedChanged(bool value) => OnPropertyChanged(nameof(CountText));
}
