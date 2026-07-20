using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// The intent handlers plugins register for actions addressed to them (AC-95), and the lookup a caller goes through
/// to reach one. The host holds them for the same reason it holds the workflow steps: the two plugins involved cannot
/// see each other — the tracker that offers "Start in Autopilot" must not reference the Autopilot plugin, and
/// Autopilot need not know the tracker exists. Both know the host, addressing each other by manifest id and an agreed
/// action string, and the host knows nothing about either.
/// </summary>
public interface IPluginIntentRegistry
{
    /// <summary>
    /// Registers <paramref name="handler"/> as <paramref name="ownerPluginId"/>'s handler for
    /// <paramref name="action"/>. Throws when that plugin already registered the same action — a second handler for
    /// one (owner, action) would make which of them runs a question of load order, the same reason
    /// <see cref="IWorkflowStepRegistry"/> refuses a duplicate type id.
    /// </summary>
    void Register(string ownerPluginId, string action, Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>> handler);

    /// <summary>
    /// Whether <paramref name="targetPluginId"/> has a handler for <paramref name="action"/> — the presence check a
    /// caller makes before showing a menu item that would otherwise dispatch to nobody.
    /// </summary>
    bool HasHandler(string targetPluginId, string action);

    /// <summary>
    /// Invokes the handler for <c>(<see cref="PluginIntent.TargetPluginId"/>, <see cref="PluginIntent.Action"/>)</c>
    /// and returns its result, or <see langword="null"/> when no handler is registered. Absence is normal here — the
    /// target plugin may simply not be installed — so it is a null return rather than the thrown error the workflow
    /// <see cref="IWorkflowStepRegistry.Raise"/> uses.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> Dispatch(PluginIntent intent);
}

internal sealed class PluginIntentRegistry : IPluginIntentRegistry, ISingletonService
{
    // Keyed on (owner plugin id, action). ValueTuple's default string equality is ordinal — the same comparison
    // WorkflowStepRegistry uses for its type ids, and correct here because both sides are the host-stamped FolderId.
    private readonly Dictionary<(string PluginId, string Action), Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>> _handlers = [];

    public void Register(string ownerPluginId, string action, Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>> handler)
    {
        if (!_handlers.TryAdd((ownerPluginId, action), handler))
        {
            throw new InvalidOperationException(
                $"Plugin '{ownerPluginId}' already registered an intent handler for action '{action}'. Each (plugin, action) has one handler.");
        }
    }

    public bool HasHandler(string targetPluginId, string action) =>
        _handlers.ContainsKey((targetPluginId, action));

    public async Task<IReadOnlyDictionary<string, string>?> Dispatch(PluginIntent intent)
    {
        if (!_handlers.TryGetValue((intent.TargetPluginId, intent.Action), out var handler))
        {
            return null;
        }

        return await handler(intent).ConfigureAwait(false);
    }
}
