namespace Cockpit.Plugins.Abstractions.Files;

/// <summary>A span of text from <see cref="Start"/> to <see cref="End"/>, as UTF-16 offsets with <c>Start &lt;= End</c>.</summary>
public sealed record LiveRange(int Start, int End);

/// <summary>What the host tells the source about its own cursor. <see cref="Selection"/> is null when nothing is selected.</summary>
public sealed record LivePresenceState(string Activity, int? Caret = null, LiveRange? Selection = null);

/// <summary>
/// Someone in the file. The source decides every field, the colour included: the host draws <see cref="Color"/> as given and never
/// derives one. <see cref="You"/> marks this host's own entry, which the host lists but does not draw as a remote caret.
/// </summary>
/// <param name="Who">A stable id for the person, the same across their connections.</param>
/// <param name="Name">The name to show.</param>
/// <param name="Color">A <c>#rrggbb</c> colour, fixed per person.</param>
/// <param name="Source">Where they are editing from: <c>Cockpit</c>, <c>web</c> or <c>agent</c>.</param>
/// <param name="Role">Their role in the file, for example <c>Editor</c> or <c>Viewer</c>.</param>
/// <param name="Activity">What they are doing: <c>editing</c>, <c>selecting</c>, <c>reading</c> or <c>watching</c>.</param>
/// <param name="You">True for this host's own entry.</param>
/// <param name="Away">True when the source has lost them (offline); the host dims them.</param>
/// <param name="Caret">Where their caret is, or null when they have none.</param>
/// <param name="Selection">What they have selected, or null.</param>
public sealed record LivePeer(string Who, string Name, string Color, string Source, string Role, string Activity,
    bool You = false, bool Away = false, int? Caret = null, LiveRange? Selection = null);

/// <summary>
/// An optional extra on an <see cref="ILiveFileSession"/>: who else is in the file and where their cursors are. A session that
/// does not implement it simply shows nobody.
/// </summary>
public interface ILivePresence
{
    /// <summary>Raised with everyone in the file, this host included, whenever it changes. Offsets are on the current text.</summary>
    event EventHandler<IReadOnlyList<LivePeer>>? PeersChanged;

    /// <summary>Everyone in the file right now.</summary>
    IReadOnlyList<LivePeer> Peers { get; }

    /// <summary>An <c>http(s)</c> address that opens the file in the source's own app, or null. The popover offers it as "Open in …".</summary>
    string? OpenUrl { get; }

    /// <summary>Tells the source where this host's cursor is and what it is doing. The source repeats it as its rhythm needs.</summary>
    Task SetPresenceAsync(LivePresenceState state, CancellationToken cancellationToken);
}
