using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The cost ceiling a plan clears before the operator ever approves it (AC-256). Until this existed the only thing
/// steering model choice was brief text, and the pilot run put 88.9% of its tokens on a model near the expensive end
/// while never once reaching for either of the two cheapest — a brief asks, it does not enforce, which is the same
/// lesson AC-433 learned about review scope.
/// <para>
/// Expressed purely as a position in the list the profile's own provider ranked
/// (<see cref="PluginProfileInfo.ModelCostEstimatesCheapestFirst"/>), never as a model name: the CEO brief is
/// deliberately provider-neutral and this rule has to be able to say the same. A fraction rather than a fixed index,
/// so the meaning does not change when a provider offers two models instead of four.
/// </para>
/// </summary>
internal static class AutopilotModelTier
{
    /// <summary>
    /// How many of a profile's ranked models a step that is not a review gate may choose from, counting from the
    /// cheapest. <see cref="AutopilotCostStrategy.CostFirst"/> means the cheapest only — its own instruction already
    /// says escalate only after a cheaper model has actually failed. <see cref="AutopilotCostStrategy.Balanced"/>
    /// allows the cheaper half, which is what moves the measured mix without reserving the top tiers from the gates
    /// that need them. Always at least one, so a profile offering a single model can still run it.
    /// </summary>
    internal static int AllowedCount(int rankedModels, AutopilotCostStrategy strategy) => strategy switch
    {
        AutopilotCostStrategy.QualityFirst => rankedModels,
        AutopilotCostStrategy.CostFirst => 1,
        _ => Math.Max(1, rankedModels / 2),
    };

    /// <summary>
    /// Checks one step against the ceiling, returning the refusal to hand back to the CEO or null when the step is
    /// within it. Silent on everything it cannot judge rather than guessing: a review gate (where a missed finding
    /// costs more than the tokens), a profile whose provider ranks nothing, and a model absent from that ranking all
    /// pass. Kept static and free of the endpoint so it is tested directly.
    /// </summary>
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
        if (chosen < 0 || chosen < allowed)
        {
            return null;
        }

        var permitted = string.Join(", ", ranked.Take(allowed).Select(estimate => estimate.Model));
        return $"Step \"{step.Id}\" is not a review gate, so under the {strategy} cost strategy it must run on one of "
            + $"profile \"{profile.Label}\"'s cheaper models ({permitted}); it has \"{step.Model}\". Move it to one of "
            + "those. Only the operator can lift this, by setting the cost strategy to QualityFirst.";
    }

    /// <summary>Every step against the ceiling, returning the first refusal so the CEO fixes one thing at a time.</summary>
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
