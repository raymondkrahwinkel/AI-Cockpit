using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Autopilot;

// The cost ceiling a plan clears before the operator approves it (AC-256) — a pilot run put 88.9% of its
// tokens on the second-dearest model, never touching the cheapest. Expressed as a fractional position in the
// profile's own ranked list, never a model name, so the CEO brief stays provider-neutral.
internal static class AutopilotModelTier
{
    // How many of a profile's ranked models a non-review step may choose from, counting from the cheapest.
    // CostFirst means cheapest only; Balanced allows the cheaper half. Always at least one, so a profile with a
    // single model can still run it.
    internal static int AllowedCount(int rankedModels, AutopilotCostStrategy strategy) => strategy switch
    {
        AutopilotCostStrategy.QualityFirst => rankedModels,
        AutopilotCostStrategy.CostFirst => 1,
        _ => Math.Max(1, rankedModels / 2),
    };

    // Checks one step against the ceiling, returning a refusal or null when within it. Silent on what it cannot
    // judge: a review gate (a missed finding costs more than the tokens), an unranked profile, or an unranked model.
    internal static string? Validate(AutopilotStep step, IReadOnlyList<PluginProfileInfo> profiles, AutopilotCostStrategy strategy)
    {
        if (strategy is AutopilotCostStrategy.QualityFirst || step.IsReviewGate || string.IsNullOrWhiteSpace(step.Model))
        {
            return null;
        }

        var profile = profiles.FirstOrDefault(candidate => string.Equals(candidate.Label, step.ProfileLabel, StringComparison.Ordinal));
        if (profile?.ModelCostEstimatesCheapestFirst is not { Count: > 0 } ranked)
        {
            return null;
        }

        var chosen = _IndexOf(ranked, step.Model);
        var allowed = AllowedCount(ranked.Count, strategy);
        if (chosen >= 0 && chosen < allowed)
        {
            return null;
        }

        // A model the profile offers but did not price is not the same as an unrankable one, and must not be waved
        // through: the allowed set is "the cheapest N of the ranking", so something outside the ranking is not in it.
        // Passing it would let a provider that priced only its cheap models opt its dear ones out of the ceiling.
        var permitted = string.Join(", ", ranked.Take(allowed).Select(estimate => estimate.Model));
        return chosen < 0
            ? $"Step \"{step.Id}\" runs on \"{step.Model}\", which profile \"{profile.Label}\" offers but has not "
              + $"priced, so it cannot be shown to sit within the cost ceiling. A step that is not a review gate must "
              + $"run on one of ({permitted})."
            : $"Step \"{step.Id}\" is not a review gate, so under the {strategy} cost strategy it must run on one of "
              + $"profile \"{profile.Label}\"'s cheaper models ({permitted}); it has \"{step.Model}\". Move it to one of "
              + "those. Only the operator can lift this, by setting the cost strategy to QualityFirst.";
    }

    // The same rule applied to a step nobody planned: `AutopilotRunDriver`'s synthesized fix step inherits a
    // review gate's profile/model, but unlike the gate it is not exempt from the ceiling. Over it, moves to the
    // dearest still-allowed model rather than the cheapest, to keep as much capability as the ceiling permits.
    internal static AutopilotStep HoldToCeiling(AutopilotStep step, IReadOnlyList<PluginProfileInfo> profiles, AutopilotCostStrategy strategy)
    {
        if (Validate(step, profiles, strategy) is null)
        {
            return step;
        }

        // Validate only returns a refusal once it has found the profile and a non-empty ranking, so both hold here.
        var profile = profiles.First(candidate => string.Equals(candidate.Label, step.ProfileLabel, StringComparison.Ordinal));
        var ranked = profile.ModelCostEstimatesCheapestFirst;

        // The dearest the ceiling allows, but only one the profile actually offers — a provider can price a model
        // it doesn't list, and moving onto that would swap a cost problem for a launch-time failure. Nothing
        // offered within the ceiling leaves the step alone: over budget beats unable to start.
        var affordable = ranked
            .Take(AllowedCount(ranked.Count, strategy))
            .Select(estimate => estimate.Model)
            .LastOrDefault(model => profile.ModelSuggestions.Contains(model, StringComparer.OrdinalIgnoreCase));

        return affordable is null ? step : step.WithProfile(step.ProfileLabel, affordable);
    }

    // Every step against the ceiling, returning the first refusal so the CEO fixes one thing at a time.
    internal static string? ValidateAll(IReadOnlyList<AutopilotStep> steps, IReadOnlyList<PluginProfileInfo> profiles, AutopilotCostStrategy strategy)
    {
        foreach (var step in steps)
        {
            if (Validate(step, profiles, strategy) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    private static int _IndexOf(IReadOnlyList<PluginModelCostEstimate> ranked, string? model)
    {
        for (var index = 0; index < ranked.Count; index++)
        {
            if (string.Equals(ranked[index].Model, model, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
