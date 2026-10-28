using Avalonia.Controls;

namespace Cockpit.Plugins.Abstractions.UI;

/// <summary>The host's live-preview Markdown editor (<see cref="ICockpitUiHost.CreateMarkdownEditor"/>); the text is the file, byte for byte.</summary>
public interface IMarkdownEditor
{
    /// <summary>The control to place; it has one parent.</summary>
    Control View { get; }

    /// <summary>The whole text, unchanged by display: what was set comes back identical until someone types.</summary>
    string Text { get; set; }

    /// <summary>Every line formatted, no toolbar; a key press is not inserted but raises <see cref="EditRequested"/>.</summary>
    bool IsReadOnly { get; set; }

    /// <summary>Shows the plain Markdown source instead of the live view (Ctrl+/).</summary>
    bool ShowSource { get; set; }

    /// <summary>The text changed through the editor.</summary>
    event EventHandler? TextChanged;

    /// <summary>A key was pressed in the read-only view: the 1-based line it was on and the text typed; the owner decides whether to start editing.</summary>
    event Action<int, string>? EditRequested;
}
