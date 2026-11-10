using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugins.Abstractions.UI.Files;

/// <summary>Which file to edit live, and the file workspace type whose backend channel serves it.</summary>
public sealed record LiveMarkdownEditorOptions(string WorkspaceTypeId, FileRef File);

/// <summary>
/// The cockpit's live Markdown editor over one file of a live file source, without a workspace tab around it: the people in the
/// file, who is typing, their carets with names, and your own typing. Dispose it to leave the file.
/// </summary>
public interface ILiveMarkdownEditor : IDisposable
{
    /// <summary>The control to place; it has one parent.</summary>
    Control View { get; }

    /// <summary>Everyone in the file right now, this host included; empty until the source has said who is there.</summary>
    IReadOnlyList<LivePeer> Peers { get; }

    /// <summary>Raised on the UI thread when <see cref="Peers"/> changes.</summary>
    event EventHandler? PeersChanged;
}
