using Avalonia;
using Avalonia.Headless;

namespace Cockpit.Core.Tests.Views;

/// <summary>
/// An Avalonia runtime without a screen. A control cannot be constructed without a platform to ask for a cursor, so
/// view-level facts — does the transcript virtualise, does a button swallow a press — are otherwise only knowable by
/// running the app and looking. This makes them testable.
/// <para>
/// Built by hand rather than with Avalonia.Headless.XUnit, which wants xunit v3 while this repo is on v2 — the same
/// arrangement the workflow plugin's tests use.
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

            AppBuilder.Configure<Cockpit.App.App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();

            _started = true;
        }
    }
}

/// <summary>Marks the tests that need a platform; xunit builds the fixture once for the whole collection.</summary>
[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;
