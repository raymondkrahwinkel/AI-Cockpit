namespace Cockpit.Core.Plugins;

// One work kind as the first-run wizard offers it (AC-511): the `Key` a store index matches
// against in `PluginStoreEntry.WorkKind`, plus how it reads on screen.
//
// `Key`: Matched against a store entry's work kind, case-insensitively.
// `Label`: The chooser's own wording.
// `Description`: One line under the label — what this kind of work involves, not which plugins it ticks.
public sealed record PluginWorkKindOption(string Key, string Label, string Description);
