namespace Cockpit.App.ViewModels;

/// <summary>
/// Coarse-grained lifecycle/attention state for a single <see cref="ClaudeSessionViewModel"/>,
/// derived from the session events it already receives. Drives the sidebar status-dot and the
/// "needs attention" affordance — see <c>Memory/Cockpit/Plan.md</c> §UX-eisen.
/// </summary>
public enum SessionStatus
{
    /// <summary>Not started, or started and waiting for the user to type — no turn in flight, nothing pending.</summary>
    Idle,

    /// <summary>A turn is in flight (message sent, no <c>TurnCompleted</c>/error yet).</summary>
    Busy,

    /// <summary>A tool-use permission decision is pending, or the CLI reported <c>needs_action</c>.</summary>
    WaitingForInput,

    /// <summary>The most recent turn finished successfully and nothing is pending.</summary>
    Done,

    /// <summary>Same signal as <see cref="WaitingForInput"/> but reserved for the sidebar's "jumps out" affordance (badge/highlight).</summary>
    NeedsAttention,
}
