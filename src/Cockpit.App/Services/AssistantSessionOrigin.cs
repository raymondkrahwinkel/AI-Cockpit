using Cockpit.App.ViewModels;

namespace Cockpit.App.Services;

// AC-1300: whether the assistant started a session — the one place that decides it, the pattern
// SessionWorkspacePlacement set in AC-543. The stamp is put on at spawn time and persisted with the pane record, so
// the answer outlives the assistant and disappears with the session; the spawn trail is never read to derive it.
internal static class AssistantSessionOrigin
{
    // True when `session` is one the assistant itself started.
    public static bool Resolve(SessionPanelViewModel session)
    {
        // First, and unconditionally — the same exception SessionWorkspacePlacement.Resolve makes: the assistant
        // is a session too, and nothing may put it under itself. Ahead of the stamp check so it holds even if
        // something later stamps one on anyway.
        if (session.BelongsToNoWorkspace)
        {
            return false;
        }

        return session.StartedByTheAssistant;
    }
}
