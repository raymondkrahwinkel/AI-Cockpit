// AC-1896 spike (G7): load <plugin folder>/PluginProbe.dll the way Cockpit loads a plugin backend, and time each step.
// Usage: PluginHost <plugin folder> <fixture.md> <fixture.pm.json>
using System.Diagnostics;
using System.Runtime.InteropServices;
using Cockpit.Infrastructure.Plugins;

var sw = Stopwatch.StartNew();
var context = new PluginLoadContext(Path.GetFullPath(Path.Combine(args[0], "PluginProbe.dll")), refuseUiFor: "probe");
var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(args[0], "PluginProbe.dll")));
var run = assembly.GetType("PluginProbe.Probe")!.GetMethod("Run")!;
Console.WriteLine($"{RuntimeInformation.RuntimeIdentifier} ({RuntimeInformation.OSDescription.Trim()}): plugin loaded in {sw.ElapsedMilliseconds} ms (Jint untouched)");
Console.WriteLine(run.Invoke(null, [File.ReadAllText(args[1]), File.ReadAllText(args[2]).Replace("\r\n", "\n")]));
Console.WriteLine($"process private bytes {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576} MiB");
