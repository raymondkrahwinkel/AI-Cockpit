namespace Cockpit.Plugin.PromptLibrary;

/// <summary>
/// One saved prompt template (#2): a stable <see cref="Id"/> (so edit/delete survive a reload), a display
/// <see cref="Name"/>, and the <see cref="Body"/> that gets inserted into the active session. The body may
/// contain <c>{{variable}}</c> placeholders (see <see cref="PromptVariables"/>) filled in before insertion.
/// Persisted as a JSON list in the plugin's per-plugin storage.
/// </summary>
public sealed record PromptTemplate(string Id, string Name, string Body);
