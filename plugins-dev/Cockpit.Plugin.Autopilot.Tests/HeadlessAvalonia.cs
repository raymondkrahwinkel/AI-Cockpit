using Avalonia;
using Avalonia.Headless;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// An Avalonia runtime without a screen (#69). A control cannot be built or attached without a platform, so this gives
/// the tests one, once, letting the workspace body's render path be observed by a test rather than only by Raymond.
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

            AppBuilder.Configure<Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();

            _started = true;
        }
    }
}

/// <summary>Marks the tests that need a platform; xunit builds the fixture once for the whole collection.</summary>
[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;
