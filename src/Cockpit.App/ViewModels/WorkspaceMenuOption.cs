using Material.Icons;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One entry in the workspace strip's "+" menu: a host type (Sessions, Dashboard) or a plugin-registered type,
/// flattened to what the menu needs to draw and create it. A single shape for both, so the menu is one list with
/// one command — host types and plugin types side by side — the way the widget gallery lists its registrations.
/// </summary>
public sealed record WorkspaceMenuOption(string Title, MaterialIconKind Icon, string Description, WorkspaceType Type);
