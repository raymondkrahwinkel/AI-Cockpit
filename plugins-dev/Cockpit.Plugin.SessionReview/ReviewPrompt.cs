namespace Cockpit.Plugin.SessionReview;

/// <summary>The prompt injected into a session to ask it to review its own uncommitted changes (AC-50).</summary>
internal static class ReviewPrompt
{
    public static string Build(string branch)
    {
        var where = string.IsNullOrWhiteSpace(branch) ? "this working directory" : $"branch '{_Safe(branch)}'";
        return $"Review the uncommitted changes on {where} for correctness and quality — run /code-review over the diff "
            + "and report the findings before anything is committed.";
    }

    // The branch name is embedded in a prompt sent to the agent; bound its length and drop any newline/quote so a
    // crafted ref name cannot break out of the sentence or smuggle instructions.
    private static string _Safe(string branch)
    {
        var cleaned = new string([.. branch.Where(c => c is not ('\n' or '\r' or '\'' or '"')).Take(120)]);
        return cleaned.Trim();
    }
}
