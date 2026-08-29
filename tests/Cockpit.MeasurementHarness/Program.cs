using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Cockpit.App.Diagnostics;
using Cockpit.MeasurementHarness.Core;
using Cockpit.MeasurementHarness.Meters;
using Cockpit.MeasurementHarness.Scenarios;

namespace Cockpit.MeasurementHarness;

/// <summary>Entry point: parses the flags, captures the run's identity, runs a scenario, writes the report.</summary>
public static class Program
{
    private static RunOutcome? _outcome;
    private static string? _unrecognisedCutOff;
    private static readonly CancellationTokenSource _stop = new();

    public static int Main(string[] args)
    {
        var flags = Options.Parse(args);
        var options = new Options(flags);

        if (options.UnsupportedReason is { } unsupported)
        {
            Console.WriteLine("VERDICT: MALFUNCTION");
            Console.WriteLine($"  blocked by: {unsupported}");
            return 4;
        }

        RunIdentity identity;
        try
        {
            identity = RunIdentity.Capture(options.Scenario, args, flags);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        HarnessApp.Body = () => _RunScenarioAsync(identity, options);
        _StartAvalonia(options);

        if (_outcome is not { } outcome)
        {
            Console.Error.WriteLine("The scenario produced no outcome; nothing was written.");
            return 3;
        }

        Console.WriteLine(outcome.Report);
        try
        {
            Console.WriteLine($"report written: {identity.WriteReport(options.OutputDirectory, outcome.Report)}");
        }
        catch (ReportCollisionException collision)
        {
            Console.Error.WriteLine(collision.Message);
            return 1;
        }

        return outcome.Trustworthy ? 0 : 4;
    }

    private static async Task _RunScenarioAsync(RunIdentity identity, Options options)
    {
        var pump = new Pump(options.Headless);
        var renderClock = options.Scenario == RenderClockScenario.Name;
        var run = new MeasurementRun(
            identity,
            renderClock ? RenderClockScenario.Control(pump) : LayoutLoopScenario.Control(pump, options.SettleMs));
        var cpu = new CpuMeter();
        var gc = new GcMeter();

        // Every meter opens its own window here. Cockpit's own CPU sampler is one shared instance whose
        // baseline every caller resets, which is how the same 0,96-core load reads 4,4% and 0,0%.
        cpu.Start();
        gc.Start();

        _HookLayoutLoopDetector(run.Recorder);
        if (renderClock)
        {
            await RenderClockScenario.RunAsync(run, pump).ConfigureAwait(true);
        }
        else
        {
            await LayoutLoopScenario.SweepAsync(
                run,
                pump,
                new SweepOptions(options.MinSessions, options.MaxSessions, options.Width, options.Height, options.SettleMs, options.Repeats))
                .ConfigureAwait(true);
        }

        await run.RunControlAsync().ConfigureAwait(true);

        // AC-1220: RenderClockRecovery tells the cut-off apart by Avalonia's message text, and an upgrade can
        // change that wording with nothing else noticing. An InvalidOperationException its own decision refuses
        // is that break happening, so the run says so instead of quietly reporting no loops.
        run.Gate(
            "the cut-off is the one the app still recognises",
            () => _unrecognisedCutOff is null,
            $"the UI thread raised InvalidOperationException(\"{_unrecognisedCutOff}\"), which "
            + "RenderClockRecovery.ShouldRecover does not accept: either Avalonia changed the cut-off wording and "
            + "the app's recovery is dead, or this run broke something else");

        run.Verify("cost of the run itself, after the measurement window closed", recorder =>
        {
            recorder.Measure("reachable-bytes", gc.ReachableBytes(run), "bytes");
            run.Write(string.Empty);
            run.Write($"render ticks forced by the harness: {pump.ForcedRenderTicks} (headless has no render timer of its own)");
            run.Write(cpu.Line("cpu over the whole run"));
            run.Write(gc.Line("gc over the whole run"));
            run.Write($"reachable after a full blocking collection: {recorder.ValueOf("reachable-bytes"):N0} bytes");
        });

        _outcome = run.Finish();
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>
    /// Avalonia reports a frame it had to cut off as an unhandled dispatcher exception. Recording it as a
    /// typed detector event is what stops a phase marker that merely mentions the words from being counted.
    /// </summary>
    // AC-1220: judged by the app's own RenderClockRecovery rather than by a second copy of Avalonia's wording.
    // The copy that stood here is the epic's own fault shape: one piece of knowledge in two places, of which one
    // goes quietly wrong. Change the wording and the ViewTests guard turns red, RenderClockRecovery gets fixed —
    // and this harness keeps reporting zero loops on a real cut-off, because nothing was watching its copy.
    private static void _HookLayoutLoopDetector(Recorder recorder) =>
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (RenderClockRecovery.ShouldRecover(e.Exception, RenderClockRecovery.MinimumInterval))
            {
                recorder.Detected("layout-loop", e.Exception.Message);
                e.Handled = true;
                return;
            }

            // Handled, so the sweep finishes and its gate reports this rather than the run dying mid-measurement.
            if (e.Exception is InvalidOperationException)
            {
                _unrecognisedCutOff ??= e.Exception.Message;
                recorder.Detected("cutoff-unrecognised", e.Exception.Message);
                e.Handled = true;
            }
        };

    private static void _StartAvalonia(Options options)
    {
        var builder = options.Scenario == LayoutLoopScenario.Name
            ? AppBuilder.Configure<Cockpit.App.App>()
            : AppBuilder.Configure<HarnessApp>();
        builder = options.Headless
            ? builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia()
            : builder.UsePlatformDetect();

        if (options.Scenario == LayoutLoopScenario.Name)
        {
            builder = builder.WithInterFont();
            builder.SetupWithoutStarting();
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await HarnessApp.Body!.Invoke().ConfigureAwait(true);
                }
                finally
                {
                    _stop.Cancel();
                }
            });
            Dispatcher.UIThread.MainLoop(_stop.Token);
            return;
        }

        builder.StartWithClassicDesktopLifetime([]);
    }
}

