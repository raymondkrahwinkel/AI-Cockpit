using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Cockpit.App.Theming;
using Cockpit.App.ViewModels;
using Cockpit.Core.Layout;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-860, the switch: three stands into one variant, and the app wiring that carries a chosen stand and a system
/// flip all the way to the variant the application actually requests.
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

    // Through `App.FollowTheme`, the seam `_StartCockpit` takes: the stand as Options writes it and the OS flip as
    // the platform reports it both have to arrive at `RequestedThemeVariant` — and the flip only while on System.
    // Every assertion waits out the dispatcher hop the wiring makes, so it reads behaviour and not timing.
    [Fact]
    public async Task AChosenStandAndASystemFlip_ReachTheRequestedVariant_ThroughTheAppWiring() =>
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var app = (App)Application.Current!;
            var before = app.RequestedThemeVariant;
            var platform = Substitute.For<IPlatformSettings>();
            platform.GetColorValues().Returns(new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark });
            var cockpit = new CockpitViewModel { ThemeMode = ThemeMode.Light };
            try
            {
                app.FollowTheme(cockpit, platform);
                await _Settled();
                Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);

                cockpit.ThemeMode = ThemeMode.Dark;
                await _Settled();
                Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);

                _FlipSystem(platform, PlatformThemeVariant.Light);
                await _Settled();
                Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);

                cockpit.ThemeMode = ThemeMode.System;
                await _Settled();
                Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);

                _FlipSystem(platform, PlatformThemeVariant.Dark);
                await _Settled();
                Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            }
            finally
            {
                app.RequestedThemeVariant = before;
            }
        });

    private static void _FlipSystem(IPlatformSettings platform, PlatformThemeVariant variant) =>
        platform.ColorValuesChanged += Raise.Event<EventHandler<PlatformColorValues>>(platform, new PlatformColorValues { ThemeVariant = variant });

    private static Task _Settled() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();
}
