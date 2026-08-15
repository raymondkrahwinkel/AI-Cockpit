namespace Cockpit.Core.Plugins;

// One work kind as the first-run wizard offers it (AC-511): `Key` matches a store entry's `Audience` list
// case-insensitively; `Label`/`Description` are the chooser's own wording, not which plugins it ticks.
public sealed record PluginWorkKindOption(string Key, string Label, string Description);
