// AC-1353 spike (F0 of AC-1350): how far does the backend core get without Cockpit.App and without Avalonia?
// Every line prefixed SPIKE| is a measurement. Not production code; never merged.
//
//   COCKPIT_STATE_ROOT=<fresh dir> dotnet run -- [--plugin <entry.dll>]... [--session] [--no-hosted]
//
// Phases: compose Core+Infrastructure DI, load plugin entry assemblies, resolve every registered service one by one
// (recording which one fails and which one drags an Avalonia assembly in), Initialize each plugin against a
// recording ICockpitHost proxy, start the hosted services, and optionally run one Claude SDK turn.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Cockpit.Core;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var pluginPaths = args.Select((a, i) => (a, i)).Where(x => x.a == "--plugin").Select(x => Path.GetFullPath(args[x.i + 1])).ToList();
var runSession = args.Contains("--session");
var runHosted = !args.Contains("--no-hosted");

if (args.Contains("--contract"))
{
    // Which members of the plugin contract carry an Avalonia type in their signature (F2's split line).
    static bool IsAv(Type t) => (t.Namespace?.StartsWith("Avalonia") ?? false) || (t.IsGenericType && t.GetGenericArguments().Any(IsAv)) || (t.HasElementType && IsAv(t.GetElementType()!));
    var total = 0; var ui = 0;
    foreach (var type in typeof(ICockpitHost).Assembly.GetExportedTypes().OrderBy(t => t.FullName))
    {
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m is MethodInfo { IsSpecialName: false } or PropertyInfo or EventInfo).ToList();
        var avMembers = members.Where(m => m switch
        {
            MethodInfo mi => IsAv(mi.ReturnType) || mi.GetParameters().Any(p => IsAv(p.ParameterType)),
            PropertyInfo pi => IsAv(pi.PropertyType),
            EventInfo ei => IsAv(ei.EventHandlerType!),
            _ => false,
        }).Select(m => m.Name).Distinct().ToList();
        total += members.Count; ui += avMembers.Count;
        if (avMembers.Count > 0 || (type.BaseType is { } b && IsAv(b)))
            Report.Line("contract-avalonia", $"{type.FullName} base={(type.BaseType is { } bb && IsAv(bb) ? bb.Name : "-")} members={avMembers.Count}/{members.Count} [{string.Join(",", avMembers)}]");
    }

    Report.Line("contract-summary", $"types={typeof(ICockpitHost).Assembly.GetExportedTypes().Length} members={total} avaloniaMembers={ui}");
    return 0;
}

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COCKPIT_STATE_ROOT")))
{
    Console.Error.WriteLine("Refusing to run without COCKPIT_STATE_ROOT: the spike must never touch a real state root.");
    return 2;
}

TaskScheduler.UnobservedTaskException += (_, e) => Report.Line("unobserved-task-exception", Report.Root(e.Exception));
AppDomain.CurrentDomain.UnhandledException += (_, e) => Report.Line("unhandled-exception", e.ExceptionObject.ToString()!.ReplaceLineEndings(" "));
Report.Line("start", $"stateRoot={Environment.GetEnvironmentVariable("COCKPIT_STATE_ROOT")} os={Environment.OSVersion}");
Report.Avalonia("P0-baseline");

// P1: the same composition Program.Main does, minus typeof(Program).Assembly.
var services = new ServiceCollection();
var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
services.AddSingleton(loggerFactory);
services.AddLogging();
services.AddCore().AddInfrastructure().AddServices(
    typeof(Cockpit.Core.DependencyInjection).Assembly,
    typeof(Cockpit.Infrastructure.DependencyInjection).Assembly);
Report.Line("P1-compose", $"descriptors={services.Count}");
Report.Avalonia("P1-compose");

