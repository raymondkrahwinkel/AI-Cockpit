using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>
/// An Avalonia runtime without a screen, so the settings view's wiring — is a placeholder documented where the
/// operator can see it, does the template box still fit the panel — is knowable without running the cockpit and
/// looking. The same arrangement <c>Cockpit.Plugin.GitHubIssues.Tests</c> uses (AC-521's YouTrack/GitHubIssues
/// counterparts) and <c>Cockpit.App.ViewTests</c> uses for the host's own views.
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
                // glyphs measure without real widths — and a pixel-position measurement (AC-521 IL#9) needs real
                // widths to mean anything.
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

    /// <summary>Runs a test body on the thread Avalonia belongs to, and hands its failure back to the test.</summary>
    public static void Run(Action body) => Dispatcher.UIThread.Invoke(body);

    public void Dispose() => _stop?.Cancel();
}
