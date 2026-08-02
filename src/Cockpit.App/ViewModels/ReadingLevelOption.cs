using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// A selectable session reading level (AC-138): the `ReadingLevel` it carries plus a display
// label and a one-line description, so the profile, New-session and header pickers can all offer the same
// three choices with the same words. The `Value` is the durable identity; the label is UI-only.
public sealed record ReadingLevelOption(ReadingLevel Value, string Label, string Description);
