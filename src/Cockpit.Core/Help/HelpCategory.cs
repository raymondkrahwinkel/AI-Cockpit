namespace Cockpit.Core.Help;

// AC-1033: the four top-level branches, all owned by the core — a plugin can neither add one nor place
// itself outside `Plugins`.
public enum HelpCategory
{
    General,
    System,
    ExtendingCockpit,
    Plugins,
}
