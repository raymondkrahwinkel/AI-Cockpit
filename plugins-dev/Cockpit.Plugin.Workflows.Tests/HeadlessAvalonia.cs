using Avalonia;
using Avalonia.Headless;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// An Avalonia runtime without a screen (#69). Controls ask the platform for things as ordinary as a mouse cursor,
/// so they cannot even be constructed without one — this gives the tests a platform, once, so control-level bugs
/// (a Button swallowing a pointer press, say) can be caught by a test rather than by Raymond.
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
