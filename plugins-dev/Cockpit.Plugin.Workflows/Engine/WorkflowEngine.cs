using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// Runs a flow (#69): starts at a trigger, walks the wires, and hands each step what the one before it produced.
/// The engine knows the <em>shape</em> of a flow — order, branches, what is switched off — and nothing about what a
/// step means; that belongs to the runners.
/// <para>
/// Two rules that are not obvious and are not negotiable. A step type this build cannot execute is
/// <see cref="RunStatus.Skipped"/> with a reason, never a success — a flow that reports green while doing nothing is
/// the worst thing this could be. And loops are allowed by the model, so the engine has a ceiling on how many steps
/// one run may take: a flow that never ends must fail loudly rather than hang the cockpit.
/// </para>
/// </summary>
public sealed class WorkflowEngine(IReadOnlyList<IStepRunner> runners)
{
    /// <summary>How many steps one run may take. A loop with a decision as its stop condition is normal; a loop without one is a bug, and this is where it surfaces.</summary>
    public const int MaxSteps = 200;

    private readonly Dictionary<string, IStepRunner> _runners =
        runners.ToDictionary(runner => runner.TypeId, StringComparer.Ordinal);

    /// <summary>Runs the flow from <paramref name="startNodeId"/> — a trigger. Never throws: what went wrong is written into the run, which is the point of keeping one.</summary>
    public async Task<WorkflowRun> RunAsync(Workflow workflow, string startNodeId, CancellationToken cancellationToken = default)
    {
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid().ToString("n"),
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
            StartedAt = DateTimeOffset.UtcNow,
        };

        if (workflow.Node(startNodeId) is null)
        {
            run.Status = RunStatus.Failed;
            run.Error = "That step is not in this flow.";
            run.FinishedAt = DateTimeOffset.UtcNow;
            return run;
        }

        // A trigger wired to nothing is not a flow that succeeded in zero work — it is a flow that was never
        // finished, and reporting it green is how you end up trusting a run that did nothing.
        if (!workflow.Connections.Any(connection => connection.FromNodeId == startNodeId))
        {
            run.Status = RunStatus.Failed;
            run.Error = $"'{workflow.Node(startNodeId)!.Name}' is wired to nothing, so there was nothing to run. Draw a wire from it to the step that should follow.";
            run.FinishedAt = DateTimeOffset.UtcNow;
            return run;
        }

        // Breadth-first from the trigger: each pending entry is a step and the items handed to it. Fan-out simply
        // means one step queues several.
        var pending = new Queue<(string NodeId, IReadOnlyList<WorkflowItem> Input)>();
        pending.Enqueue((startNodeId, [WorkflowItem.Empty()]));

        // What each step handed on, by name: this is what lets a parameter reach back past the step before it
        // ({Run a command.output}) rather than only to its immediate input.
        var produced = new Dictionary<string, IReadOnlyList<WorkflowItem>>(StringComparer.OrdinalIgnoreCase);

        var executed = 0;

        while (pending.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            if (++executed > MaxSteps)
            {
                run.Status = RunStatus.Failed;
                run.Error = $"The flow ran more than {MaxSteps} steps and was stopped. A loop with no way out?";
                break;
            }

            var (nodeId, input) = pending.Dequeue();
            if (workflow.Node(nodeId) is not { } node)
            {
                continue;
            }

            var step = new StepRun
            {
                NodeId = node.Id,
                NodeName = node.Name,
                TypeId = node.TypeId,
                Traced = node.IsTraced,
                StartedAt = DateTimeOffset.UtcNow,
            };
            run.Steps.Add(step);

            var outcome = await _RunStepAsync(new StepContext(node, input, produced), step, cancellationToken);
            step.FinishedAt = DateTimeOffset.UtcNow;
            produced[node.Name] = outcome.Items;

            if (step.Status == RunStatus.Failed)
            {
                // The branch stops here; the rest of the flow, on other branches, still runs. What failed is in the
                // run, and the run says so.
                run.Status = RunStatus.Failed;
                run.Error ??= $"'{node.Name}' failed: {step.Note}";
                continue;
            }

            _QueueNext(workflow, node, outcome, pending);
        }

        run.FinishedAt = DateTimeOffset.UtcNow;
        if (run.Status == RunStatus.Running)
        {
            run.Status = RunStatus.Succeeded;
        }

        return run;
    }

    private async Task<StepOutcome> _RunStepAsync(StepContext context, StepRun step, CancellationToken cancellationToken)
    {
        var (node, input, _) = context;

        // Switched off: the items pass through untouched, so the rest of the flow still runs on the data it would
        // have had. That is what "skip this step" means, and it is more useful than stopping.
        if (node.IsDisabled)
        {
            step.Status = RunStatus.Skipped;
            step.Note = "Switched off.";
            step.Items = RunItems.Keep(input);
            return StepOutcome.Passing(input, string.Empty);
        }

        if (!_runners.TryGetValue(node.TypeId, out var runner))
        {
            step.Status = RunStatus.Skipped;
            step.Note = $"This cockpit cannot run '{node.TypeId}' yet — the step was passed by, not performed.";
            step.Items = RunItems.Keep(input);
            return StepOutcome.Passing(input, string.Empty);
        }

        try
        {
            var outcome = await runner.RunAsync(context, cancellationToken);
            step.Status = RunStatus.Succeeded;
            step.Output = outcome.Output;
            step.Items = RunItems.Keep(outcome.Items);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            step.Status = RunStatus.Skipped;
            step.Note = "The run was stopped.";
            return StepOutcome.Passing(input, string.Empty);
        }
        catch (Exception exception)
        {
            step.Status = RunStatus.Failed;
            step.Note = exception.Message;
            return StepOutcome.Passing([], string.Empty);
        }
    }

    // Which way out a step leaves by is the step's own business: a decision picks a branch by naming it in its
    // outcome; everything else leaves by its one way out.
    private static void _QueueNext(Workflow workflow, WorkflowNode node, StepOutcome outcome, Queue<(string, IReadOnlyList<WorkflowItem>)> pending)
    {
        var branch = _ChosenBranch(node, outcome);

        foreach (var connection in workflow.Connections.Where(connection => connection.FromNodeId == node.Id))
        {
            if (branch is not null && connection.FromOutput != branch)
            {
                continue;
            }

            pending.Enqueue((connection.ToNodeId, outcome.Items));
        }
    }

    // A decision's runner says which branch it took by putting "true" or "false" in its output; anything else has
    // one way out and takes it.
    private static int? _ChosenBranch(WorkflowNode node, StepOutcome outcome)
    {
        if (node.Kind != WorkflowNodeKind.Decision)
        {
            return null;
        }

        var index = node.Outputs
            .Select((label, position) => (label, position))
            .FirstOrDefault(entry => string.Equals(entry.label, outcome.Output, StringComparison.OrdinalIgnoreCase));

        return index.label is null ? 0 : index.position;
    }
}
