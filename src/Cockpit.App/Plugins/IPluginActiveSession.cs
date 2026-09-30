using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Plugins;

/// <summary>
/// The session this window has selected, as a plugin's UI part reads it.
/// </summary>
public interface IPluginActiveSession
{
    /// <summary>
    /// The pane id of the selected session, or null when none is.
    /// </summary>
    string? ActivePaneId { get; }

    /// <summary>
    /// The working directory of the selected session, or null.
    /// </summary>
    string? ActiveSessionWorkingDirectory { get; }

    /// <summary>
    /// The live usage of the selected session, or null.
    /// </summary>
    SessionUsageSnapshot? ActiveSessionUsage { get; }

    /// <summary>
    /// Raised when the selection changes, or the selected session's working directory first becomes known.
    /// </summary>
    event EventHandler? ActiveSessionChanged;

    /// <summary>
    /// Raised when <see cref="ActiveSessionUsage"/> moves.
    /// </summary>
    event EventHandler? ActiveSessionUsageChanged;
}
