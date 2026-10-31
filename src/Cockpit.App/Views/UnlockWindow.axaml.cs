using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// Stands in front of the cockpit when the operator encrypted their credentials: the password is the key, so
// nothing that reads a setting — no plugin, no MCP server, no session — may run until it has been typed.
public partial class UnlockWindow : Window
{
    public UnlockWindow()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this, titleBar: CockpitTitleBar.Window);

        Opened += (_, _) => TakeFocus();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is UnlockViewModel viewModel)
            {
                viewModel.ResetRequested += OnResetRequested;
            }
        };
    }

    // Puts the window in front with the caret in the password box — what the operator came here to type. Called when
    // the window opens, and again when the OS unlocks (AC-187): a screen shown while the desktop was still locked was
    // never activated on the operator's own desktop, so it needs asking a second time.
    public void TakeFocus()
    {
        Activate();
        this.FindControl<TextBox>("PasswordBox")?.Focus();
    }

    private UnlockViewModel? ViewModel => DataContext as UnlockViewModel;

    // Settling time between this window becoming active and the prompt: right after a Windows unlock the desktop is
    // still switching, and a prompt raised then loses the foreground (0x80098044, measured 31-10).
    private static readonly TimeSpan HelloPromptSettle = TimeSpan.FromMilliseconds(400);

    // Opens the Hello prompt once, when it is on offer and untried, but only after this window is the active one: a
    // prompt owned by a window that is not yet in front is refused by Windows (Raymond 31-10).
    public void PromptHello()
    {
        if (IsActive)
        {
            DispatcherTimer.RunOnce(_PromptHelloNow, HelloPromptSettle);
            return;
        }

        EventHandler? onActivated = null;
        onActivated = (_, _) =>
        {
            Activated -= onActivated;
            DispatcherTimer.RunOnce(_PromptHelloNow, HelloPromptSettle);
        };
        Activated += onActivated;
    }

    private void _PromptHelloNow()
    {
        if (ViewModel is { OffersHello: true, HelloTried: false, IsWaitingForHello: false, IsBusy: false } viewModel)
        {
            viewModel.TryHelloCommand.Execute(null);
        }
    }

    private void OnUnlock(object? sender, RoutedEventArgs e) => _ = ViewModel?.UnlockAsync();

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = ViewModel?.UnlockAsync();
        }
    }

    private void OnForgot(object? sender, RoutedEventArgs e) => ViewModel?.RequestReset();

    // AC-1879: offers Windows Hello once the window is up, so the lock never waits on the helper; the `--remote`
    // window never calls this. A failure here only means no Hello button: the password row is already there.
    public void OfferHello() => Opened += (_, _) =>
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.HelloSigner = new Services.WindowsHelloSigner(this);
            viewModel.PasswordFocusRequested += (_, _) => this.FindControl<TextBox>("PasswordBox")?.Focus();
            viewModel.ShowHelloCommand.Execute(null);
        }
    };

    private async void OnResetRequested(object? sender, EventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (await ConfirmResetAsync(this))
        {
            await viewModel.ResetAsync();
        }
    }

    internal static ConfirmationDialog CreateResetConfirmation() => new()
    {
        DataContext = new ConfirmationDialogViewModel(
            "Start without your credentials",
            "There is no way to decrypt your API keys and tokens without the password — that is what makes the "
            + "encryption worth having. Continuing empties them and turns encryption off. Your profiles, "
            + "sessions, layout and shortcuts are untouched; you will have to enter your tokens again.",
            "Empty credentials"),
    };

    internal static Task<bool> ConfirmResetAsync(Window owner) => CreateResetConfirmation().ShowDialog<bool>(owner);
}
