using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One field a plugin contributed to the project editor (AC-317): what this project is called in a tracker or on a
/// forge. The plugin described it and supplies the choices; this holds the row's state while the dialog is open —
/// what is stored, what is in the box, and whether the list is still coming.
/// <para>
/// <see cref="Value"/> and <see cref="Text"/> are separate because the operator picks by name and the plugin queries
/// by identifier: "AI-Cockpit — AC" is what is read, <c>AC</c> is what is kept. They only agree for a value typed by
/// hand, which is exactly what a repository nobody granted read access to needs.
/// </para>
/// </summary>
public partial class ProjectPluginFieldViewModel : ViewModelBase
{
    private readonly ProjectFieldRegistration _registration;

    public ProjectPluginFieldViewModel(ProjectFieldRegistration registration, string? value)
    {
        _registration = registration;
        _value = value ?? string.Empty;

        // Until the options arrive the identifier is all there is to show. A saved link must be visible the moment
        // the editor opens — a box that is blank while a list loads reads as "not linked", and an operator who saves
        // in that moment would make it true.
        _text = _value;
    }

    public string Key => _registration.Key;

    public string Title => _registration.Title;

    public string? Hint => _registration.Hint;

    public string? Placeholder => _registration.Placeholder;

    /// <summary>What gets stored on the project: the picked option's identifier, or whatever the operator typed.</summary>
    [ObservableProperty]
    private string _value;

    /// <summary>What is in the box — an option's display text once one is picked, the identifier itself otherwise.</summary>
    [ObservableProperty]
    private string _text;

    /// <summary>The choices the plugin supplied, empty until they have loaded (or when it had none to offer).</summary>
    public ObservableCollection<ProjectFieldOption> Options { get; } = [];

    /// <summary>Whether the choices are still coming — the row stays usable while they are, so the dialog never waits on a network call.</summary>
    [ObservableProperty]
    private bool _isLoadingOptions;

    /// <summary>Why the choices could not be fetched, shown under the box. Null when they arrived, including when there were none.</summary>
    [ObservableProperty]
    private string? _loadError;

    /// <summary>
    /// Fills <see cref="Options"/> from the plugin, and shows a saved value under its proper name once the list can
    /// say what that name is. Never throws: a tracker that is unreachable costs this row its list, not the editor.
    /// </summary>
    public async Task LoadOptionsAsync(CancellationToken cancellationToken = default)
    {
        IsLoadingOptions = true;
        LoadError = null;

        try
        {
            // On a worker, not merely awaited: everything a plugin's fetch does before its own first await runs on
            // whichever thread called it, and for the GitHub field that is a process spawn — the editor would stutter
            // on opening. The continuation comes back to the UI thread, which is where Options may be added to.
            foreach (var option in await Task.Run(() => _registration.LoadOptionsAsync(cancellationToken), cancellationToken))
            {
                Options.Add(option);
            }

            _ShowValueUnderItsDisplayName();
        }
        catch (OperationCanceledException)
        {
            // The dialog closed while the list was still coming; there is nobody left to tell.
        }
        catch (Exception exception)
        {
            // Said plainly rather than swallowed: "no options" and "the fetch failed" mean different things to
            // someone deciding whether their project is linked to the right place.
            LoadError = exception.Message;
        }
        finally
        {
            IsLoadingOptions = false;
        }
    }

    // The text is the only thing the box reports, so it is the only thing read: picking from the list puts that
    // option's display name in it, and typing puts whatever was typed. A display name that matches an option is
    // resolved back to that option's identifier — which is both how a pick is stored and why picking "AI-Cockpit —
    // AC" and typing it out by hand cannot store two different things. Anything else is kept verbatim, which is what
    // a repository the operator has no read access to needs.
    //
    // One rule and no re-entry guard, deliberately: _ShowValueUnderItsDisplayName writes a display name back into the
    // box, which lands here and resolves to the very identifier it came from. A guard around that write would only be
    // protecting the rule from agreeing with itself.
    partial void OnTextChanged(string value) =>
        Value = Options.FirstOrDefault(option => string.Equals(option.Display, value, StringComparison.Ordinal))?.Value
            ?? value;

    private void _ShowValueUnderItsDisplayName()
    {
        if (Options.FirstOrDefault(option => string.Equals(option.Value, Value, StringComparison.Ordinal)) is { } match)
        {
            Text = match.Display;
        }
    }
}
