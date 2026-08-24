namespace Cockpit.Core.Abstractions.Hotkeys;

// AC-1013: One desktop-wide hotkey the cockpit asks for — Id is stable (GlobalHotkeys holds the ones in use;
// on Linux it is also the compositor's binding identity, so changing it silently orphans the operator's
// binding), Description is the desktop's shortcut-settings label, KeyName is a requested Avalonia Key name.
public sealed record GlobalHotkeyBinding(string Id, string Description, string KeyName);
