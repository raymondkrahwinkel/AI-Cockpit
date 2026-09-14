using Avalonia.Platform;
using Avalonia.Styling;
using Cockpit.App.Theming;
using Cockpit.Core.Layout;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-860, the switch: three stands into one variant, the stand kept apart from the variant on screen, and a
/// system flip that only lands while the stand is System.
/// </summary>
[Collection("avalonia")]
public class Ac860ThemeSelectorTests
{
    [Theory]
    [InlineData(ThemeMode.Light, PlatformThemeVariant.Dark, "Light")]
    [InlineData(ThemeMode.Dark, PlatformThemeVariant.Light, "Dark")]
    [InlineData(ThemeMode.System, PlatformThemeVariant.Light, "Light")]
    [InlineData(ThemeMode.System, PlatformThemeVariant.Dark, "Dark")]
    public void ThreeStandsResolveToOneVariant(ThemeMode mode, PlatformThemeVariant system, string expected)
        => Assert.Equal(expected, ThemeSelector.Resolve(mode, system).ToString());

    // System while the desktop is light reads as Light on screen and stays System as the choice — the derived
    // reading must never be mistaken for what the operator picked, or a save writes it over the choice.
    [Fact]
    public void TheChosenStandIsKeptApartFromTheActiveVariant()
    {
        var selector = new ThemeSelector(ThemeMode.System, PlatformThemeVariant.Light, _ => { });

        Assert.Equal(ThemeVariant.Light, selector.Active);
        Assert.Equal(ThemeMode.System, selector.Mode);
    }

    [Fact]
    public void ASystemFlipLandsOnlyWhileFollowingTheSystem()
    {
        var applied = new List<ThemeVariant>();
        var selector = new ThemeSelector(ThemeMode.Dark, PlatformThemeVariant.Dark, applied.Add);

        selector.SetSystem(PlatformThemeVariant.Light);
        Assert.Empty(applied);

        selector.SetMode(ThemeMode.System);
        Assert.Equal([ThemeVariant.Light], applied);

        selector.SetSystem(PlatformThemeVariant.Dark);
        Assert.Equal([ThemeVariant.Light, ThemeVariant.Dark], applied);
    }
}
