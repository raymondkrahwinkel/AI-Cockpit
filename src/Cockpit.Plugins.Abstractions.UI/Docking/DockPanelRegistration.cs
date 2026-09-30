using Avalonia.Controls;
using Material.Icons;

namespace Cockpit.Plugins.Abstractions.Docking;

/// <summary>
/// A panel a plugin contributes to the right-hand dock rail (<see cref="ICockpitHost.AddDockPanel"/>).
/// The rail owns width and collapse; this only says what tab to show and what to build behind it.
/// </summary>
/// <param name="Id">
/// A stable, namespaced id (e.g. "github.pull-requests") — persisted as the open panel across a
/// restart, so treat it as an API surface.
/// </param>
/// <param name="Title">
/// Shown as the vertical tab label on the collapsed rail.
/// </param>
/// <param name="IconKind">
/// Shown above the title on the rail tab; required, since the rail has no text fallback.
/// </param>
/// <param name="CreateView">
/// Builds the panel's content, on the UI thread, once each time it is opened.
/// </param>
public sealed record DockPanelRegistration(string Id, string Title, MaterialIconKind IconKind, Func<Control> CreateView);
