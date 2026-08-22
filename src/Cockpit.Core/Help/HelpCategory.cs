namespace Cockpit.Core.Help;

// The four top-level branches of the knowledge base, all four owned by the core. A plugin cannot add one
// and cannot place itself outside `Plugins`: a tree that grows freely is unreadable by the tenth plugin,
// and third-party documentation has no business nesting itself between the app's own pages (AC-1033).
// `ExtendingCockpit` stays separate from `Plugins` because that last one is an inventory of what is
// installed, and "how do I write one" is not an entry in an inventory.
public enum HelpCategory
{
    General,
    System,
    ExtendingCockpit,
    Plugins,
}
