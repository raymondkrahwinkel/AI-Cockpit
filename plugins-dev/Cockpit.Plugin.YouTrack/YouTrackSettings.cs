using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The plugin's settings, persisted through the host's per-plugin <see cref="IPluginStorage"/>. YouTrack has
/// no local CLI equivalent to <c>gh</c>, so this plugin is HTTP-only per instance: a list of
/// <see cref="YouTrackInstance"/> (each its own base URL + permanent token + optional default project), so one
/// cockpit can pull issues from several YouTracks (#48). The prompt template dropped on click is shared across
/// every instance.
/// </summary>
internal sealed class YouTrackSettings(IPluginStorage storage)
{
    public List<YouTrackInstance> Instances
    {
        get => _LoadInstances();
        set => storage.Set("instances", value);
    }

    /// <summary>
    /// Which issues the session picker offers, as a YouTrack query. Default <c>#Unresolved</c> — showing issues that
    /// are done is offering work that is over. Anything YouTrack's own search understands works here, so a board that
    /// calls its states something unusual is not a special case: <c>State: {In Progress}</c>, <c>#Unresolved -State: Review</c>.
    /// </summary>
    public string PickerQuery
    {
        get => storage.Get<string>("pickerQuery") is { Length: > 0 } query ? query : "#Unresolved";
        set => storage.Set("pickerQuery", value);
    }

    /// <summary>
    /// How a branch is named for an issue — <c>{id}</c> and <c>{summary}</c>, e.g. <c>{id}-{summary}</c> (the default)
    /// or <c>feature/{id}</c>. A naming convention is a team's business, not this plugin's; what stays this plugin's
    /// business is that the result is a ref git will accept.
    /// </summary>
    public string BranchPattern
    {
        get => storage.Get<string>("branchPattern") is { Length: > 0 } pattern ? pattern : BranchName.DefaultPattern;
        set => storage.Set("branchPattern", value);
    }

    public string Template
    {
        get => storage.Get<string>("template") ?? PromptTemplate.Default;
        set => storage.Set("template", value);
    }

    // Back-compat (#48): before instances were a list, this plugin had exactly one — instanceUrl/token/projectTag
    // stored directly. Migrate that single config into a one-item list on first read instead of a returning
    // user silently losing their configured instance (an empty list, requiring them to notice and re-enter it).
    private List<YouTrackInstance> _LoadInstances()
    {
        var stored = storage.Get<List<YouTrackInstance>>("instances");
        if (stored is { Count: > 0 })
        {
            return stored;
        }

        var legacyInstanceUrl = storage.Get<string>("instanceUrl");
        var legacyToken = storage.Get<string>("token");
        if (string.IsNullOrWhiteSpace(legacyInstanceUrl) && string.IsNullOrWhiteSpace(legacyToken))
        {
            return [];
        }

        var legacyProjectTag = storage.Get<string>("projectTag");
        var migrated = new List<YouTrackInstance>
        {
            new("Default", legacyInstanceUrl ?? string.Empty, legacyToken ?? string.Empty, legacyProjectTag ?? string.Empty),
        };
        storage.Set("instances", migrated);
        return migrated;
    }
}
