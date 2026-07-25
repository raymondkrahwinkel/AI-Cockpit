namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// A <see cref="PluginAutopilotTemplate"/> paired with the id of the plugin that registered it (AC-189). The host
/// stamps <see cref="OwnerPluginId"/> from the registering plugin's own identity — never from anything the plugin
/// composes — the same rule <see cref="ICockpitHost.RegisterIntentHandler"/> uses, so a plugin cannot register a
/// template under another's name. The Autopilot plugin reads these back through
/// <see cref="ICockpitHost.RegisteredAutopilotTemplates"/> and needs the owner to attribute each template and to key
/// any operator override to it.
/// </summary>
/// <param name="OwnerPluginId">The manifest id of the plugin that registered the template.</param>
/// <param name="Template">The registered template.</param>
public sealed record RegisteredAutopilotTemplate(
    string OwnerPluginId,
    PluginAutopilotTemplate Template);
