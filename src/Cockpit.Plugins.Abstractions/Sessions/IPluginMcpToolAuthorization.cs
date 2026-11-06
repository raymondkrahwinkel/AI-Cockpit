namespace Cockpit.Plugins.Abstractions.Sessions;

public interface IPluginMcpToolAuthorization
{
    /// <summary>True only when the canonical tool call can run without a client permission prompt.</summary>
    bool MayCallWithoutPrompt(string canonicalName, string argumentsJson);
}
