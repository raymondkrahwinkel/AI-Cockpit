using Avalonia;
using Avalonia.Headless;

namespace Cockpit.Plugin.Autopilot.Tests;

// An Avalonia runtime without a screen (#69). A control cannot be built or attached without a platform, so this gives
// the tests one, once, letting the workspace body's render path be observed by a test rather than only by the operator.
//
// It runs the real `Cockpit.App.App` (not a bare `Application`) so the workspace body resolves
// the actual Cockpit theme brushes and fonts — the render tests then observe the surface as an operator sees it, and
// the screenshot render (`AutopilotScreenshotTests`) captures real, themed pixels. `SetupWithoutStarting`
// runs only `App.Initialize` (the XAML/theme load), never `OnFrameworkInitializationCompleted`, so
// none of the app's real startup (secrets, cockpit, plugins) fires. Skia with headless drawing on is what lets a
// frame be captured; the text-only render tests do not need it but share the process-global platform.
//
// Set up by hand rather than with Avalonia.Headless.XUnit, which requires xunit v3 while this repo is on v2.
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
                // The app ships Inter and asks for it at startup; a harness without it measures text in whatever
                // font the machine offers, which is not this program and not the same on CI.
                .WithInterFont()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();

            _started = true;
        }
    }
}

// Marks the tests that need a platform; xunit builds the fixture once for the whole collection.
[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;
