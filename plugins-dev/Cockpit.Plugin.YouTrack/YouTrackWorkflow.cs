namespace Cockpit.Plugin.YouTrack;

// Starting an issue: move it to "in progress" and put the token owner's name on it. Which value that is
// depends on the board — an ordinary field calls it "In Progress", a workflow-governed one fires an event
// called something like "start progress" — so the target is picked from what the project actually offers
// (`YouTrackStateField.AvailableTargets`) instead of being written into the cockpit.
//
// Creating the branch is deliberately *not* here: that is git's business, and baking one branch
// convention into an issue integration would impose it on anyone else who uses the plugin. The plugin offers
// the name (`BranchName`); the workflow plugin (#69) is where "start ticket → create branch" becomes
// a step you compose yourself.
internal sealed class YouTrackWorkflow(YouTrackClient client)
{
    // Moves the issue to `target` and assigns it to the token's own account. Returns what happened, in the operator's words.
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
