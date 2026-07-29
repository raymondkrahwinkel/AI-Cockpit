using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.LocalCi;

/// <summary>
/// What the operator gets to decide about local runs. Read fresh on every access, so a change takes effect on the
/// next run rather than on the next restart.
/// </summary>
internal sealed class LocalCiSettings(IPluginStorage storage)
{
    /// <summary>
    /// The image act runs a Linux job in. Overridable because act's own documentation is explicit that its images
    /// differ from GitHub's runner images: a project whose jobs need a tool the medium image lacks has nowhere else
    /// to say so, and the alternative — this plugin guessing a bigger image for everyone — costs 50 GB.
    /// </summary>
    /// <summary>Whether sessions are offered the <c>cockpit-local-ci</c> tools at all.</summary>
    public bool McpEnabled
    {
        get => storage.Get<bool?>("mcpEnabled") ?? true;
        set => storage.Set("mcpEnabled", value);
    }

    public string RunnerImage
    {
        get => storage.Get<string>("runnerImage") is { Length: > 0 } image ? image : ActRunOptions.DefaultRunnerImage;
        set => storage.Set("runnerImage", value?.Trim() ?? string.Empty);
    }
}
