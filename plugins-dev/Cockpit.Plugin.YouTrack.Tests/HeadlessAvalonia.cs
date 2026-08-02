using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Cockpit.Plugin.YouTrack.Tests;

// An Avalonia runtime without a screen, so the dialog's wiring — does a filter keep its selection, does a status
// line survive the reload an action triggers — is knowable without running the cockpit and looking. The same
// arrangement `Cockpit.App.ViewTests` uses for the host's own views.
//
// It owns a thread, and every test body runs on it (`Run`). Avalonia binds its dispatcher to the
// thread that set it up, and xunit hands each test whichever thread it pleases: setting the platform up once and
// then touching a control from a test thread fails with "a different thread owns it" — sometimes, depending on
// what else ran first, which is the worst way for a test to fail.
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
                // Skia rather than headless drawing, deliberately: headless drawing stubs out text shaping, so
                // glyphs measure without real widths. Layout assertions then prove less than they appear to — and
                // a wrapped run containing line breaks never finishes measuring at all, allocating until the
                // process is killed (which took an OOM kill of the whole machine to notice). Real measurement
                // costs a Skia reference and makes what these tests assert about layout actually true.
                AppBuilder.Configure<DialogTestApp>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .UseSkia()
                    // The app ships Inter and asks for it at startup; a harness without it measures text in whatever
                    // font the machine offers, which is not this program and not the same on CI.
                    .WithInterFont()
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

    // Runs a test body on the thread Avalonia belongs to, and hands its failure back to the test.
    public static void Run(Action body) => Dispatcher.UIThread.Invoke(body);

    public void Dispose() => _stop?.Cancel();
}
