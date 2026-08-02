namespace Cockpit.Plugin.Workflows.Model;

// How the node picker files a type (#69) — the question it answers is "what am I looking for", which is why the
// categories are phrased from the operator's side and not from the code's.
public enum NodeCategory
{
    // What starts a run.
    Trigger,

    // Anything to do with the cockpit's own sessions.
    Sessions,

    // Telling you, or somewhere else, that something happened.
    Notify,

    // Reaching outside the cockpit: a command, an HTTP call.
    External,

    // Branching, waiting, deciding.
    Flow,
}
