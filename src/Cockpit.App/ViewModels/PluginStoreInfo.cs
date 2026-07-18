using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One configured plugin store as shown in the Manage-stores dialog (#62, AC-7): the store itself plus the
/// display fields — name, icon and plugin count. These are enriched from the store's <c>index.json</c> once it
/// has been browsed (<see cref="PluginManagerViewModel.BrowseStoresAsync"/>), and derived from the location
/// until then, so a freshly added store still reads as "owner/repo" (or a folder name) rather than a raw link
/// before its first fetch.
/// </summary>
public sealed partial class PluginStoreInfo : ObservableObject
{
    /// <summary>The store this row represents — what the Remove command acts on.</summary>
    public PluginStoreConfig Store { get; }

    /// <summary>The store's location — its URL, or a local folder path — shown under the name.</summary>
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

    public PluginStoreInfo(PluginStoreConfig store)
    {
        Store = store;
        Url = store.Location;
        _name = store.IsLocal
            ? _LocalName(store.Location)
            : PluginStoreUrl.DeriveDisplayName(store.Location);
    }

    /// <summary>Whether this is a local-folder store (AC-7) — the row shows a folder badge rather than a link.</summary>
    public bool IsLocal => Store.IsLocal;

    /// <summary>Whether this is a private remote store reached with a token (AC-7) — the row shows a lock badge.</summary>
    public bool IsPrivate => !Store.IsLocal && Store.HasToken;

    // The folder's own name reads better than the full path as a title; the path still shows underneath.
    private static string _LocalName(string path)
    {
        var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);

        return string.IsNullOrWhiteSpace(name) ? path : name;
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
