using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// Runs a plugin's <see cref="IPluginTtyProvider"/> as one of the cockpit's own <see cref="ITtySessionProvider"/>s.
/// The two contracts say the same thing in two vocabularies — the plugin SDK cannot reference the core's types
/// without binding every plugin to the core's version of them — so this is where one becomes the other.
/// </summary>
/// <remarks>
/// Nothing is lost across this seam, and that is worth saying out loud: the TTY contract is small enough that a
/// plugin can express all of it. The session-driver adapter (<c>PluginSessionDriverAdapter</c>) cannot say that —
/// it drops the working directory, the MCP selection, resume, and half the events — which is precisely why Claude
/// could not be hosted as a session-driver plugin, and can be hosted as a TTY one.
/// </remarks>
internal sealed class PluginTtySessionProviderAdapter(
    string providerId,
    IPluginTtyProvider inner,
    string configJson) : ITtySessionProvider
{
    public string ProviderId => providerId;

    public TtyLaunchSpec BuildLaunch(TtyLaunchContext context)
    {
        var spec = inner.BuildLaunch(new PluginTtyLaunchContext(
            configJson,
            context.Options,
            context.WorkingDirectory,
            _Resume(context.Resume),
            context.BaseEnvironment));

        return new TtyLaunchSpec(
            spec.ExecutablePath,
            spec.Arguments,
            spec.EnvironmentOverlay,
            spec.WorkingDirectory,
            spec.SessionScopedFiles);
    }

    /// <summary>
    /// A plugin says "resume this conversation, or the last one" and nothing else. The core's <see cref="SessionResume"/>
    /// also has a "start fresh" case, which is the absence of a resume — so it maps to null rather than to an
    /// object that says nothing.
    /// </summary>
    private static PluginTtyResume? _Resume(SessionResume? resume) => resume switch
    {
        { Mode: SessionResumeMode.MostRecent } => new PluginTtyResume(null),
        { Mode: SessionResumeMode.BySessionId, SessionId: { } id } when !string.IsNullOrWhiteSpace(id) => new PluginTtyResume(id.Trim()),
        _ => null,
    };
}
