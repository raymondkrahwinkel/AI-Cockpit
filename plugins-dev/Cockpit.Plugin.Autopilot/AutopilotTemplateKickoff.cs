namespace Cockpit.Plugin.Autopilot;

// The CEO's opening kickoff for a planning round when a template is (or is not) chosen (AC-189, slice 3). No
// template keeps current behaviour (tracker kickoff, or idle for a CEO-first run); a chosen template's
// `Body` resolved through `AutopilotTemplateResolver` becomes the kickoff instead. Kept a pure builder so the rule is unit-testable without a live session or UI.
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

    // The intent-Data view of a plan source, keyed the way `AutopilotTemplateResolver` expects, so a
    // template's `{{issue.*}}` tokens fill from the triggering item — present even when blank so it is never
    // reported missing. Null when there is no source, so every issue token is reported missing instead.
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
