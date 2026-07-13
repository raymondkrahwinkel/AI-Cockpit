using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Cockpit.Core.Tests.Views;

/// <summary>
/// An Avalonia runtime without a screen. A control cannot be constructed without a platform to ask for a cursor, so
/// view-level facts — does the transcript virtualise? — are otherwise only knowable by running the app and looking.
/// <para>
/// It owns a thread, and every test body runs on it (<see cref="Run"/>). Avalonia binds its dispatcher to the thread
/// that set it up, and xunit hands each test whichever thread it pleases: setting the platform up once and then
/// touching a control from a test thread fails with "a different thread owns it" — sometimes, depending on what else
/// ran first, which is the worst way for a test to fail.
/// </para>
/// <para>
/// Built by hand rather than with Avalonia.Headless.XUnit, which wants xunit v3 while this repo is on v2.
/// </para>
/// </summary>
public sealed class HeadlessAvalonia : IDisposable
{
    private static readonly Lock Gate = new();
    private static Thread? _uiThread;
    private static CancellationTokenSource? _stop;

    public HeadlessAvalonia()
    {
        lock (Gate)
        {
            if (_uiThread is not null)
            {
                return;
            }

            var ready = new ManualResetEventSlim();
            _stop = new CancellationTokenSource();

            _uiThread = new Thread(() =>
            {
                AppBuilder.Configure<Cockpit.App.App>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                    .SetupWithoutStarting();

                ready.Set();

                // The loop is what makes it a UI thread: without it, work posted to the dispatcher is never run.
                Dispatcher.UIThread.MainLoop(_stop.Token);
            })
            {
                IsBackground = true,
                Name = "headless-avalonia",
            };

            _uiThread.Start();
            ready.Wait(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Runs a test body on the thread Avalonia belongs to, and hands its failure back to the test.</summary>
    public static void Run(Action body) => Dispatcher.UIThread.Invoke(body);

    public void Dispose() => _stop?.Cancel();
}

/// <summary>Marks the tests that need a platform; xunit builds the fixture once for the whole collection.</summary>
[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;