/// <summary>The bare application the harness runs in: a theme, and the scenario once the platform is up.</summary>
public sealed class HarnessApp : Avalonia.Application
{
    internal static Func<Task>? Body;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();

        // The scenario opens and closes a window per sweep point, so the default "quit when the last window
        // closes" would end the run at the first one — and did, silently, producing no report at all.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
        }

        Dispatcher.UIThread.Post(() => _ = Body?.Invoke());
    }
}

/// <summary>
/// The flags, parsed once and reported in full — including the ones left off. An absent flag is a setting,
/// and a header that only lists what was passed cannot be compared with a header from another run.
/// </summary>
public sealed class Options(IReadOnlyDictionary<string, string> flags)
{
    public string Scenario { get; } = flags["scenario"];

    public bool Headless { get; } = flags["headless"] == "true";

    public string? UnsupportedReason =>
        Scenario == RenderClockScenario.Name && Headless
            ? "render-clock has no compositor in --headless=true, so it cannot measure it"
            : null;

    public int MinSessions { get; } = int.Parse(flags["min-sessions"]);

    public int MaxSessions { get; } = int.Parse(flags["max-sessions"]);

    public double Width { get; } = double.Parse(flags["width"]);

    public double Height { get; } = double.Parse(flags["height"]);

    public int SettleMs { get; } = int.Parse(flags["settle-ms"]);

    public int Repeats { get; } = int.Parse(flags["repeats"]);

    public string OutputDirectory { get; } = flags["out"];

    /// <summary>Defaults first, then whatever the argv overrides, so the report always lists every flag.</summary>
    public static Dictionary<string, string> Parse(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scenario"] = LayoutLoopScenario.Name,
            ["headless"] = "false",
            ["min-sessions"] = "2",
            ["max-sessions"] = "6",
            ["width"] = "1400",
            ["height"] = "900",
            ["settle-ms"] = "700",
            ["repeats"] = "1",
            ["out"] = ".",
        };

        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var body = arg[2..];
            var split = body.IndexOf('=');
            var name = split < 0 ? body : body[..split];
            var value = split < 0 ? "true" : body[(split + 1)..];
            if (!flags.ContainsKey(name))
            {
                throw new ArgumentException($"unknown flag --{name}; known flags are {string.Join(", ", flags.Keys)}");
            }

            flags[name] = value;
        }

        return flags;
    }
}
