using System.Text;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Kind.Security;

// The one place this plugin's security policy lives (AC-179): create and delete always ask Dangerous, never
// remembered, showing the literal kind argv, on scopes that differ so a create approval is never a delete one.
// The card renders the action verbatim and a cluster name is agent-supplied, so control characters are collapsed.
internal sealed class KindConsentGate(ICockpitHost host)
{
    // Null when the operator approved; otherwise the refusal an MCP tool hands straight back to the agent.
    public async Task<string?> AuthorizeAsync(string operation, string scope, string? paneId)
    {
        var request = new ConsentRequest(
            Title: "Kind: cluster lifecycle",
            Action: _Escape(operation),
            Source: new ConsentSource(paneId, PluginId: null, Label: "Kind"),
            Scope: scope,
            Risk: ConsentRisk.Dangerous,
            AllowRemember: false);

        var decision = await host.RequestConsentAsync(request);
        return decision.IsApproved ? null : "The operator did not approve this kind cluster action.";
    }

    private static string _Escape(string operation)
    {
        var escaped = new StringBuilder(operation.Length);
        foreach (var character in operation)
        {
            escaped.Append(char.IsControl(character) ? ' ' : character);
        }

        return escaped.ToString();
    }
}
