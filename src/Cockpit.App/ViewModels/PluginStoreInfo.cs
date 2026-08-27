using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

// These are enriched from the store's `index.json` once it has been browsed
// (`PluginManagerViewModel.BrowseStoresAsync`), and derived from the location until then, so a freshly added store
// still reads as "owner/repo" (or a folder name) rather than a raw link before its first fetch (#62, AC-7).
public sealed partial class PluginStoreInfo : ObservableObject
{
    // The store this row represents — what the Remove command acts on.
    public PluginStoreConfig Store { get; }

    // The store's location — its URL, or a local folder path — shown under the name.
    public string Url { get; }

    // The store's display name — its `index.json` name once browsed, else derived from the URL.
    [ObservableProperty]
    private string _name;

    // The store's icon glyph from its `index.json`, or null until browsed / when it sets none.
    [ObservableProperty]
    private string? _icon;

    // How many plugins the store advertises — 0 until it has been browsed.
    [ObservableProperty]
    private int _pluginCount;

    // False once a browse could not reach the store, so the row can say so instead of showing a stale count.
    [ObservableProperty]
    private bool _isReachable = true;

    // True once the store has been browsed at least once — until then the count line stays quiet rather than claiming "No plugins yet".
    [ObservableProperty]
    private bool _isBrowsed;

    // The store's real logo image once fetched from its `index.json` `iconUrl`, or null — the row then falls back to the store's own glyph (`ShowIconGlyph`) or a default icon (`ShowDefaultIcon`).
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

    // Whether this is a local-folder store (AC-7) — the row shows a folder badge rather than a link.
    public bool IsLocal => Store.IsLocal;

    // Whether this is a private remote store reached with a token (AC-7) — the row shows a lock badge.
    public bool IsPrivate => !Store.IsLocal && Store.HasToken;

    // The folder's own name reads better than the full path as a title; the path still shows underneath.
    private static string _LocalName(string path)
    {
        var trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);

        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    // Whether a real logo image has been fetched — the row shows it in place of the glyph.
    public bool HasLogo => Logo is not null;

    // Whether the store advertised its own icon glyph — the row shows it instead of the default storefront icon.
    public bool HasCustomIcon => !string.IsNullOrWhiteSpace(Icon);

    // Shows the store's own icon glyph — true only once no logo has loaded and the store declared its own icon.
    public bool ShowIconGlyph => !HasLogo && HasCustomIcon;

    // Shows the default storefront icon — true only once neither a logo nor the store's own icon is available.
    public bool ShowDefaultIcon => !HasLogo && !HasCustomIcon;

    // The count line under the name — quiet until browsed, then the plugin count or an unreachable note.
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
