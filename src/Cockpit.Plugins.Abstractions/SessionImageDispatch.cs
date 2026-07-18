namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// The images from one user message (AC-14), handed to every registered <see cref="SessionImageSinkRegistration"/>:
/// which session they came from (its pane id), and the images themselves. Provider-agnostic.
/// </summary>
/// <param name="PaneId">The pane id of the session the message was sent to — a tracker plugin maps it to the issue that session tracks.</param>
/// <param name="Images">The images the message carried; never empty (a sink is only called when there is at least one).</param>
public sealed record SessionImageDispatch(string PaneId, IReadOnlyList<SessionImageAttachment> Images);