// P2: load each plugin entry assembly the way PluginActivator does, and let it register services.
var plugins = new List<ICockpitPlugin>();
foreach (var path in pluginPaths)
{
    try
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var context = new SpikeLoadContext(path);
        var assembly = context.LoadFromAssemblyPath(path);
        Report.Avalonia($"P2a-{name}-assembly-loaded");
        var manifest = Path.Combine(Path.GetDirectoryName(path)!, "plugin.json");
        var entryName = File.Exists(manifest)
            ? System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest)).RootElement.GetProperty("entryType").GetString()
            : null;
        var entry = entryName is not null
            ? assembly.GetType(entryName, throwOnError: true)!
            : assembly.GetTypes().Single(t => typeof(ICockpitPlugin).IsAssignableFrom(t) && !t.IsAbstract);
        Report.Avalonia($"P2b-{name}-entry-type");
        var plugin = (ICockpitPlugin)Activator.CreateInstance(entry)!;
        Report.Avalonia($"P2c-{name}-instance");
        if (args.Contains("--jit-probe"))
        {
            _ = typeof(ICockpitHost).TypeHandle;
            Report.Avalonia($"P2d-{name}-ICockpitHost-typeof");
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(entry.GetMethod("Initialize")!.MethodHandle);
            Report.Avalonia($"P2e-{name}-Initialize-jitted");
        }
        var before = services.Count;
        plugin.ConfigureServices(services);
        plugins.Add(plugin);
        Report.Line("P2-plugin-load", $"ok {Path.GetFileNameWithoutExtension(path)} addedDescriptors={services.Count - before}");
    }
    catch (Exception e)
    {
        Report.Line("P2-plugin-load", $"FAIL {Path.GetFileNameWithoutExtension(path)} {Report.Root(e)}");
    }

    Report.Avalonia($"P2-after-{Path.GetFileNameWithoutExtension(path)}");
}

