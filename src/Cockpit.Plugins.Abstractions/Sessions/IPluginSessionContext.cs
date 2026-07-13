namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One session, as seen by a contribution that lives <em>inside</em> that session's header
/// (<see cref="ICockpitHost.AddSessionHeaderItem"/>). Where <see cref="ICockpitSessionObserver"/> follows
/// whichever session is selected, this is bound to a single one for as long as its panel exists — so a header
/// item shows the state of the session it sits in, not of the session you happen to be looking at.
/// Events are raised on the UI thread.
/// </summary>
public interface IPluginSessionContext
{
    /// <summary>The directory this session is working in, or null until it is known (an SDK session before its init event).</summary>
    string? WorkingDirectory { get; }

    /// <summary>Raised when <see cref="WorkingDirectory"/> becomes known or changes — the cue to re-scope.</summary>
    event EventHandler? WorkingDirectoryChanged;

    /// <summary>Raised for each chunk of text <em>this</em> session produces (assistant prose and tool output), verbatim — substring-scan it for a signal.</summary>
    event EventHandler<SessionOutputText>? OutputProduced;
}
