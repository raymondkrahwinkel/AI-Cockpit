using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Workflows.Engine;

// One step of a run: what it was handed, what it produced, and what became of it.
public sealed class StepRun
{
    public required string NodeId { get; init; }

    public required string NodeName { get; init; }

    public required string TypeId { get; init; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    // What the step produced, as text — a command's output, the message that was sent. The engine's own record of what actually happened.
    public string Output { get; set; } = string.Empty;

    // What the step handed on, kept as data rather than as a sentence: this is what the node dialog shows in its
    // output pane, and where the fields a later step may refer to come from. Recorded from what actually flowed —
    // a list of what a step *might* produce would be a guess.
    public IReadOnlyList<JsonObject> Items { get; set; } = [];

    // The names of the fields it handed on.
    [JsonIgnore]
    public IReadOnlyList<string> Fields => Items.FirstOrDefault()?.Select(entry => entry.Key).ToList() ?? [];

    // Whether the operator asked this step to print what it produced, in full.
    public bool Traced { get; set; }

    // Why it failed, or why it was passed by.
    public string? Note { get; set; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; set; }

    public TimeSpan Duration => (FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}
