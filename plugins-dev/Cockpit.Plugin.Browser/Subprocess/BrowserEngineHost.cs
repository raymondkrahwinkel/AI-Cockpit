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
            connection = new BrowserIpcConnection(pipes.Input, pipes.Output, generation, (message, _) =>
            {
                if (message.Operation != "engine.ready")
                {
                    throw new InvalidDataException("Unknown engine operation.");
                }
                return Task.FromResult(JsonSerializer.SerializeToElement(new { Initialized = initialized, ProcessId = Environment.ProcessId }));
            }, (generation, "enginehost"));
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
                CefRuntime.Shutdown();
            }
        }
    }
}
