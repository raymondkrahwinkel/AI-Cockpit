namespace Cockpit.Core.Configuration;

// One setting both machines changed since they last agreed: this machine's value and the carrier's, as JSON text. Null
// is a removed profile or project.
public sealed record SettingsConflict(string Key, string? Mine, string? Theirs, IReadOnlyDictionary<string, string?>? CarrierValues = null);