// P1b (static): every constructor dependency of a Core/Infrastructure/plugin service that nothing registers.
// These are the seams Cockpit.App fills today; each is a type the backend needs from outside Core+Infrastructure.
var registered = services.Select(d => d.ServiceType).ToHashSet();
var unmet = new Dictionary<string, SortedSet<string>>();
foreach (var d in services.Where(d => d.ImplementationType is not null))
{
    var ctor = d.ImplementationType!.GetConstructors(BindingFlags.Public | BindingFlags.Instance).OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
    if (ctor is null) continue;
    foreach (var p in ctor.GetParameters().Where(p => !p.HasDefaultValue))
    {
        var t = p.ParameterType;
        var ok = registered.Contains(t)
            || (t.IsGenericType && (registered.Contains(t.GetGenericTypeDefinition())
                || t.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
            || t == typeof(IServiceProvider);
        if (!ok)
        {
            (unmet.TryGetValue(t.FullName!, out var who) ? who : unmet[t.FullName!] = []).Add(d.ImplementationType.Name);
        }
    }
}

foreach (var (type, who) in unmet.OrderBy(u => u.Key))
{
    Report.Line("P1b-unmet-dependency", $"{type} <- {string.Join(",", who)}");
}

// P1d (static): the MCP endpoints resolve their tool types lazily, so P1b cannot see what they need. Walk every
// endpoint's tool type: constructor parameters and tool-method parameters that are interfaces nothing registers.
foreach (var d in services.Where(d => d.ServiceType == typeof(Cockpit.Core.Abstractions.Mcp.CockpitMcpEndpoint)))
{
    Cockpit.Core.Abstractions.Mcp.CockpitMcpEndpoint? endpoint;
    try { endpoint = (Cockpit.Core.Abstractions.Mcp.CockpitMcpEndpoint?)(d.ImplementationInstance ?? d.ImplementationFactory?.Invoke(new ServiceCollection().BuildServiceProvider())); }
    catch { endpoint = null; }
    if (endpoint is null) { Report.Line("P1d-mcp-endpoint", "(factory needs the container; skipped)"); continue; }
    var toolType = endpoint.ToolsType;
    var needs = toolType.GetConstructors().SelectMany(c => c.GetParameters())
        .Concat(toolType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).SelectMany(m => m.GetParameters()))
        .Select(p => p.ParameterType).Where(t => t.IsInterface && !registered.Contains(t) && t.Namespace?.StartsWith("Cockpit") == true)
        .Select(t => t.Name).Distinct().Order().ToList();
    Report.Line("P1d-mcp-endpoint", $"{endpoint.ServerName} ({toolType.Name}) unmet=[{string.Join(",", needs)}]");
}

// The seams P1b found are filled with null stubs so the run gets further; each one is a blocker, not a fix.
if (!args.Contains("--no-stubs"))
{
    foreach (var type in new[] { typeof(Cockpit.Core.Abstractions.Sessions.ISessionWorkspaces), typeof(Cockpit.Core.Abstractions.Agents.IPaneWorkspaceDirectory),
                 typeof(Cockpit.Core.Abstractions.Mcp.IMcpServerCatalog), typeof(Cockpit.Core.Abstractions.Voice.IUiHitchProbe) })
    {
        services.AddSingleton(type, _ => NullProxy.For(type));
        Report.Line("P1c-stubbed-seam", type.FullName!);
    }
}

// P3: build and resolve every closed service type one at a time.
var provider = services.BuildServiceProvider();
var failures = new List<(string Service, string Why)>();
foreach (var type in services.Select(d => d.ServiceType).Where(t => !t.ContainsGenericParameters).Distinct())
{
    var before = Report.AvaloniaSet();
    try
    {
        provider.GetServices(type).ToList();
    }
    catch (Exception e)
    {
        failures.Add((type.FullName ?? type.Name, Report.Root(e)));
    }

    var added = Report.AvaloniaSet().Except(before).ToList();
    if (added.Count > 0)
    {
        Report.Line("P3-avalonia-pulled-by", $"{type.FullName} -> {string.Join(",", added)}");
    }
}

foreach (var group in failures.GroupBy(f => f.Why).OrderByDescending(g => g.Count()))
{
    Report.Line("P3-resolve-fail", $"{group.Count()}x {group.Key} :: {string.Join(", ", group.Select(f => f.Service).Take(12))}");
}

Report.Line("P3-resolve", $"types={services.Select(d => d.ServiceType).Distinct().Count()} failed={failures.Count}");
Report.Avalonia("P3-resolve-all");

// P4: Initialize every plugin against a host that forwards the registries that live in Core/Infrastructure and
// records every other call — that record is the list of host capabilities a plugin backend asks for.
// P7 seeding (before Initialize, so the Workflows plugin reads it): a delegation profile and one scheduled flow.
var flowMinutes = args.Contains("--flow") ? int.Parse(args[Array.IndexOf(args, "--flow") + 1]) : 0;
if (flowMinutes > 0)
{
    var profileStore = provider.GetRequiredService<Cockpit.Core.Abstractions.Profiles.ISessionProfileStore>();
    await profileStore.SaveAsync([new SessionProfile("spike-haiku",
        new PluginProviderConfig("claude", Environment.GetEnvironmentVariable("SPIKE_CLAUDE_CONFIG_JSON") ?? "{}"),
        Delegation: new DelegationPolicy(AllowedAsTarget: true, MaxConcurrent: 2))]);
    var active = args.Contains("--flow-off") ? "false" : "true";
    HostProxy.SeedStorage("workflows", "workflows", $$$"""
        [{"Id":"spike-flow","Name":"spike","IsActive":{{{active}}},"RunUnattended":true,
          "Nodes":[{"Id":"t","TypeId":"cockpit.schedule","Name":"Every minute","Parameters":{"When":"every 1m"}},
                   {"Id":"d","TypeId":"cockpit.delegate","Name":"Delegate","Parameters":{"Profile":"spike-haiku","Prompt":"Antwoord alleen: OK"}}],
          "Connections":[{"FromNodeId":"t","FromOutput":0,"ToNodeId":"d"}]}]
        """);
    Report.Line("P7-seed", $"profile spike-haiku + flow every 1m active={active}");
}

var host = HostProxy.Create(provider);
Report.Avalonia("P4-host-proxy-created");
foreach (var plugin in plugins)
{
    var avaloniaBefore = Report.AvaloniaSet();
    HostProxy.Current = plugin.Metadata.Id;
    try
    {
        plugin.Initialize(host);
        var pulled = Report.AvaloniaSet().Except(avaloniaBefore).ToList();
        if (pulled.Count > 0) Report.Line("P4-avalonia-pulled-by", $"{plugin.Metadata.Id} -> {string.Join(",", pulled)}");
        Report.Line("P4-plugin-init", $"ok {plugin.Metadata.Id}");
    }
    catch (Exception e)
    {
        Report.Line("P4-plugin-init", $"FAIL {plugin.Metadata.Id} {Report.Root(e)}");
    }
}

foreach (var call in HostProxy.Calls.OrderBy(c => c.Key))
{
    Report.Line("P4-host-call", $"{call.Key} x{call.Value}");
}

Report.Avalonia("P4-plugin-init");

// P5: the hosted services Program.Main starts by hand.
var startedHosted = new List<IHostedService>();
if (runHosted)
{
    foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)))
    {
        IHostedService hosted;
        try
        {
            hosted = (IHostedService)(descriptor.ImplementationInstance
                ?? descriptor.ImplementationFactory?.Invoke(provider)
                ?? provider.GetRequiredService(descriptor.ImplementationType!));
        }
        catch (Exception e)
        {
            Report.Line("P5-hosted", $"CANNOT-BUILD {descriptor.ImplementationType?.FullName ?? "factory"} {Report.Root(e)}");
            continue;
        }

        startedHosted.Add(hosted);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await hosted.StartAsync(timeout.Token);
            Report.Line("P5-hosted", $"ok {hosted.GetType().FullName}");
        }
        catch (Exception e)
        {
            Report.Line("P5-hosted", $"FAIL {hosted.GetType().FullName} {Report.Root(e)}");
        }
    }

    Report.Avalonia("P5-hosted");
}

