namespace Cockpit.Core.Layout;

// The stand the operator chose in Options -> Appearance (AC-860). This is what gets saved; which variant is on
// screen at the moment is a separate, derived reading — under `System` the two differ whenever the OS flips.
public enum ThemeMode
{
    System,
    Light,
    Dark,
}
