namespace Cockpit.Plugin.SessionReview;

/// <summary>The prompt injected into a session to ask it to review its own uncommitted changes (AC-50).</summary>
internal static class ReviewPrompt
{
    public static string Build(string branch)
    {
        var where = string.IsNullOrWhiteSpace(branch) ? "this working directory" : $"branch '{branch}'";
        return $"Review the uncommitted changes on {where} for correctness and quality — run /code-review over the diff "
            + "and report the findings before anything is committed.";
    }
}
