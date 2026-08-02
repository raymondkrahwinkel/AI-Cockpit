namespace Cockpit.Plugin.PromptLibrary;

// One saved prompt template (#2): a stable `Id` (so edit/delete survive a reload), a display
// `Name`, and the `Body` that gets inserted into the active session. The body may
// contain `{{variable}}` placeholders (see `PromptVariables`) filled in before insertion.
// Persisted as a JSON list in the plugin's per-plugin storage.
public sealed record PromptTemplate(string Id, string Name, string Body);
