using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// A goal/brief template the operator can start an Autopilot run from (AC-189). `Body` is the brief text with
// optional `{{placeholder}}` tokens `AutopilotTemplateResolver` fills at run time. `Origin` fixes
// `Editable`/`Deletable`: Builtin/Plugin are editable but not deletable; User templates are both.
internal sealed record AutopilotTemplate(
    string Id,
    string Name,
    string Body,
    AutopilotTemplateOrigin Origin,
    string? OwnerPluginId,
    bool Editable,
    bool Deletable,
    IReadOnlyList<string>? RequiredPlaceholders = null,
    bool DeliversPullRequest = false)
{
    // A plugin's registration as a template: editable (the edit is kept as an override), never deletable, attributed to its owner. Carries the plugin's PR-delivery signal (AC-216) through unchanged.
    public static AutopilotTemplate ForPlugin(string ownerPluginId, PluginAutopilotTemplate registration) => new(
        registration.Id,
        registration.Name,
        registration.Body,
        AutopilotTemplateOrigin.Plugin,
        ownerPluginId,
        Editable: true,
        Deletable: false,
        registration.RequiredPlaceholders,
        registration.DeliversPullRequest);

    // A template the operator authored: theirs to edit and to delete. An operator template is administrative (no PR expectation) unless a future editor lets them opt in.
    public static AutopilotTemplate ForUser(string id, string name, string body, IReadOnlyList<string>? requiredPlaceholders = null, bool deliversPullRequest = false) => new(
        id,
        name,
        body,
        AutopilotTemplateOrigin.User,
        OwnerPluginId: null,
        Editable: true,
        Deletable: true,
        requiredPlaceholders,
        deliversPullRequest);
}
