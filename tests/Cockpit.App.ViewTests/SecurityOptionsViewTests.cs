using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The two surfaces of credential encryption: the tab that turns it on, and the window that stands in front of the
/// cockpit once it is on.
/// <para>
/// Here rather than in the unit tests because both are views — the gate is only a gate if the window actually renders
/// and the cockpit's own window is not built behind it, and a XAML binding to a property that does not exist fails at
/// load time and nowhere else.
/// </para>
/// </summary>
[Collection("avalonia")]
public class SecurityOptionsViewTests
{
    [Fact]
    public void TheOptionsDialog_HasASecurityTab_ThatLoadsItsBindings() => HeadlessAvalonia.Run(() =>
    {
        var dialog = new OptionsDialog { DataContext = new CockpitViewModel() };
        dialog.Show();

        // AC-1000: the sidebar's CategoryNav ListBox replaced the old per-tab TabControl.
        var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
        Assert.Contains("security", nav.Items.OfType<ListBoxItem>().Select(item => item.Tag as string));

        dialog.Close();
    });

    [Fact]
    public void TheUnlockWindow_AsksForThePassword_AndSaysWhyItIsLocked() => HeadlessAvalonia.Run(() =>
    {
        var window = new UnlockWindow { DataContext = new UnlockViewModel() };
        window.Show();

        var password = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PasswordBox");
        Assert.NotEqual('\0', password.PasswordChar);

        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text), text => text != null && text.Contains("encrypted"));

        window.Close();

        // AC-1878, layer 2 view: startup asks in the splash; the separate window remains for screen lock.
        var sequence = new Core.Startup.StartupSequence();
        var splashModel = new SplashViewModel(sequence);
        var unlock = new UnlockViewModel { Password = "temporary" };
        splashModel.BeginUnlock(unlock);
        var splash = new SplashWindow(splashModel, null);
        splash.Show();
        var splashPassword = splash.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PasswordBox");
        Assert.True(splashPassword.IsVisible);
        Assert.NotEqual('\0', splashPassword.PasswordChar);
        Assert.Equal("temporary", splashPassword.Text);
        Assert.Equal("Unlock to continue", splashModel.Headline);
        unlock.IsBusy = true;
        Assert.False(splashPassword.IsEffectivelyEnabled);
        Assert.Equal("Checking the password", splashModel.Detail);
        unlock.IsBusy = false;
        var forgot = splash.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "I forgot my password"));
        Assert.Equal(
            Assert.IsType<SolidColorBrush>(splash.FindResource("CockpitTextSecondaryBrush")).Color,
            Assert.IsType<SolidColorBrush>(forgot.GetVisualDescendants().OfType<TextBlock>().Single().Foreground).Color);
        forgot.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var confirmation = Assert.Single(splash.OwnedWindows);
        Assert.IsType<ConfirmationDialog>(confirmation);
        var cancel = confirmation.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel"));
        cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(splashModel.IsUnlocking);
        Assert.True(splash.IsVisible);
        splashModel.EndUnlock();
        Assert.False(splashPassword.IsEffectivelyVisible);
        Assert.Empty(unlock.Password);
        Assert.Equal("Opening your cockpit…", splashModel.Headline);
        splash.Close();
    });

    [Fact]
    public void ARefusedPassword_LeavesTheAppLocked_AndSaysSo() => HeadlessAvalonia.Run(async () =>
    {
        var viewModel = new UnlockViewModel(new AlwaysWrongPassword());
        var unlocked = false;
        viewModel.Unlocked += (_, _) => unlocked = true;

        viewModel.Password = "not the password";
        await viewModel.UnlockAsync();

        Assert.False(unlocked);
        Assert.False(string.IsNullOrEmpty(viewModel.Error), "an operator who typed the wrong password must be told, not left staring");
        Assert.Empty(viewModel.Password);
    });

    private sealed class AlwaysWrongPassword : Core.Abstractions.Secrets.ISecretProtectionService
    {
        public Task<Core.Abstractions.Secrets.SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Core.Abstractions.Secrets.SecretProtectionStatus(Enabled: true, Unlocked: false));

        public Task DismissUnprotectedWarningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task EnableAsync(string password, IProgress<Core.Abstractions.Secrets.SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DisableAsync(IProgress<Core.Abstractions.Secrets.SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ChangePasswordAsync(string currentPassword, string newPassword, IProgress<Core.Abstractions.Secrets.SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
