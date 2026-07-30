using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Cockpit.Plugin.GitStatus.Tests;

/// <summary>
/// An Avalonia runtime without a screen, so the dialog's wiring is knowable without running the cockpit and
/// looking — the same arrangement the GitHubIssues/YouTrack plugin test projects use, and <c>Cockpit.App.ViewTests</c>
/// for the host's own views.
/// <para>
/// It owns a thread, and every test body runs on it (<see cref="Run"/>). Avalonia binds its dispatcher to the
/// thread that set it up, and xunit hands each test whichever thread it pleases: setting the platform up once and
/// then touching a control from a test thread fails with "a different thread owns it" — sometimes, depending on
/// what else ran first, which is the worst way for a test to fail.
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
                // Skia rather than headless drawing, deliberately: headless drawing stubs out text shaping, so
                // glyphs measure without real widths and a layout assertion proves less than it appears to. Real
                // measurement costs a Skia reference and makes what these tests assert about layout actually true.
                AppBuilder.Configure<DialogTestApp>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .UseSkia()
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

    /// <summary>Runs a test body on the thread Avalonia belongs to, and hands its failure back to the test.</summary>
    public static void Run(Action body) => Dispatcher.UIThread.Invoke(body);

    public void Dispose() => _stop?.Cancel();
}
