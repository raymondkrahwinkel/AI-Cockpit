namespace Cockpit.Core.Help;

// Who shipped a set of help articles: the app itself, or one installed plugin. Name and author come from
// the plugin's manifest rather than a second declaration inside the documentation, so a plugin writes its
// own name in exactly one place — the same reason `PluginMetadata.Version` is left to `plugin.json`.
public sealed record HelpOwner(string Id, string Name, string? Author = null)
{
    public const string CoreId = "cockpit";

    public static HelpOwner Core { get; } = new(CoreId, "Cockpit", "Cockpit");

    public bool IsCore => string.Equals(Id, CoreId, StringComparison.OrdinalIgnoreCase);

    // Shown as a badge beside the page title, never as different styling: a page from someone else is as
    // legitimate as ours and reads the same, the operator only needs to be able to see whose it is before
    // he follows its instructions.
    public bool IsThirdParty =>
        !IsCore && !string.Equals(Author, Core.Name, StringComparison.OrdinalIgnoreCase);
}
