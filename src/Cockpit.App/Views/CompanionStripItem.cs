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
    bool Inline);
