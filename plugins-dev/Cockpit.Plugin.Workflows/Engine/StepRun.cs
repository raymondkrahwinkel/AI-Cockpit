using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>One step of a run: what it was handed, what it produced, and what became of it.</summary>
public sealed class StepRun
{
    public required string NodeId { get; init; }

    public required string NodeName { get; init; }

    public required string TypeId { get; init; }

    public RunStatus Status { get; set; } = RunStatus.Running;

    /// <summary>What the step produced, as text — a command's output, the message that was sent. The engine's own record of what actually happened.</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>
    /// What the step handed on, kept as data rather than as a sentence: this is what the node dialog shows in its
    /// output pane, and where the fields a later step may refer to come from. Recorded from what actually flowed —
    /// a list of what a step <em>might</em> produce would be a guess.
    /// </summary>
    public IReadOnlyList<JsonObject> Items { get; set; } = [];

    /// <summary>The names of the fields it handed on.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Fields => Items.FirstOrDefault()?.Select(entry => entry.Key).ToList() ?? [];

    /// <summary>Whether the operator asked this step to print what it produced, in full.</summary>
    public bool Traced { get; set; }

    /// <summary>Why it failed, or why it was passed by.</summary>
    public string? Note { get; set; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; set; }

    public TimeSpan Duration => (FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}
