using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.Vault;

namespace Cockpit.Plugins.Abstractions.UI;

/// <summary>
/// The block a vault provider's connect page puts on top: shown while the vault is closed because credential
/// encryption is off or locked, hidden once <see cref="IVaultProviderContext.Availability"/> is
/// <see cref="VaultAvailability.Available"/>. Its button calls <see cref="IVaultProviderContext.OpenEncryptionSettings"/>.
/// </summary>
public sealed class VaultEncryptionRequiredPanel : Border
{
    /// <summary>The text of the block.</summary>
    public const string Message =
        "The vault needs credential encryption. Turn it on in Security; until then agents cannot use the vault and no provider can be connected.";

    private readonly IVaultProviderContext _context;

    /// <summary>Builds the block over the provider's context from <c>ICockpitHost.AddVaultProvider</c>.</summary>
    public VaultEncryptionRequiredPanel(IVaultProviderContext context)
    {
        _context = context;
        var button = new Button { Content = "Turn on encryption", HorizontalAlignment = HorizontalAlignment.Left };
        button.Classes.Add("Accent");
        button.Click += (_, _) => _context.OpenEncryptionSettings();
        var text = new TextBlock { Text = Message, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextPrimaryBrush"));
        Child = new StackPanel { Spacing = 10, Children = { text, button } };
        Padding = new Thickness(12);
        CornerRadius = new CornerRadius(9);
        BorderThickness = new Thickness(1);
        this.Bind(BackgroundProperty, this.GetResourceObservable("CockpitSecondaryBgBrush"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("CockpitHairlineBrush"));
        AttachedToVisualTree += (_, _) =>
        {
            _context.AvailabilityChanged += _OnChanged;
            _Refresh();
        };
        DetachedFromVisualTree += (_, _) => _context.AvailabilityChanged -= _OnChanged;
    }

    private void _OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(_Refresh);

    private void _Refresh() => IsVisible = _context.Availability != VaultAvailability.Available;
}
