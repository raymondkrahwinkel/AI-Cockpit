using Avalonia;
using Avalonia.Headless;

namespace Cockpit.Plugin.Autopilot.Tests;

// An Avalonia runtime without a screen (#69), letting the workspace body's render path be observed by a test.
// Runs the real `Cockpit.App.App` (not a bare `Application`) so it resolves the real theme brushes/fonts, via
// `SetupWithoutStarting` (XAML/theme load only, no real app startup). Set up by hand since Avalonia.Headless.XUnit needs xunit v3, and this repo is on v2.
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
