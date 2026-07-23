using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// The host-owned registry of Autopilot goal/brief templates plugins contribute (AC-189). The host holds them for the
/// same reason it holds the intent handlers and workflow templates: the plugins involved need not see each other — a
/// plugin that ships a brief must not reference the Autopilot plugin, and Autopilot need not know that plugin exists.
/// Both know the host; the host stamps each registration with the contributing plugin's own id. Registrations live
/// only in memory: a plugin re-registers on every start, so nothing here is persisted.
/// </summary>
public interface IAutopilotTemplateRegistry
{
    /// <summary>
    /// Records <paramref name="template"/> as <paramref name="ownerPluginId"/>'s registration. A plugin re-registering
    /// the same template id (a later start, a reload) replaces its earlier entry rather than doubling it.
    /// </summary>
    void Register(string ownerPluginId, PluginAutopilotTemplate template);

    /// <summary>Every registration, each carrying the id of the plugin that contributed it — what the Autopilot plugin reads to build its template picker.</summary>
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
