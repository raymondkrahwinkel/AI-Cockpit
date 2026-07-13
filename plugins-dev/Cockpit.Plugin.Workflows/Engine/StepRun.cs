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

    /// <summary>The field names the step handed on. What a later step can refer to with <c>{field}</c> — recorded from what actually flowed, so the settings panel can list it instead of guessing.</summary>
    public IReadOnlyList<string> Fields { get; set; } = [];

    /// <summary>Why it failed, or why it was passed by.</summary>
    public string? Note { get; set; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; set; }

    public TimeSpan Duration => (FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}
