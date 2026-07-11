using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The plugin's settings, persisted through the host's per-plugin <see cref="IPluginStorage"/>. YouTrack has
/// no local CLI equivalent to <c>gh</c>, so this plugin is HTTP-only: an instance base URL (left empty until
/// configured — no default instance, since there is no universal one), a permanent token (runtime-configured
/// — never hardcoded), the project short-name to track, and an optional extra query filter appended to the
/// open-issues search. The prompt template dropped on click is editable too.
/// </summary>
internal sealed class YouTrackSettings(IPluginStorage storage)
{
    public string InstanceUrl
    {
        get => storage.Get<string>("instanceUrl") ?? string.Empty;
        set => storage.Set("instanceUrl", value);
    }

    public string Token
    {
        get => storage.Get<string>("token") ?? string.Empty;
        set => storage.Set("token", value);
    }

    public string ProjectTag
    {
        get => storage.Get<string>("projectTag") ?? string.Empty;
        set => storage.Set("projectTag", value);
    }

    public string ExtraQuery
    {
        get => storage.Get<string>("extraQuery") ?? string.Empty;
        set => storage.Set("extraQuery", value);
    }

    public string Template
    {
        get => storage.Get<string>("template") ?? PromptTemplate.Default;
        set => storage.Set("template", value);
    }
}
