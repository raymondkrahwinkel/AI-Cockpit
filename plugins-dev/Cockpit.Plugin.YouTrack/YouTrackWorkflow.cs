namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Starting an issue, as Raymond works: move it to "in progress" and put his name on it. Which value that is
/// depends on the board — an ordinary field calls it "In Progress", a workflow-governed one fires an event
/// called something like "start progress" — so the target is picked from what the project actually offers
/// (<see cref="YouTrackStateField.AvailableTargets"/>) instead of being written into the cockpit.
/// <para>
/// Creating the branch is deliberately <em>not</em> here: that is git's business, and baking one branch
/// convention into an issue integration would impose it on anyone else who uses the plugin. The plugin offers
/// the name (<see cref="BranchName"/>); the workflow plugin (#69) is where "start ticket → create branch" becomes
/// a step you compose yourself.
/// </para>
/// </summary>
internal sealed class YouTrackWorkflow(YouTrackClient client)
{
    /// <summary>The target that means "I am working on this now", or null when the board offers nothing like it — in which case Start is not offered at all rather than guessing.</summary>
    public static string? FindStartTarget(YouTrackStateField field)
    {
        var targets = field.AvailableTargets;

        return targets.FirstOrDefault(target => string.Equals(target, "In Progress", StringComparison.OrdinalIgnoreCase))
            ?? targets.FirstOrDefault(target => target.Contains("progress", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Moves the issue to <paramref name="target"/> and assigns it to the token's own account. Returns what happened, in the operator's words.</summary>
    public async Task<string> StartAsync(YouTrackInstance instance, YouTrackIssue issue, YouTrackIssueFields fields, string target, CancellationToken cancellationToken)
    {
        if (fields.State is not { } state)
        {
            throw new InvalidOperationException($"{issue.IdReadable} has no status field, so it cannot be started.");
        }

        await client.SetStateAsync(instance.InstanceUrl, instance.Token, issue, state, target, cancellationToken);

        if (fields.AssigneeFieldName is not { } assigneeField)
        {
            return $"{issue.IdReadable} → {target} (this project has no assignee field).";
        }

        try
        {
            await client.AssignToMeAsync(instance.InstanceUrl, instance.Token, issue, assigneeField, cancellationToken);
        }
        catch (Exception exception)
        {
            // The status already moved, so saying only "it failed" would be a lie about where the issue stands.
            return $"{issue.IdReadable} → {target}, but assigning it to you failed: {exception.Message}";
        }

        return $"{issue.IdReadable} → {target}, assigned to you.";
    }
}
