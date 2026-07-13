using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// What to put back from a backup (#70). The archive says which plugins it carries and this asks which of them the
/// operator actually wants — a backup made on another machine may hold plugins this one has never had, and restoring
/// them all because they were in the file is a decision nobody made.
/// </summary>
public partial class RestoreSelectionDialog : Window
{
    public RestoreSelectionDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnRestore(object? sender, RoutedEventArgs e) =>
        Close((DataContext as RestoreSelectionViewModel)?.ToOptions());
}
