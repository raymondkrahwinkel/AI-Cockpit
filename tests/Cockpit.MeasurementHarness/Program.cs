using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Cockpit.MeasurementHarness.Core;
using Cockpit.MeasurementHarness.Meters;
using Cockpit.MeasurementHarness.Scenarios;

namespace Cockpit.MeasurementHarness;

/// <summary>Entry point: parses the flags, captures the run's identity, runs a scenario, writes the report.</summary>
public static class Program
{
    private static RunOutcome? _outcome;

    public static int Main(string[] args)
    {
        var flags = Options.Parse(args);
        var options = new Options(flags);

        RunIdentity identity;
        try
        {
            identity = RunIdentity.Capture(LayoutLoopScenario.Name, args, flags);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        HarnessApp.Body = () => _RunScenarioAsync(identity, options);
        _StartAvalonia(options.Headless);

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
        var run = new MeasurementRun(identity, LayoutLoopScenario.Control(pump, options.SettleMs));
        var cpu = new CpuMeter();
        var gc = new GcMeter();

        // Every meter opens its own window here. Cockpit's own CPU sampler is one shared instance whose
        // baseline every caller resets, which is how the same 0,96-core load reads 4,4% and 0,0%.
        cpu.Start();
        gc.Start();

        _HookLayoutLoopDetector(run.Recorder);
        await LayoutLoopScenario.SweepAsync(
            run,
            pump,
            new SweepOptions(options.MinSessions, options.MaxSessions, options.Width, options.Height, options.SettleMs))
            .ConfigureAwait(true);
        await run.RunControlAsync().ConfigureAwait(true);

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
    private static void _HookLayoutLoopDetector(Recorder recorder) =>
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (e.Exception is not InvalidOperationException { Message: "Infinite layout loop detected" })
            {
                return;
            }

            recorder.Detected("layout-loop", e.Exception.Message);
            e.Handled = true;
        };

    private static void _StartAvalonia(bool headless)
    {
        var builder = AppBuilder.Configure<HarnessApp>();
        builder = headless
            ? builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia()
            : builder.UsePlatformDetect();

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
    public bool Headless { get; } = flags["headless"] == "true";

    public int MinSessions { get; } = int.Parse(flags["min-sessions"]);

    public int MaxSessions { get; } = int.Parse(flags["max-sessions"]);

    public double Width { get; } = double.Parse(flags["width"]);

    public double Height { get; } = double.Parse(flags["height"]);

    public int SettleMs { get; } = int.Parse(flags["settle-ms"]);

    public string OutputDirectory { get; } = flags["out"];

    /// <summary>Defaults first, then whatever the argv overrides, so the report always lists every flag.</summary>
    public static Dictionary<string, string> Parse(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["headless"] = "false",
            ["min-sessions"] = "2",
            ["max-sessions"] = "6",
            ["width"] = "1400",
            ["height"] = "900",
            ["settle-ms"] = "700",
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
