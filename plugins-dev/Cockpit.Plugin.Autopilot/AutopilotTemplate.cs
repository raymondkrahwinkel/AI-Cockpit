using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// A goal/brief template the operator can start an Autopilot run from (AC-189). The `Body` is the brief
// text, with optional `{{placeholder}}` tokens that `AutopilotTemplateResolver` fills in from the
// triggering issue and the operator's input at run time. Its `Origin` decides the edit/delete rules:
// Builtin and Plugin templates are editable (an edit is kept as an override on the original registration) but not
// deletable; User templates the operator authored are both. This is the Autopilot plugin's own richer view;
// `PluginAutopilotTemplate` is the leaner thing a plugin registers.
//
// `Id`: Stable identity, so an override or a user edit is keyed to it across restarts.
// `Name`: What the template picker shows.
// `Body`: The goal/brief text, with optional `{{placeholder}}` tokens.
// `Origin`: Where it came from — which fixes `Editable` and `Deletable`.
// `OwnerPluginId`: The id of the plugin that contributed it (Plugin origin only); null otherwise.
// `Editable`: Whether the operator may edit it. True for every origin.
// `Deletable`: Whether the operator may delete it. True only for User templates.
// `RequiredPlaceholders`: The placeholder names the brief cannot do without, so the surface can warn before a run starts with one unfilled. Optional.
// `DeliversPullRequest`: Whether a run from this template is a code run that ends with a merge-ready pull request (AC-216) — carried from the plugin's `PluginAutopilotTemplate.DeliversPullRequest`. False for an administrative template (no PR expected).
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