// P6: one Claude SDK turn through ISessionManager, no viewmodel in between.
if (runSession)
{
    var manager = provider.GetRequiredService<ISessionManager>();
    var profile = new SessionProfile("spike-haiku", new PluginProviderConfig("claude", Environment.GetEnvironmentVariable("SPIKE_CLAUDE_CONFIG_JSON") ?? "{}"));
    var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var runtime = manager.Create(profile);
    runtime.EventAppended += e =>
    {
        Report.Line("P6-event", e.ToString()!.Length > 400 ? e.ToString()![..400] : e.ToString()!);
        if (e is TurnCompleted or SessionError)
        {
            done.TrySetResult(e.ToString() ?? "");
        }
    };

    try
    {
        var workdir = Directory.CreateTempSubdirectory("ac1353-").FullName;
        await runtime.StartAsync(profile, permissionMode: "default", model: "haiku", workingDirectory: workdir);
        await runtime.SendUserMessageAsync("Antwoord alleen: OK");
        var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromMinutes(3)));
        Report.Line("P6-session", finished == done.Task
            ? $"turn-ended last=\"{runtime.LastAssistantText}\""
            : "TIMEOUT no TurnCompleted in 3 min");
        await manager.StopAsync(runtime.Id);
        Report.Line("P6-session", $"stopped sessionsLeft={manager.Sessions.Count}");
    }
    catch (Exception e)
    {
        Report.Line("P6-session", $"FAIL {Report.Root(e)}");
    }

    Report.Avalonia("P6-session");
}

