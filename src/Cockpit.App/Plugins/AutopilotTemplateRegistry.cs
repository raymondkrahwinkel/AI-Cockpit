using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// The host-owned registry of Autopilot goal/brief templates plugins contribute (AC-189) — same reason as the intent
/// handlers and workflow templates: the plugins involved need not see each other, only the host, which stamps each
/// registration with the contributing plugin's id. In-memory only; a plugin re-registers on every start.
/// </summary>
public interface IAutopilotTemplateRegistry
{
    /// <summary>
    /// Records <paramref name="template"/> as <paramref name="ownerPluginId"/>'s registration. A plugin re-registering
    /// the same template id (a later start, a reload) replaces its earlier entry rather than doubling it.
    /// </summary>
    void Register(string ownerPluginId, PluginAutopilotTemplate template);

    /// <summary>
    /// Every registration, each carrying the id of the plugin that contributed it — what the Autopilot plugin reads to build its template picker.
    /// </summary>
    IReadOnlyList<RegisteredAutopilotTemplate> Registrations { get; }
}

internal sealed class AutopilotTemplateRegistry : IAutopilotTemplateRegistry, ISingletonService
{
    // Keyed on (owner plugin id, template id): the same plugin re-registering one template replaces it, and two
    // plugins may ship a template with the same id without colliding. Both sides are host-stamped ids, so ordinal
    // string equality (ValueTuple's default) is the right comparison — the same choice PluginIntentRegistry makes.
    private readonly Dictionary<(string PluginId, string TemplateId), RegisteredAutopilotTemplate> _registrations = [];

    public void Register(string ownerPluginId, PluginAutopilotTemplate template) =>
        _registrations[(ownerPluginId, template.Id)] = new RegisteredAutopilotTemplate(ownerPluginId, template);

    public IReadOnlyList<RegisteredAutopilotTemplate> Registrations => [.. _registrations.Values];
}
