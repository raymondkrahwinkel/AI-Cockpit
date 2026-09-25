using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Workflows.Engine;

// AC-1399 (D6): the session a step acts on, named by the step's own Session parameter — a pane id, or a session's
// name, such as the one a Start session step hands on ({Start session.session}). A flow that a timer fires has no
// window and so no session in view: there is no falling back to "the active one", and a step that names none fails.
internal static class SessionTarget
{
    public const string Parameter = "Session";

    // The assistant is listed among the open sessions, but a plugin cannot type into or label it (AC-1392).
    private const string AssistantPaneId = "cockpit-assistant";

    // What the step says, placeholders filled; empty when it says nothing.
    public static string Named(StepContext context) =>
        context.Resolve(context.Node.Parameters.GetValueOrDefault(Parameter)).Text.Trim();

    // What the consent prompt shows: the session's name when the step names an open one by pane id.
    public static string Describe(ICockpitHost host, StepContext context)
    {
        var named = Named(context);
        return host.Sessions.OpenSessions.FirstOrDefault(session => string.Equals(session.PaneId, named, StringComparison.Ordinal))?.Name ?? named;
    }

    public static OpenCockpitSession Resolve(ICockpitHost host, StepContext context)
    {
        var named = Named(context);
        if (named.Length == 0)
        {
            throw new InvalidOperationException(
                "This step names no session — open it and name one: a session's name, its pane id, or {Start session.session} after a step that started one.");
        }

        var open = host.Sessions.OpenSessions.Where(session => session.PaneId != AssistantPaneId).ToList();
        if (open.FirstOrDefault(session => string.Equals(session.PaneId, named, StringComparison.Ordinal)) is { } byPaneId)
        {
            return byPaneId;
        }

        var byName = open.Where(session => string.Equals(session.Name, named, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count switch
        {
            1 => byName[0],
            0 => throw new InvalidOperationException($"No open session is called '{named}'."),
            _ => throw new InvalidOperationException($"{byName.Count} open sessions are called '{named}' — name the one you mean by its pane id."),
        };
    }
}
