namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// The tools the host mounted for one session, for a driver that runs the model's tool loop itself (AC-964).
/// The host owns connecting, permission-gating and the transcript rows, so a driver only builds and calls.
/// </summary>
/// <remarks>
/// Handed to <see cref="IPluginSessionDriver.StartAsync(string?, string?, string?, IReadOnlyDictionary{string, string}?, IReadOnlyList{PluginMcpServer}?, IReadOnlyDictionary{string, string}?, IPluginToolset?, CancellationToken)"/>
/// for a provider whose registration declares a <see cref="PluginHostToolLoop"/> other than
/// <see cref="PluginHostToolLoop.None"/>. It is the alternative to <see cref="PluginMcpServer"/>: a driver
/// takes either the endpoints to mount itself, or this, never both.
/// </remarks>
public interface IPluginToolset
{
    /// <summary>
    /// The tools to offer the model on every turn — already narrowed by the host, so a driver passes the whole
    /// list on rather than filtering it.
    /// </summary>
    IReadOnlyList<PluginToolDescriptor> Tools { get; }

    /// <summary>
    /// Every tool name this session can reach, including those <see cref="Tools"/> leaves behind a tool-search
    /// proxy. What a driver reports as <c>PluginSessionInitialized.Tools</c>, so the header's count is the real one.
    /// </summary>
    IReadOnlyList<string> ReachableToolNames { get; }

    /// <summary>
    /// Runs one tool and returns its result as the model should see it. A refused or failed call comes back as
    /// result text rather than an exception, so the loop continues and the model can react.
    /// </summary>
    /// <param name="name">The tool's <see cref="PluginToolDescriptor.Name"/>.</param>
    /// <param name="argumentsJson">The call's arguments as a JSON object.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<string> InvokeAsync(string name, string argumentsJson, CancellationToken cancellationToken = default);
}
