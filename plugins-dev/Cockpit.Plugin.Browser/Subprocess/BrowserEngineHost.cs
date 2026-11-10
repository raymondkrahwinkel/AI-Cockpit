using System.Text.Json;
using Cockpit.Plugin.Browser.Host;
using Cockpit.Plugin.Browser.Ipc;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.Subprocess;

internal static class BrowserEngineHost
{
    internal static int Run(string[] args, string runtimeDir)
    {
        string Argument(string name) => args.Single(argument => argument.StartsWith(name, StringComparison.Ordinal))[name.Length..];
        var generation = Guid.ParseExact(Argument("--cockpit-generation="), "N");
        string storage = Argument("--cockpit-buffer-root=");
        string cache = Argument("--cockpit-cache-root=");
        storage = BrowserIpcStorage.ValidateDirectory(storage);
        cache = BrowserIpcStorage.PrepareCache(cache);
        var pipes = BrowserIpcPipes.OpenChild(Argument("--cockpit-input="), Argument("--cockpit-output="));
        bool initialized = false;
        BrowserIpcConnection? connection = null;
        BrowserHostTabs? tabs = null;
        try
        {
            try
            {
                BrowserHostRuntime.Load(runtimeDir);
                var mainArgs = new CefMainArgs(OperatingSystem.IsLinux() ? Environment.GetCommandLineArgs() : args);
                CefRuntime.Initialize(mainArgs, BrowserHostRuntime.CreateSettings(runtimeDir, Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path."), cache),
                    BrowserHostRuntime.CreateApp(runtimeDir, BrowserHostRuntime.Switches(args.Contains("--cockpit-hardware-acceleration"))), nint.Zero);
                initialized = true;
            }
            catch (Exception)
            {
                // Report a finite startup failure over IPC without leaking local paths or exception details.
            }
            // AC-2148: a DevTools call waiting on its page must not hold up input or close of any tab.
            connection = new BrowserIpcConnection(pipes.Input, pipes.Output, generation, (message, token) =>
            {
                if (message.Operation == "engine.ready" && message.TabId == generation)
                {
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { Initialized = initialized, ProcessId = Environment.ProcessId }));
                }
                return tabs?.HandleAsync(message, token) ?? throw new InvalidDataException("Unknown engine operation.");
            }, (generation, "enginehost"), message => message.Operation == "tab.devtools");
            tabs = new BrowserHostTabs(connection, storage, cache);
            connection.Completion.GetAwaiter().GetResult();
            return initialized ? 0 : 1;
        }
        finally
        {
            if (connection is not null)
            {
                connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else
            {
                pipes.Input.Dispose();
                pipes.Output.Dispose();
            }
            if (initialized)
            {
                // CEF must shut down on its startup thread, after every browser has closed.
                if (tabs is not null && tabs.DisposeAsync().AsTask().Wait(BrowserIpcLimits.Deadline) == false)
                {
                    throw new TimeoutException("Browser tabs did not close before CEF shutdown.");
                }
                CefRuntime.Shutdown();
            }
        }
    }
}
