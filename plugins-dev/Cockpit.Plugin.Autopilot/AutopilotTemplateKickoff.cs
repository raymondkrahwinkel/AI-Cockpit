namespace Cockpit.Plugin.Autopilot;

// The CEO's opening kickoff for a planning round when a template is (or is not) chosen in the plan flow (AC-189, slice
// 3). It turns the operator's template choice into the visible first turn the planning CEO is set going with:
// - No template — "free planning" — keeps the current behaviour exactly: the tracker kickoff
// (`AutopilotCeoBrief.SourceKickoff`) when the run was triggered from an item, else no kickoff at all so a
// CEO-first run stays idle waiting for the operator to say what it should achieve.
// - A chosen template — its `AutopilotTemplate.Body` resolved through
// `AutopilotTemplateResolver` (its `{{issue.*}}` tokens filled from the triggering item) becomes the
// kickoff instead, so the resolved brief is what the CEO drafts the plan from.
// Kept a pure builder so the "template body → resolved kickoff" rule is unit-testable without a live session or UI.
//
// `Message`: The first user turn to submit to the CEO, or null to leave the session idle (free CEO-first planning).
// `MissingPlaceholders`: The template placeholders that could not be filled, so the surface can warn; empty for free planning.
internal sealed record AutopilotKickoff(string? Message, IReadOnlyList<string> MissingPlaceholders);

internal static class AutopilotTemplateKickoff
{
    // Builds the CEO kickoff for a planning round. `template` is the operator's choice — null for free
    // planning; `source` is the triggering item, or null for a CEO-first run. Never throws: a template
    // whose placeholders cannot all be filled still yields a kickoff (the gaps left empty) with the missing names reported.
    public static AutopilotKickoff Build(AutopilotTemplate? template, AutopilotPlanSource? source)
    {
        if (template is null)
        {
            // Free planning — exactly the current behaviour: a tracker run kicks off from its item, a CEO-first run idles.
            var kickoff = source is { } item ? AutopilotCeoBrief.SourceKickoff(item) : null;
            return new AutopilotKickoff(kickoff, []);
        }

        var resolution = AutopilotTemplateResolver.Resolve(template.Body, SourceData(source));

        // A body that resolves to nothing but whitespace (e.g. only unfilled tokens on a CEO-first run) would submit an
        // empty turn; leave the CEO idle instead so it asks what the run should achieve, rather than sending a blank message.
        var message = string.IsNullOrWhiteSpace(resolution.Text) ? null : resolution.Text;
        return new AutopilotKickoff(message, resolution.MissingPlaceholders);
    }

    // The intent-Data view of a plan source, keyed the way `AutopilotTemplateResolver` expects
    // (`issue`/`title`/`description`/`url`/`tracker`), so a template's `{{issue.*}}` tokens
    // fill from the triggering item. The plan source now carries the item's url (from the intent's `url` key, AC-189),
    // so `{{issue.url}}` resolves to the real link — present, even when blank, so it is never reported missing from a
    // source. Null when there is no source (a CEO-first run), so every issue token is reported missing.
    public static IReadOnlyDictionary<string, string>? SourceData(AutopilotPlanSource? source) =>
        source is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["issue"] = source.IssueId,
                ["title"] = source.Title,
                ["description"] = source.Description,
                ["url"] = source.Url,
                ["tracker"] = source.Tracker,
            };
}
