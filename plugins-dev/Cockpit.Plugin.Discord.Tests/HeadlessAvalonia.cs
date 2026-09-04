using Avalonia;
using Avalonia.Headless;

namespace Cockpit.Plugin.Discord.Tests;

// An Avalonia runtime without a screen — a control needs a platform to be constructed at all. Copied from the
// Kubernetes plugin's test project rather than shared, since a plugin test project may no more reference another
// plugin's than a plugin may reference the host. By hand: Avalonia.Headless.XUnit wants xunit v3, this repo is v2.
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
