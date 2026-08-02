namespace Cockpit.Plugin.LocalCi.Workflows;

// A parsed workflow file, kept deliberately close to what is written in the YAML: the keys a job and a step
// actually carry, not an interpretation of them. The classification is a separate step, so anything this model
// does not name survives as a key the classifier can refuse rather than as something silently dropped.
//
// `Keys`: The file's own top-level keys. Kept for the same reason a job's are: a setting that applies to
// every job in the file decides as much about whether they can run as anything written inside them.
internal sealed record WorkflowDocument(string Path, string Name, IReadOnlyList<string> Keys, IReadOnlyList<WorkflowJob> Jobs);

// `Keys`: Every key written under this job, in file order — including ones this plugin has no meaning for.
// `StrategyKeys`: The keys under `strategy`, empty when there is none. Kept rather than reduced to a
// matrix flag, because a strategy without a matrix is still one run and should not be refused for existing.
internal sealed record WorkflowJob(
    string Id,
    string? Name,
    RunsOnSpec RunsOn,
    IReadOnlyList<string> StrategyKeys,
    IReadOnlyList<string> Keys,
    IReadOnlyList<WorkflowStep> Steps)
{
    // One job in the file, many runs on GitHub.
    public bool HasMatrix => StrategyKeys.Contains("matrix", StringComparer.OrdinalIgnoreCase);
}

// `Keys`: Every key written on this step, in file order.
// `Uses`: The action reference, e.g. `actions/checkout@v7`, or null for a `run:` step.
internal sealed record WorkflowStep(IReadOnlyList<string> Keys, string? Uses)
{
    // The action without its version — `actions/checkout@v7` becomes `actions/checkout`.
    public string? ActionId => Uses?.Split('@', 2)[0].Trim();
}

// What `runs-on` said. A separate shape because "a single label" is only one of the things that may be
// written there, and the other forms are precisely the ones that must not be guessed at.
internal sealed record RunsOnSpec(RunsOnKind Kind, string? Label)
{
    public static RunsOnSpec Missing { get; } = new(RunsOnKind.Missing, null);

    public static RunsOnSpec Expression { get; } = new(RunsOnKind.Expression, null);

    public static RunsOnSpec NotUnderstood { get; } = new(RunsOnKind.NotUnderstood, null);

    public static RunsOnSpec Named(string label) => new(RunsOnKind.Label, label);
}

internal enum RunsOnKind
{
    // No `runs-on` at all.
    Missing,

    // A single plain label, e.g. `ubuntu-latest`.
    Label,

    // A `${{ … }}` expression — what it resolves to is GitHub's business, not something to assume.
    Expression,

    // A list, a group/labels mapping, or anything else this plugin has no reading for.
    NotUnderstood,
}

// Either a parsed document or the reason it could not be parsed. A broken workflow is a thing to report, not an
// exception to let out into a settings page.
internal sealed record WorkflowParseResult(string Path, WorkflowDocument? Document, string? Error)
{
    public static WorkflowParseResult Parsed(WorkflowDocument document) => new(document.Path, document, null);

    public static WorkflowParseResult Failed(string path, string error) => new(path, null, error);

    public bool IsParsed => Document is not null;
}
