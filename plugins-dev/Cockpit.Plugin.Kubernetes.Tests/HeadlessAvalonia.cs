using Avalonia;
using Avalonia.Headless;

namespace Cockpit.Plugin.Kubernetes.Tests;

// An Avalonia runtime without a screen, the same fixture the Depot, Autopilot and Workflows plugin test projects
// carry (copied rather than shared — a plugin test project cannot reference another plugin's any more than a
// plugin can reference the host). Needed here since AC-1004 to build the settings view at all: it is a control,
// and a control cannot be constructed without a platform.
//
// Bare `Application` and headless drawing, unlike Depot's: these tests read the view's staged answer, never a
// pixel or a template, so neither Skia nor the cockpit's theme has anything to add.
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

            AppBuilder.Configure<Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();

            _started = true;
        }
    }
}

// Marks the tests that need a platform; xunit builds the fixture once for the whole collection.
[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;
