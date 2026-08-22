namespace Cockpit.Core.Help;

// Why a picture in a documentation page is, or is not, on screen. `BlockedExternal` is deliberately its own
// answer rather than a flavour of `Missing`: the reader is owed the difference between "the author forgot to
// ship this" and "this page asked to fetch something from a stranger's server the moment you opened it, and
// the app refused" (AC-1033).
public enum HelpImageOutcome
{
    Embedded,
    BlockedExternal,
    Missing,
}
