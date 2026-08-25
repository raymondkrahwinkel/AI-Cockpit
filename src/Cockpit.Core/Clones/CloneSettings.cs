namespace Cockpit.Core.Clones;

// Where the cockpit clones repositories from a URL (AC-90). `Root` null or blank keeps the default —
// a `clones/` folder under the app state root — while an operator can override it. Existing clones
// keep the absolute path they were made at, so changing this never strands them (AC-85).
public sealed record CloneSettings
{
    public string? Root { get; init; }
}
