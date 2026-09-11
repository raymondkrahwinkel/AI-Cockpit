using Avalonia.Controls;
using Avalonia.Input;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// The quick-note surface (AC-492), opened by a desktop-wide key while another program has the foreground. Shown
// without an owner on purpose: closing an owned window activates its owner, and the main window is not where the
// operator came from — with no owner the desktop hands focus back to whatever was in front before.
public partial class QuickNoteWindow : Window
{
    private bool _wasActivated;

    public QuickNoteWindow()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this, "Quick note", "Goes into the project's memory. Nothing starts.");

        Activated += (_, _) => _wasActivated = true;
        Deactivated += _OnDeactivated;
        Opened += (_, _) => NoteBox.Focus();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is QuickNoteViewModel note)
            {
                note.Saved += (_, _) => Close();
            }
        };
    }

    // A note that was never typed does not need a window kept around after the operator clicked elsewhere; one
    // that was typed stays — topmost, so it is still there — until it is saved or dismissed on purpose.
    private void _OnDeactivated(object? sender, EventArgs e)
    {
        if (_wasActivated && DataContext is QuickNoteViewModel { IsSaving: false } note && string.IsNullOrWhiteSpace(note.Note))
        {
            Close();
        }
    }

    // Enter is a new line — this is a note, not a search box. Ctrl+Enter saves; nothing on the keyboard starts.
    private void OnNoteBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is QuickNoteViewModel note)
        {
            note.SaveCommand.Execute(null);
            e.Handled = true;
        }
    }
}
