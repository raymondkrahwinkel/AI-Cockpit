using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Vault;

namespace Cockpit.App.Controls;

// AC-2347: Options › Vault, over the window's vault overview.
public partial class VaultOptionsPage : UserControl
{
    public VaultOptionsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is VaultOverviewViewModel vault)
            {
                this.FindControl<ContentControl>("EncryptionBlock")!.Content = new VaultEncryptionRequiredPanel(new PageContext(vault));
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnRevoke(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is VaultPermissionRow { Revoke: { } revoke })
        {
            await revoke();
        }
    }

    // The block the plugins use, driven by the overview instead of a provider's own context.
    private sealed class PageContext(VaultOverviewViewModel vault) : IVaultProviderContext
    {
        public VaultAvailability Availability => vault.Availability;

        public event EventHandler? AvailabilityChanged
        {
            add => vault.AvailabilityChanged += value;
            remove => vault.AvailabilityChanged -= value;
        }

        public bool TryStoreCredential(string value) => false;

        public string? ReadCredential() => null;

        public void ClearCredential()
        {
        }

        public void OpenEncryptionSettings() => vault.OpenEncryptionSettings();
    }
}
