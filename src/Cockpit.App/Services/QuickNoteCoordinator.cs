using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Projects;

namespace Cockpit.App.Services;

// AC-492: the quick-note key opens one note surface over whatever the operator is doing; a second press while it is
// open brings that one back rather than stacking another. Nothing here touches voice — the note is typed.
public sealed class QuickNoteCoordinator : ISingletonService
{
    private readonly GlobalHotkeyCoordinator _hotkeys;
    private readonly CockpitViewModel _cockpit;
    private readonly IProjectMemoryNoteWriter _writer;

    private QuickNoteWindow? _window;

    // What "save and start" runs; swappable for tests, since the real one launches a session.
    private Func<Project, string, Task> _start;

    // What was typed and not saved when the window last closed — Escape lies next to the typing keys, and a thought
    // that vanishes on it is gone for good. Lives only as long as this process; nothing writes it to disk.
    private string _draft = string.Empty;

    public QuickNoteCoordinator(GlobalHotkeyCoordinator hotkeys, CockpitViewModel cockpit, IProjectMemoryNoteWriter writer)
    {
        _hotkeys = hotkeys;
        _cockpit = cockpit;
        _writer = writer;
        _start = _StartAsync;

        // Marshalled like the screenshot key's: the key fires on the hook thread, and what follows is a window.
        hotkeys.Pressed += (_, id) =>
        {
            if (id == GlobalHotkeys.QuickNote)
            {
                Dispatcher.UIThread.Post(Open);
            }
        };

        hotkeys.TriggerDescriptionsChanged += (_, _) => Dispatcher.UIThread.Post(HandleTriggerDescriptionsChanged);
    }

    internal void HandleTriggerDescriptionsChanged() =>
        _cockpit.QuickNoteHotkeyTrigger = _hotkeys.DescribeTrigger(
            GlobalHotkeys.QuickNote,
            unboundMessage: "Your desktop has not bound it yet. Look for “Quick note” in its own shortcut settings.",
            macOsRefusedMessage: "macOS refused this key — pick another. The Quick note palette command still works.",
            failedMessage: "It is switched on but could not be registered — see the log.");

    // Also the command palette's route, so the surface exists without the key — and on a desktop that has none.
    public void Open()
    {
        if (_window is { } open)
        {
            WindowActivation.BringToFront(open);
            return;
        }

        var note = CreateViewModel();
        var window = new QuickNoteWindow { DataContext = note };
        window.Closed += (_, _) =>
        {
            KeepDraft(note);
            _window = null;
        };
        _window = window;
        window.Show();
    }

    // Only projects a note can actually land in are offered (criterion 2's counter-proof), in the order the
    // projects list already keeps: most recently opened first. Opens on whatever the last window left unsaved.
    internal QuickNoteViewModel CreateViewModel() =>
        new(_cockpit.Projects.RecentProjects.Where(_writer.CanAppend).ToList(), _writer, _start) { Note = _draft };

    internal void UseStart(Func<Project, string, Task> start) => _start = start;

    // The window's parting word: a saved note has already emptied itself, so only an unsaved one carries over.
    internal void KeepDraft(QuickNoteViewModel note) => _draft = note.Note;

    // "Save and start": the session opens on the project with the note in its composer, unsent — the same seam a
    // project job's prompt takes. The main window comes forward because the operator just asked to work there.
    private async Task _StartAsync(Project project, string note)
    {
        await _cockpit.StartProjectSessionWithPromptAsync(project, note);
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
        {
            WindowActivation.BringToFront(main);
        }
    }
}
