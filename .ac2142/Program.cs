using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugin.Browser.UI.Engine;
using Xilium.CefGlue;

[SupportedOSPlatform("windows")]
internal static class Program
{
    internal static string[] Arguments = [];
    internal const string Runtime = "C:/ck/487726/ac2142/cef";

    [STAThread]
    private static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows() == false)
        {
            throw new PlatformNotSupportedException("AC-2142 is the Windows transport gate.");
        }
        NativeLibrary.SetDllImportResolver(typeof(CefRuntime).Assembly, (name, _, _) =>
            name == "libcef" ? NativeLibrary.Load(Path.Combine(Runtime, "libcef.dll")) : nint.Zero);
        if (args.Any(argument => argument.StartsWith("--type=", StringComparison.Ordinal)))
        {
            int exit = 0;
            var thread = new Thread(() =>
            {
                CefRuntime.Load(Runtime);
                exit = CefRuntime.ExecuteProcess(new CefMainArgs(args), new EngineApp(), nint.Zero);
            }, 8 * 1024 * 1024);
            thread.Start();
            thread.Join();
            return exit;
        }
        Arguments = args;
        return AppBuilder.Configure<ProbeApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
    }

    internal static void Initialize(string state)
    {
        Directory.CreateDirectory(state);
        CefRuntime.Load(Runtime);
        CefRuntime.Initialize(new CefMainArgs([]), new CefSettings
        {
            MultiThreadedMessageLoop = true,
            WindowlessRenderingEnabled = true,
            NoSandbox = true,
            CommandLineArgsDisabled = true,
            BrowserSubprocessPath = Environment.ProcessPath,
            RootCachePath = state,
            ResourcesDirPath = Runtime,
            LocalesDirPath = Path.Combine(Runtime, "locales"),
            LogFile = Path.Combine(state, "cef.log"),
            LogSeverity = CefLogSeverity.Warning,
        }, new EngineApp(), nint.Zero);
    }

    private sealed class EngineApp : CefApp
    {
        protected override void OnBeforeCommandLineProcessing(string processType, CefCommandLine commandLine)
        {
            commandLine.AppendSwitch("cockpit-cef-runtime-dir", Runtime);
            commandLine.AppendSwitch("autoplay-policy", "no-user-gesture-required");
        }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ProbeApplication : Application
{
    private readonly List<CefBrowserView> _views = [];
    private readonly List<SharedFrame> _buffers = [];
    private readonly List<Surface> _surfaces = [];
    private readonly Dictionary<int, TaskCompletionSource<(long FrameId, long Timestamp)>> _waiting = [];
    private readonly List<DispatcherTimer> _pollers = [];
    private readonly List<object> _correlations = [];
    private int _lastWidth;
    private int _lastHeight;
    private Process? _host;
    private Task? _hostOutput;
    private TaskCompletionSource? _ack;
    private long _consumed;
    private bool _engine;
    private int _width;
    private int _height;
    private double _scale;
    private int _tabs;
    private string _route = string.Empty;
    private string _state = string.Empty;
    private string _name = string.Empty;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        _desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime ?? throw new InvalidOperationException("Desktop lifetime missing.");
        string[] args = Program.Arguments;
        _route = args[0];
        _state = args[1];
        _width = int.Parse(args[2], CultureInfo.InvariantCulture);
        _height = int.Parse(args[3], CultureInfo.InvariantCulture);
        _scale = double.Parse(args[4], CultureInfo.InvariantCulture);
        _tabs = int.Parse(args[5], CultureInfo.InvariantCulture);
        _name = args[6];
        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _RunAsEventHandler();
    }

    private async void _RunAsEventHandler()
    {
        int exitCode = 0;
        try
        {
            if (_route == "host")
            {
                await _RunHostAsync();
            }
            else
            {
                await _RunParentAsync();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            exitCode = 1;
        }
        finally
        {
            foreach (DispatcherTimer timer in _pollers)
            {
                timer.Stop();
            }
            foreach (CefBrowserView view in _views)
            {
                await view.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (_engine)
            {
                CefRuntime.Shutdown();
            }
            if (_host is { } host)
            {
                if (host.HasExited == false)
                {
                    await host.StandardInput.WriteLineAsync("stop");
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    try
                    {
                        await host.WaitForExitAsync(budget.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        host.Kill(entireProcessTree: true);
                        await host.WaitForExitAsync();
                        exitCode = 1;
                    }
                }
                if (_hostOutput is not null)
                {
                    await _hostOutput;
                }
                host.Dispose();
            }
            foreach (SharedFrame buffer in _buffers)
            {
                buffer.Dispose();
            }
            _desktop?.Shutdown(exitCode);
        }
    }

    private void _CreateViews(bool host)
    {
        Program.Initialize(_state);
        _engine = true;
        var engine = new BrowserEngine();
        for (int tab = 0; tab < _tabs; tab++)
        {
            var profile = CefRequestContext.CreateContext(new CefRequestContextSettings(), null);
            var view = new CefBrowserView(engine, new Uri(Path.GetFullPath(".ac2142/page.html")).AbsoluteUri, profile);
            view.ProbeSize(_width, _height, _scale);
            view.FrameCopied = _FrameCopied;
            if (host)
            {
                var buffer = new SharedFrame(_name + "-" + tab, (int)(_width * _scale), (int)(_height * _scale), _scale, false, Program.Arguments.Contains("silent"), Program.Arguments.Contains("poll"));
                view.PaintOverride = buffer.Publish;
                _buffers.Add(buffer);
            }
            _views.Add(view);
        }
    }

    private async Task _RunHostAsync()
    {
        _CreateViews(true);
        while (await Console.In.ReadLineAsync() is { } command && command != "stop")
        {
            string[] parts = command.Split(' ');
            if (parts[0] == "animate")
            {
                await _AnimationAsync(parts[1] == "on");
            }
            else
            {
                await _InputAsync(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), parts[2]);
            }
            Console.WriteLine("ack");
        }
    }

    private async Task _RunParentAsync()
    {
        if (_route == "local")
        {
            _CreateViews(false);
        }
        else
        {
            for (int tab = 0; tab < _tabs; tab++)
            {
                var buffer = new SharedFrame(_name + "-" + tab, (int)(_width * _scale), (int)(_height * _scale), _scale, true, legacy: _route == "poll");
                buffer.FrameCopied = _FrameCopied;
                var surface = new Surface(buffer);
                buffer.Updated = surface.InvalidateVisual;
                _surfaces.Add(surface);
                _buffers.Add(buffer);
                if (_route == "poll")
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                    timer.Tick += (_, _) => buffer.Consume();
                    timer.Start();
                    _pollers.Add(timer);
                }
                else
                {
                    buffer.Listen();
                }
            }
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Executable missing."))
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in new[] { "host", _state, _width.ToString(CultureInfo.InvariantCulture), _height.ToString(CultureInfo.InvariantCulture), _scale.ToString(CultureInfo.InvariantCulture), _tabs.ToString(CultureInfo.InvariantCulture), _name })
            {
                start.ArgumentList.Add(argument);
            }
            if (_route == "silent")
            {
                start.ArgumentList.Add("silent");
            }
            if (_route == "poll")
            {
                start.ArgumentList.Add("poll");
            }
            _host = Process.Start(start) ?? throw new InvalidOperationException("Host did not start.");
            _hostOutput = _ReadHostAsync(_host);
        }

        var panel = new Grid();
        for (int tab = 0; tab < _tabs; tab++)
        {
            Control content = _route == "local" ? _views[tab] : _surfaces[tab];
            content.Width = _width;
            content.Height = _height;
            panel.Children.Add(content);
        }
        var window = new Window { Title = "AC-2142 disposable transport probe", Width = _width, Height = _height, WindowDecorations = WindowDecorations.None, CanResize = false, Content = panel, ShowActivated = false };
        window.Show();
        // The scenario scale is explicit, independent of the operator monitor's scaling.
        foreach (CefBrowserView view in _views)
        {
            view.ProbeSize(_width, _height, _scale);
        }
        await Task.Delay(2500);
        foreach (CefBrowserView view in _views)
        {
            view.ProbeSize(_width, _height, _scale);
        }
        await Task.Delay(500);
        ProcessTree.Snapshot startMetrics = ProcessTree.Capture();
        var memorySamples = new List<ProcessTree.Snapshot> { startMetrics };
        long measurementStart = Stopwatch.GetTimestamp();
        long consumedStart = _consumed;
        long copiedStart = _CopyBytes();
        long offeredStart = _Offered();
        var latency = new Dictionary<string, List<double>> { ["type"] = [], ["scroll"] = [] };
        int eventId = 10;
        foreach (string kind in latency.Keys)
        {
            int samples = Program.Arguments.Contains("--functional") ? 4 : 40;
            for (int sample = 0; sample < samples; sample++)
            {
                int tab = sample % _tabs;
                eventId++;
                var frame = new TaskCompletionSource<(long FrameId, long Timestamp)>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting.Add(eventId, frame);
                long started = Stopwatch.GetTimestamp();
                if (_route == "local")
                {
                    await _InputAsync(tab, eventId, kind);
                }
                else
                {
                    _ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await (_host ?? throw new InvalidOperationException("Host missing.")).StandardInput.WriteLineAsync($"{tab} {eventId} {kind}");
                    await _ack.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                (long FrameId, long Timestamp) received;
                try
                {
                    received = await frame.Task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException($"Missing frame-ready update: event={eventId}, tab={tab}, kind={kind}, consumed={_consumed}, expected={_width * _scale}x{_height * _scale}, actual={_lastWidth}x{_lastHeight}.");
                }
                _correlations.Add(new { eventId, frameId = received.FrameId, tab, kind, dispatched = started, copied = received.Timestamp });
                latency[kind].Add(Stopwatch.GetElapsedTime(started, received.Timestamp).TotalMilliseconds);
                await Task.Delay(20);
            }
            memorySamples.Add(ProcessTree.Capture());
        }
        await _SetAnimationAsync(true);
        long throughputStart = Stopwatch.GetTimestamp();
        long throughputConsumed = _consumed;
        long throughputOffered = _Offered();
        await Task.Delay(3000);
        double throughputSeconds = Stopwatch.GetElapsedTime(throughputStart).TotalSeconds;
        long animationConsumed = _consumed - throughputConsumed;
        long animationOffered = _Offered() - throughputOffered;
        await _SetAnimationAsync(false);
        ProcessTree.Snapshot endMetrics = ProcessTree.Capture();
        memorySamples.Add(endMetrics);
        double elapsedMs = Stopwatch.GetElapsedTime(measurementStart).TotalMilliseconds;
        if (startMetrics.Pids.SequenceEqual(endMetrics.Pids) == false)
        {
            throw new InvalidOperationException("Owned process tree changed during measurement; CPU delta is invalid.");
        }
        if (_lastWidth != (int)(_width * _scale) || _lastHeight != (int)(_height * _scale))
        {
            throw new InvalidOperationException($"Incorrect frame size: {_lastWidth}x{_lastHeight}.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            route = _route, width = _width, height = _height, scale = _scale, tabs = _tabs,
            latency, correlations = _correlations, consumed = _consumed - consumedStart,
            offered = _Offered() - offeredStart, copyBytes = _CopyBytes() - copiedStart,
            bufferAllocations = _views.Sum(view => view.BufferAllocations) + _buffers.Sum(buffer => buffer.BufferAllocations),
            transportAllocatedBytes = _buffers.Sum(buffer => buffer.AllocatedBytes),
            animation = new { offered = animationOffered, consumed = animationConsumed, seconds = throughputSeconds },
            cpuCorePercent = (endMetrics.CpuMilliseconds - startMetrics.CpuMilliseconds) / elapsedMs * 100,
            memorySamples, elapsedMs, hostPid = _host?.Id, parentPid = Environment.ProcessId,
            frameWidth = _lastWidth, frameHeight = _lastHeight,
        }));
        window.Close();
    }

    private long _CopyBytes() => _route == "local"
        ? _views.Sum(view => Interlocked.Read(ref view.CopyBytes))
        : _buffers.Sum(buffer => buffer.CopiedBytes + buffer.Counters().HostBytes);

    private long _Offered() => _route == "local"
        ? _views.Sum(view => Interlocked.Read(ref view.PaintCount))
        : _buffers.Sum(buffer => buffer.Counters().Offered);

    private async Task _SetAnimationAsync(bool enabled)
    {
        if (_route == "local")
        {
            await _AnimationAsync(enabled);
            return;
        }
        _ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await (_host ?? throw new InvalidOperationException("Host missing.")).StandardInput.WriteLineAsync(enabled ? "animate on" : "animate off");
        await _ack.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async Task _AnimationAsync(bool enabled)
    {
        foreach (CefBrowserView view in _views)
        {
            await view.DevToolsAsync("Runtime.evaluate", new Dictionary<string, object> { ["expression"] = enabled ? "window.animating=true" : "window.animating=false" });
        }
    }

    private async Task _ReadHostAsync(Process host)
    {
        while (await host.StandardOutput.ReadLineAsync() is { } line)
        {
            if (line == "ack")
            {
                _ack?.TrySetResult();
            }
            else
            {
                Console.Error.WriteLine("host: " + line);
            }
        }
    }

    private async Task _InputAsync(int tab, int id, string kind)
    {
        CefBrowserView view = _views[tab];
        string? result = await view.DevToolsAsync("Runtime.evaluate", new Dictionary<string, object>
        {
            ["expression"] = $"window.probeId={id}; document.querySelector('input').focus(); true",
            ["returnByValue"] = true,
        });
        if (result is null)
        {
            throw new InvalidOperationException("Page input arming failed.");
        }
        CefBrowser browser = view.ProbeBrowser ?? throw new InvalidOperationException("Browser not ready.");
        CefRuntime.PostTask(CefThreadId.UI, new InputTask(() =>
        {
            CefBrowserHost host = browser.GetHost();
            host.SetFocus(true);
            if (kind == "type")
            {
                host.SendKeyEvent(new CefKeyEvent { EventType = CefKeyEventType.Char, WindowsKeyCode = 'x', Character = 'x', UnmodifiedCharacter = 'x' });
            }
            else
            {
                host.SendMouseWheelEvent(new CefMouseEvent { X = 150, Y = 150 }, 0, id % 2 == 0 ? -120 : 120);
            }
        }));
    }

    private unsafe void _FrameCopied(nint address, int width, int height, int stride, long frameId)
    {
        _consumed++;
        _lastWidth = width;
        _lastHeight = height;
        int id = 0;
        for (int bit = 0; bit < 20; bit++)
        {
            int x = (int)((4 + bit * 4 + 1) * _scale);
            int y = (int)(5 * _scale);
            if (x < width && y < height && *((byte*)address + y * stride + x * 4) > 127)
            {
                id |= 1 << bit;
            }
        }
        if (_waiting.Remove(id, out TaskCompletionSource<(long FrameId, long Timestamp)>? waiting))
        {
            waiting.TrySetResult((frameId, Stopwatch.GetTimestamp()));
        }
    }

    private sealed class InputTask(Action action) : CefTask
    {
        protected override void Execute() => action();
    }

    private sealed class Surface(SharedFrame frame) : Control
    {
        public override void Render(DrawingContext context)
        {
            if (frame.Bitmap is { } bitmap)
            {
                context.DrawImage(bitmap, new Rect(Bounds.Size));
            }
        }
    }
}