if (flowMinutes > 0)
{
    Report.Line("P7-flow", $"observing for {flowMinutes} min");
    for (var minute = 0; minute < flowMinutes; minute++)
    {
        await Task.Delay(TimeSpan.FromMinutes(1));
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
    Report.Line("P7-flow", $"delegate-calls={SpikeActions.Delegations} completed={SpikeActions.Completed} answers=[{string.Join(" | ", SpikeActions.Answers)}]");
    Report.Avalonia("P7-flow");
}

Report.Avalonia("END");
if (File.Exists("/proc/self/maps"))
{
    var natives = File.ReadAllLines("/proc/self/maps").Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
        .Where(p => p.EndsWith(".so") || p.Contains(".so.")).Select(Path.GetFileName).Distinct().Order().ToList();
    Report.Line("native-libs", string.Join(",", natives));
}
if (runHosted)
{
    foreach (var hosted in startedHosted)
    {
        try { await hosted.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); } catch { }
    }
}

Environment.Exit(0);
return 0;

static class Report
{
    public static void Line(string phase, string text) => Console.WriteLine($"SPIKE|{phase}|{text}");

    public static HashSet<string> AvaloniaSet() =>
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "")
            .Where(n => n.StartsWith("Avalonia", StringComparison.Ordinal)).ToHashSet();

    public static void Avalonia(string phase)
    {
        var loaded = AvaloniaSet();
        Line("avalonia", $"{phase} count={loaded.Count} [{string.Join(",", loaded.Order())}]");
    }

    // The innermost message is the one that names the missing type.
    public static string Root(Exception e)
    {
        while (e.InnerException is not null && e is not InvalidOperationException { InnerException: null })
        {
            e = e.InnerException;
        }

        return $"{e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}";
    }
}

sealed class SpikeLoadContext(string mainAssemblyPath) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName name) =>
        _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;

    protected override nint LoadUnmanagedDll(string name) =>
        _resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : nint.Zero;
}

// Forwards the calls that have a Core/Infrastructure registry behind them — the same one-liners CockpitHost has —
// and counts every other one so the report can say which host capability a plugin backend reached for.
public class HostProxy : DispatchProxy
{
    public static readonly ConcurrentDictionary<string, int> Calls = new();
    public static string Current = "";
    private static readonly ConcurrentDictionary<string, SpikeStorage> Storages = new();
    public static void SeedStorage(string pluginId, string key, string json) => Storages.GetOrAdd(pluginId, _ => new SpikeStorage()).Set(key, json);
    private IServiceProvider _services = null!;

    public static ICockpitHost Create(IServiceProvider services)
    {
        var proxy = DispatchProxy.Create<ICockpitHost, HostProxy>();
        ((HostProxy)(object)proxy)._services = services;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var name = method!.Name;
        switch (name)
        {
            case "AddSessionProvider":
                _services.GetRequiredService<Cockpit.Infrastructure.Sessions.IPluginProviderRegistry>().Register((Cockpit.Plugins.Abstractions.Sessions.SessionProviderRegistration)args![0]!);
                return null;
            case "AddTtyProvider":
                _services.GetRequiredService<Cockpit.Infrastructure.Sessions.Tty.IPluginTtyProviderRegistry>().Register((Cockpit.Plugins.Abstractions.Sessions.TtyProviderRegistration)args![0]!);
                return null;
            case "AddManagedCli":
                _services.GetRequiredService<Cockpit.Infrastructure.ManagedCli.IManagedCliService>().Register((Cockpit.Plugins.Abstractions.ManagedCli.ManagedCliDescriptor)args![0]!);
                return null;
            case "get_Services":
                return _services;
            case "get_Storage":
                return Storages.GetOrAdd(Current, _ => new SpikeStorage());
            case "get_Cache":
                return new InMemoryPluginCache();
            case "get_Actions":
                return SpikeActions.Create(_services);
            case "get_Sessions":
                return NullProxy.For(typeof(Cockpit.Plugins.Abstractions.Sessions.ICockpitSessionObserver));
            case "ResolveManagedCliPath":
                return _services.GetService<Cockpit.Infrastructure.ManagedCli.IManagedCliService>()?.ResolveInstalledPath((string)args![0]!);
        }

        Calls.AddOrUpdate($"{Current}: {name}({string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name))})", 1, (_, n) => n + 1);
        var returnType = method.ReturnType;
        if (returnType == typeof(void)) return null;
        if (returnType == typeof(Task)) return Task.CompletedTask;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = returnType.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
        }

