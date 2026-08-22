namespace Cockpit.Core.Help;

// AC-1033: why a picture is, or is not, on screen. `BlockedExternal` is its own answer because the reader is
// owed the difference between a forgotten file and a request the app refused to make.
public enum HelpImageOutcome
{
    Embedded,
    BlockedExternal,
    Missing,
}
