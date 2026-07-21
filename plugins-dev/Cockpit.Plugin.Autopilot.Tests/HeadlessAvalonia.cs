using Avalonia;
using Avalonia.Headless;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// An Avalonia runtime without a screen (#69). A control cannot be built or attached without a platform, so this gives
/// the tests one, once, letting the workspace body's render path be observed by a test rather than only by Raymond.
/// <para>
/// It runs the real <see cref="Cockpit.App.App"/> (not a bare <see cref="Application"/>) so the workspace body resolves
/// the actual Cockpit theme brushes and fonts — the render tests then observe the surface as an operator sees it, and
/// the screenshot render (<see cref="AutopilotScreenshotTests"/>) captures real, themed pixels. <c>SetupWithoutStarting</c>
/// runs only <c>App.Initialize</c> (the XAML/theme load), never <c>OnFrameworkInitializationCompleted</c>, so
/// none of the app's real startup (secrets, cockpit, plugins) fires. Skia with headless drawing on is what lets a
/// frame be captured; the text-only render tests do not need it but share the process-global platform.
/// </para>
/// <para>
/// Set up by hand rather than with Avalonia.Headless.XUnit, which requires xunit v3 while this repo is on v2.
/// </para>
/// </summary>
public sealed class HeadlessAvalonia
{
    private static readonly Lock Gate = new();
    private static bool _started;

    public HeadlessAvalonia()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            // No .With(Program.CockpitFontOptions()) as the production Screenshotter has: those are internal to
            // Cockpit.App and only register emoji fallbacks, which nothing the render tests draw needs. If a future
            // render ever shows emoji content, add the parity there rather than reaching for the internal helper.
            AppBuilder.Configure<Cockpit.App.App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

            _started = true;
        }
    }
}

/// <summary>Marks the tests that need a platform; xunit builds the fixture once for the whole collection.</summary>
[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;
