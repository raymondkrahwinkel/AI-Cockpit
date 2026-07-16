namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Option keys the host bridges from its own typed session-start surface into a plugin driver's
/// <see cref="IPluginSessionDriver.StartAsync(string?, string?, string?, System.Collections.Generic.IReadOnlyDictionary{string, string}?, System.Collections.Generic.IReadOnlyList{PluginMcpServer}?, System.Threading.CancellationToken)"/>
/// options map. The host's <c>ISessionDriver.StartAsync</c> carries a typed <c>permissionMode</c> (a Claude concept
/// that predates the plugin surface); the plugin contract has no such parameter, so a provider that understands
/// Claude-style permission modes declares a launch option under <see cref="PermissionMode"/> and the host's driver
/// adapter folds the operator's selection into the options map under that key. A provider that has no permission modes
/// (an HTTP model, Codex's sandbox) simply never declares the option and never reads it.
/// </summary>
public static class WellKnownPluginSessionOptions
{
    /// <summary>The option key by which a plugin driver receives the host's Claude-style permission-mode selection.</summary>
    public const string PermissionMode = "permission-mode";

    /// <summary>
    /// The option key for the model — the host's driver adapter wires its typed <c>SetModelAsync</c> to a live
    /// <see cref="IPluginSessionDriver.SetLiveOptionAsync"/> under this key, so a plugin that declares
    /// <see cref="PluginSessionCapabilities.SupportsLiveModelSwitch"/> receives a mid-session model change.
    /// </summary>
    public const string Model = "model";
}
