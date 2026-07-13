namespace Cockpit.Plugins.Abstractions.Workflows;

/// <summary>
/// A step a plugin contributes to the workflow editor (#69) — "Move a ticket to In Progress", "Comment on a pull
/// request". Without this, the steps a flow can take are whatever the workflows plugin happened to build, and every
/// integration the cockpit ever grows would have to be built there too, by someone who does not have the YouTrack
/// client in front of them.
/// <para>
/// A step is declared and run in one place, on purpose: what a step is called, what it asks for and what it does are
/// the same knowledge, and splitting them across a registry and a runner is how they drift apart.
/// </para>
/// <para>
/// Data is plain string fields, not the workflow engine's own item type. A plugin should not have to reference the
/// workflows plugin to add a step to it — the contract between them is this interface and nothing else.
/// </para>
/// </summary>
public interface IWorkflowStep
{
    /// <summary>Unique, and prefixed with the plugin: <c>youtrack.start</c>. It is stored in the flow, so it must not change once a flow uses it.</summary>
    string TypeId { get; }

    /// <summary>What it is called on the canvas: "Start a ticket".</summary>
    string Name { get; }

    /// <summary>One sentence: what it does, and anything the operator would be surprised by.</summary>
    string Description { get; }

    /// <summary>A single character or emoji for the card.</summary>
    string Icon { get; }

    /// <summary>The heading it appears under in the step picker: the plugin's own name reads best ("YouTrack").</summary>
    string Category { get; }

    /// <summary>The settings it asks for, in order. Each is a text field, and a value may carry <c>{output}</c> to use what an earlier step produced.</summary>
    IReadOnlyList<string> Parameters { get; }

    /// <summary>
    /// The ways out. One unnamed way out is the usual case, and the default. Naming several makes it a decision:
    /// the step says in <see cref="WorkflowStepResult.Branch"/> which one it took, and only that wire is followed.
    /// </summary>
    IReadOnlyList<string> Outputs => [string.Empty];

    /// <summary>What it typically hands on, with an example value — shown before a flow has ever run, so a step can be configured against its input rather than against a guess. Optional.</summary>
    IReadOnlyDictionary<string, string> Produces => new Dictionary<string, string>();

    /// <summary>
    /// Runs the step. Throwing fails it, and the message is what the operator reads in the run — so write it as a
    /// sentence they can act on. Returning without doing the work is not a failure the run can see, so do not.
    /// </summary>
    Task<WorkflowStepResult> RunAsync(WorkflowStepContext context, CancellationToken cancellationToken);
}

/// <summary>
/// What a step is handed when it runs: its own settings, already resolved (a <c>{output}</c> in a parameter has been
/// replaced with what the earlier step produced), and the data flowing into it.
/// </summary>
/// <param name="Parameters">The settings, by the names the step declared, with placeholders already filled.</param>
/// <param name="Input">What the step before handed over — usually one item.</param>
public sealed record WorkflowStepContext(
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Input)
{
    /// <summary>One setting, or empty when it was left blank.</summary>
    public string Parameter(string name) => Parameters.GetValueOrDefault(name, string.Empty);
}

/// <summary>What a step produced.</summary>
/// <param name="Items">The data the next step gets. Empty means "pass on what came in".</param>
/// <param name="Output">One line for the run log: what actually happened. This is what the operator reads afterwards.</param>
/// <param name="Branch">For a step with named ways out: which one was taken. Null for the usual single way out.</param>
public sealed record WorkflowStepResult(
    IReadOnlyList<IReadOnlyDictionary<string, string>> Items,
    string Output,
    string? Branch = null)
{
    /// <summary>A step that did something and has one thing to say about it, handing on a single named value.</summary>
    public static WorkflowStepResult Of(string field, string value, string output) =>
        new([new Dictionary<string, string> { [field] = value }], output);

    /// <summary>A step that did something and produced no data of its own.</summary>
    public static WorkflowStepResult Done(string output) => new([], output);
}