        return NullProxy.Empty(returnType);
    }
}

// Answers every member with its default, so a seam Cockpit.App fills today can be left empty on purpose.
public class NullProxy : DispatchProxy
{
    public static object For(Type type) => DispatchProxy.Create(type, typeof(NullProxy));

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var returnType = method!.ReturnType;
        if (returnType == typeof(void)) return null;
        if (returnType == typeof(Task)) return Task.CompletedTask;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = returnType.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [Empty(inner)]);
        }

        return Empty(returnType);
    }

    public static object? Empty(Type type)
    {
        if (type.IsValueType) return Activator.CreateInstance(type);
        if (type.IsGenericType && type.GetGenericTypeDefinition() is var g
            && (g == typeof(IReadOnlyList<>) || g == typeof(IEnumerable<>) || g == typeof(IReadOnlyCollection<>)))
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
            return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(type.GetGenericArguments()));
        return null;
    }
}

// Stand-in for Cockpit.App.Plugins.PluginStorage, which is in App: an in-memory key/value store.
public sealed class SpikeStorage : IPluginStorage
{
    private readonly ConcurrentDictionary<string, object?> _values = new();
    public T? Get<T>(string key) => _values.TryGetValue(key, out var v) && v is T t ? t : default;
    public void Set<T>(string key, T value) => _values[key] = value;
    public void Remove(string key) => _values.TryRemove(key, out _);
}

// ICockpitActions for the spike: DelegateAsync goes to the real IDelegationService (as PluginActions does);
// every other member is a UI action on the selected session and is only counted.
public class SpikeActions : DispatchProxy
{
    public static int Delegations;
    public static int Completed;
    public static readonly ConcurrentQueue<string> Answers = new();
    private IServiceProvider _services = null!;

    public static ICockpitActions Create(IServiceProvider services)
    {
        var proxy = DispatchProxy.Create<ICockpitActions, SpikeActions>();
        ((SpikeActions)(object)proxy)._services = services;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "DelegateAsync")
        {
            Interlocked.Increment(ref Delegations);
            Report.Line("P7-delegate", $"fired at {DateTimeOffset.Now:HH:mm:ss}");
            return _DelegateAsync((string)args![0]!, (string)args[1]!, (string?)args[2]);
        }

        HostProxy.Calls.AddOrUpdate($"actions: {method.Name}", 1, (_, n) => n + 1);
        return method.ReturnType == typeof(Task) ? Task.CompletedTask
            : method.ReturnType == typeof(bool) ? false : null;
    }

    private async Task<string> _DelegateAsync(string profile, string prompt, string? directory)
    {
        var delegation = _services.GetRequiredService<Cockpit.Core.Abstractions.Delegation.IDelegationService>();
        var task = await delegation.DelegateAsync(new Cockpit.Core.Abstractions.Delegation.DelegationRequest(profile, prompt, WorkingDirectory: directory));
        while (true)
        {
            await Task.Delay(500);
            var current = delegation.GetTask(task.TaskId);
            if (current is null || current.Status is Cockpit.Core.Delegation.DelegatedTaskStatus.Queued or Cockpit.Core.Delegation.DelegatedTaskStatus.Running)
            {
                if (current is null) return "(task vanished)";
                continue;
            }

            Interlocked.Increment(ref Completed);
            var answer = $"{current.Status}: {current.Result ?? current.Error}";
            Answers.Enqueue(answer.Length > 80 ? answer[..80] : answer);
            return current.Result ?? "";
        }
    }
}
