using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Browser.UI.Share;

internal sealed record LookAlongTab(string Id, string Title, string Address);

// AC-1715: an agent asks to look along. Nothing is read until Raymond picks a tab on the card; No, Escape and the
// timeout all end in a refusal that touched no tab.
internal static class TabLookAlong
{
    private const int ReasonLimit = 200;

    public static async Task<T> AskAsync<T>(
        string paneId,
        string? reason,
        IReadOnlyList<LookAlongTab> tabs,
        string? activeId,
        Func<ConsentRequest, Task<ConsentDecision>> consent,
        Func<string, Task<T>> shareAndRead,
        Func<string, T> refuse)
    {
        if (tabs.Count == 0)
        {
            return refuse("There is no browser tab open to look at.");
        }

        ConsentDecision decision;
        try
        {
            decision = await consent(Card(paneId, reason, tabs, activeId));
        }
        catch (OperationCanceledException)
        {
            decision = ConsentDecision.Denied;
        }

        return decision is { IsApproved: true, Chosen: { } chosen }
            && chosen.TryGetValue("tab", out var picked)
            && picked.Count == 1
            && tabs.Any(tab => tab.Id == picked[0])
                ? await shareAndRead(picked[0])
                : refuse("The operator did not share a tab. Do not ask again unless they invite you to.");
    }

    // The address as the card's first line, kept to what fits: the host and the last path segment.
    private static string Short(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.Host + (uri.AbsolutePath.Trim('/').Split('/') is { Length: > 1 } parts ? "/…/" + parts[^1] : uri.AbsolutePath.Length > 1 ? uri.AbsolutePath : string.Empty)
            : address;

    // The card sits in the asker's own conversation (its pane is the source). The reason is the agent's own words and
    // is shown as such, never as the card's framing.
    public static ConsentRequest Card(string paneId, string? reason, IReadOnlyList<LookAlongTab> tabs, string? activeId)
    {
        var selected = tabs.Any(tab => tab.Id == activeId) ? activeId : tabs[0].Id;
        var subject = Short(tabs.First(tab => tab.Id == selected).Address);
        var said = string.Join(' ', (reason ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (said.Length > ReasonLimit)
        {
            said = said[..ReasonLimit] + "...";
        }

        return new ConsentRequest(
            "Look along on a browser tab?",
            $"{subject} · your own profile, signed in\n"
            + (said.Length > 0 ? $"The session says: \"{said}\"\n" : string.Empty)
            + "Watching only: address, title, page text and a screenshot of the one tab you pick.",
            new ConsentSource(paneId, null, "Browser"),
            "browser.look-along",
            ConsentRisk.Dangerous)
        {
            Choices =
            [
                new ConsentChoice("tab", "Which tab", [.. tabs.Select(tab => new ConsentOption(tab.Id, tab.Title, tab.Address, Selected: tab.Id == selected))]),
            ],
            ApproveLabel = "Share this tab",
            Never = ["Cookies and storage", "saved passwords and autofill", "other tabs", "controlling the page"],
        };
    }
}
