using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// Sets a session's status line by hand (AC-32). Returns the new value from <c>ShowDialog&lt;string?&gt;</c> —
/// an empty string when the operator clears it — and <see langword="null"/> when they cancel, so an unchanged
/// status is left alone.
/// </summary>
public partial class SetStatusDialog : Window
{
    public SetStatusDialog()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is SetStatusDialogViewModel)
            {
                CockpitWindowChrome.Apply(this, "Set status");
            }
        };

        Opened += (_, _) => this.FindControl<TextBox>("StatusBox")?.Focus();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnClear(object? sender, RoutedEventArgs e) => Close(string.Empty);

    private void OnSet(object? sender, RoutedEventArgs e) =>
        Close((DataContext as SetStatusDialogViewModel)?.StatusText?.Trim() ?? string.Empty);

    private void OnStatusBoxKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                OnSet(sender, e);
                break;
            case Key.Escape:
                OnCancel(sender, e);
                break;
        }
    }
}
