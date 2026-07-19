using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using FluentAssertions;

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

        var tabs = dialog.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.Items.OfType<TabItem>().Select(tab => tab.Header)
            .Should().Contain("Security");

        dialog.Close();
    });

    [Fact]
    public void TheUnlockWindow_AsksForThePassword_AndSaysWhyItIsLocked() => HeadlessAvalonia.Run(() =>
    {
        var window = new UnlockWindow { DataContext = new UnlockViewModel() };
        window.Show();

        var password = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "PasswordBox");
        password.PasswordChar.Should().NotBe('\0', "a password box that shows the password is not one");

        window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text)
            .Should().Contain(text => text != null && text.Contains("encrypted"));

        window.Close();
    });

    [Fact]
    public void ARefusedPassword_LeavesTheAppLocked_AndSaysSo() => HeadlessAvalonia.Run(async () =>
    {
        var viewModel = new UnlockViewModel(new AlwaysWrongPassword());
        var unlocked = false;
        viewModel.Unlocked += (_, _) => unlocked = true;

        viewModel.Password = "not the password";
        await viewModel.UnlockAsync();

        unlocked.Should().BeFalse();
        viewModel.Error.Should().NotBeNullOrEmpty("an operator who typed the wrong password must be told, not left staring");
        viewModel.Password.Should().BeEmpty("the box is cleared so the next attempt starts fresh");
    });

    private sealed class AlwaysWrongPassword : Core.Abstractions.Secrets.ISecretProtectionService
    {
        public Task<Core.Abstractions.Secrets.SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Core.Abstractions.Secrets.SecretProtectionStatus(Enabled: true, Unlocked: false));

        public Task DismissUnprotectedWarningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public void Relock()
        {
        }

        public Task EnableAsync(string password, IProgress<Core.Abstractions.Secrets.SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DisableAsync(IProgress<Core.Abstractions.Secrets.SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ChangePasswordAsync(string currentPassword, string newPassword, IProgress<Core.Abstractions.Secrets.SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
