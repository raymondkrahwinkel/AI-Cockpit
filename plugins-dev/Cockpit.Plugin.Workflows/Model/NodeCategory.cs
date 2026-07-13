namespace Cockpit.Plugin.Workflows.Model;

/// <summary>
/// How the node picker files a type (#69) — the question it answers is "what am I looking for", which is why the
/// categories are phrased from the operator's side and not from the code's.
/// </summary>
public enum NodeCategory
{
    /// <summary>What starts a run.</summary>
    Trigger,

    /// <summary>Anything to do with the cockpit's own sessions.</summary>
    Sessions,

    /// <summary>Telling you, or somewhere else, that something happened.</summary>
    Notify,

    /// <summary>Reaching outside the cockpit: a command, an HTTP call.</summary>
    External,

    /// <summary>Branching, waiting, deciding.</summary>
    Flow,
}
