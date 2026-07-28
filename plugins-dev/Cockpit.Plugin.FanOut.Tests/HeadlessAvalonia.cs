using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Cockpit.Plugin.FanOut.Tests;

/// <summary>
/// An Avalonia runtime without a screen: a control cannot be built without a platform, so this gives the tests
/// one, once, letting the workspace body be started by a test rather than only by an operator.
/// <para>
/// A bare <see cref="Application"/> rather than the cockpit's own: these tests observe what the body embeds and
/// where it places it, never how it is painted, and the body already falls back to literal colours when no theme
/// answers. Pulling the whole app in for brushes nothing asserts on would be weight for its own sake.
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

            AppBuilder.Configure<Application>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();

            _started = true;
        }
    }

    /// <summary>Runs a body on the UI thread — controls have thread affinity, and xunit does not run tests on it.</summary>
    public static T Run<T>(Func<T> body) => Dispatcher.UIThread.Invoke(body);
}
