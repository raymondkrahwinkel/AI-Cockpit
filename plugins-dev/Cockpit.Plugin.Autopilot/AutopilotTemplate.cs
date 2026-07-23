using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// A goal/brief template the operator can start an Autopilot run from (AC-189). The <see cref="Body"/> is the brief
/// text, with optional <c>{{placeholder}}</c> tokens that <see cref="AutopilotTemplateResolver"/> fills in from the
/// triggering issue and the operator's input at run time. Its <see cref="Origin"/> decides the edit/delete rules:
/// Builtin and Plugin templates are editable (an edit is kept as an override on the original registration) but not
/// deletable; User templates the operator authored are both. This is the Autopilot plugin's own richer view;
/// <see cref="PluginAutopilotTemplate"/> is the leaner thing a plugin registers.
/// </summary>
/// <param name="Id">Stable identity, so an override or a user edit is keyed to it across restarts.</param>
/// <param name="Name">What the template picker shows.</param>
/// <param name="Body">The goal/brief text, with optional <c>{{placeholder}}</c> tokens.</param>
/// <param name="Origin">Where it came from — which fixes <see cref="Editable"/> and <see cref="Deletable"/>.</param>
/// <param name="OwnerPluginId">The id of the plugin that contributed it (Plugin origin only); null otherwise.</param>
/// <param name="Editable">Whether the operator may edit it. True for every origin.</param>
/// <param name="Deletable">Whether the operator may delete it. True only for User templates.</param>
/// <param name="RequiredPlaceholders">The placeholder names the brief cannot do without, so the surface can warn before a run starts with one unfilled. Optional.</param>
internal sealed record AutopilotTemplate(
    string Id,
    string Name,
    string Body,
    AutopilotTemplateOrigin Origin,
    string? OwnerPluginId,
    bool Editable,
    bool Deletable,
    IReadOnlyList<string>? RequiredPlaceholders = null)
{
    /// <summary>A plugin's registration as a template: editable (the edit is kept as an override), never deletable, attributed to its owner.</summary>
    public static AutopilotTemplate ForPlugin(string ownerPluginId, PluginAutopilotTemplate registration) => new(
        registration.Id,
        registration.Name,
        registration.Body,
        AutopilotTemplateOrigin.Plugin,
        ownerPluginId,
        Editable: true,
        Deletable: false,
        registration.RequiredPlaceholders);

    /// <summary>A template the operator authored: theirs to edit and to delete.</summary>
    public static AutopilotTemplate ForUser(string id, string name, string body, IReadOnlyList<string>? requiredPlaceholders = null) => new(
        id,
        name,
        body,
        AutopilotTemplateOrigin.User,
        OwnerPluginId: null,
        Editable: true,
        Deletable: true,
        requiredPlaceholders);
}
