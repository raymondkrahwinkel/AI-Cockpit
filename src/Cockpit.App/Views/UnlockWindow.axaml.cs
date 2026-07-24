using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// Stands in front of the cockpit when the operator encrypted their credentials: the password is the key, so
/// nothing that reads a setting — no plugin, no MCP server, no session — may run until it has been typed.
/// </summary>
public partial class UnlockWindow : Window
{
    public UnlockWindow()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this, "AI-Cockpit");

        Opened += (_, _) => TakeFocus();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is UnlockViewModel viewModel)
            {
                viewModel.ResetRequested += OnResetRequested;
            }
        };
    }

    /// <summary>
    /// Puts the window in front with the caret in the password box — what the operator came here to type. Called when
    /// the window opens, and again when the OS unlocks (AC-187): a screen shown while the desktop was still locked was
    /// never activated on the operator's own desktop, so it needs asking a second time.
    /// </summary>
    public void TakeFocus()
    {
        Activate();
        this.FindControl<TextBox>("PasswordBox")?.Focus();
    }

    private UnlockViewModel? ViewModel => DataContext as UnlockViewModel;

    private void OnUnlock(object? sender, RoutedEventArgs e) => _ = ViewModel?.UnlockAsync();

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = ViewModel?.UnlockAsync();
        }
    }

    private void OnForgot(object? sender, RoutedEventArgs e) => ViewModel?.RequestReset();

    private async void OnResetRequested(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        // Spelled out rather than softened: this is the one door out of a forgotten password, and it costs the
        // credentials. Everything else the operator configured survives, which is the part that makes it usable
        // rather than a euphemism for "start over".
        var confirmation = new ConfirmationDialog
        {
            DataContext = new ConfirmationDialogViewModel(
                "Start without your credentials",
                "There is no way to decrypt your API keys and tokens without the password — that is what makes the "
                + "encryption worth having. Continuing empties them and turns encryption off. Your profiles, "
                + "sessions, layout and shortcuts are untouched; you will have to enter your tokens again.",
                "Empty credentials"),
        };

        if (await confirmation.ShowDialog<bool>(this))
        {
            await viewModel.ResetAsync();
        }
    }
}
