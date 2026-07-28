using Cockpit.Plugin.LocalCi.Runtime;
using Cockpit.Plugin.LocalCi.Ui;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Plugin.LocalCi;

/// <summary>
/// Local CI plugin entry point (AC-448). The floor the rest of the feature stands on: it works out whether this
/// machine can run a workflow job at all — Docker in three states rather than two, Linux containers, the act
/// runtime. It runs nothing itself.
/// </summary>
public sealed class LocalCiPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "local-ci",
        DisplayName: "Local CI",
        Author: "Cockpit",
        Description: "Work out whether this machine can run your GitHub workflow jobs locally, before promising " +
            "anything. Docker is reported in three states — missing, installed but the engine is not answering, " +
            "or ready — and the plugin checks that the engine runs Linux containers and that the act runtime is on " +
            "PATH, naming the install command when it is not.");

    private LocalCiRuntime? _runtime;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        // One runtime for the whole plugin: the detection is the answer everything downstream reads, so it is taken
        // once and cached rather than re-probed per caller.
        var runtime = new LocalCiRuntime(new CliRunner());
        _runtime = runtime;

        host.AddSettings(() => new LocalCiSettingsControl(runtime));

        // Docker Desktop may well have been started while the dialog was open; a save is the cheapest honest moment
        // to stop trusting a stale answer.
        host.OnSettingsSaved(runtime.Invalidate);
    }

    public void Dispose() => _runtime?.Dispose();
}
