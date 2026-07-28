namespace Cockpit.Plugin.LocalCi.Workflows;

/// <summary>
/// A parsed workflow file, kept deliberately close to what is written in the YAML: the keys a job and a step
/// actually carry, not an interpretation of them. The classification is a separate step, so anything this model
/// does not name survives as a key the classifier can refuse rather than as something silently dropped.
/// </summary>
internal sealed record WorkflowDocument(string Path, string Name, IReadOnlyList<WorkflowJob> Jobs);

/// <param name="Keys">Every key written under this job, in file order — including ones this plugin has no meaning for.</param>
/// <param name="HasMatrix">A <c>strategy.matrix</c> was present. One job in the file, many runs on GitHub.</param>
internal sealed record WorkflowJob(
    string Id,
    string? Name,
    RunsOnSpec RunsOn,
    bool HasMatrix,
    IReadOnlyList<string> Keys,
    IReadOnlyList<WorkflowStep> Steps);

/// <param name="Keys">Every key written on this step, in file order.</param>
/// <param name="Uses">The action reference, e.g. <c>actions/checkout@v7</c>, or null for a <c>run:</c> step.</param>
internal sealed record WorkflowStep(IReadOnlyList<string> Keys, string? Uses)
{
    /// <summary>The action without its version — <c>actions/checkout@v7</c> becomes <c>actions/checkout</c>.</summary>
    public string? ActionId => Uses?.Split('@', 2)[0].Trim();
}

/// <summary>
/// What <c>runs-on</c> said. A separate shape because "a single label" is only one of the things that may be
/// written there, and the other forms are precisely the ones that must not be guessed at.
/// </summary>
internal sealed record RunsOnSpec(RunsOnKind Kind, string? Label)
{
    public static RunsOnSpec Missing { get; } = new(RunsOnKind.Missing, null);

    public static RunsOnSpec Expression { get; } = new(RunsOnKind.Expression, null);

    public static RunsOnSpec NotUnderstood { get; } = new(RunsOnKind.NotUnderstood, null);

    public static RunsOnSpec Named(string label) => new(RunsOnKind.Label, label);
}

internal enum RunsOnKind
{
    /// <summary>No <c>runs-on</c> at all.</summary>
    Missing,

    /// <summary>A single plain label, e.g. <c>ubuntu-latest</c>.</summary>
    Label,

    /// <summary>A <c>${{ … }}</c> expression — what it resolves to is GitHub's business, not something to assume.</summary>
    Expression,

    /// <summary>A list, a group/labels mapping, or anything else this plugin has no reading for.</summary>
    NotUnderstood,
}

/// <summary>
/// Either a parsed document or the reason it could not be parsed. A broken workflow is a thing to report, not an
/// exception to let out into a settings page.
/// </summary>
internal sealed record WorkflowParseResult(string Path, WorkflowDocument? Document, string? Error)
{
    public static WorkflowParseResult Parsed(WorkflowDocument document) => new(document.Path, document, null);

    public static WorkflowParseResult Failed(string path, string error) => new(path, null, error);

    public bool IsParsed => Document is not null;
}
