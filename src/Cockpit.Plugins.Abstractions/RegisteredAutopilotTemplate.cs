namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// A <see cref="PluginAutopilotTemplate"/> paired with the id of the plugin that registered it (AC-189). The
/// Autopilot plugin reads these back through <see cref="ICockpitHost.RegisteredAutopilotTemplates"/>.
/// </summary>
/// <remarks>
/// The host stamps <see cref="OwnerPluginId"/> from the registering plugin's own identity — a plugin cannot
/// register a template under another's name.
/// </remarks>
/// <param name="OwnerPluginId">
/// The manifest id of the plugin that registered the template.
/// </param>
/// <param name="Template">
/// The registered template.
/// </param>
public sealed record RegisteredAutopilotTemplate(
    string OwnerPluginId,
    PluginAutopilotTemplate Template);
