using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Material.Icons;

namespace Cockpit.App.Views;

// One tool on the strip: an icon button that opens its view in a pop-out, or, when Inline, the view itself.
internal sealed record CompanionStripItem(
    string Id,
    string Title,
    string Tooltip,
    string Glyph,
    MaterialIconKind? IconKind,
    SideMenuButtonBadge? Badge,
    Func<Control> CreateView,
    bool Inline, Func<Control>? CreateStripContent = null,
    // AC-2190: the view draws its own pop-out head (status, notifications); others get the shared one with the title.
    bool OwnHead = false);
