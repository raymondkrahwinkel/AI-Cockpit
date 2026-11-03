using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

// Copying a flow into a new one (#69) — what duplicating, starting from a template and importing a file all do. Two
// flows sharing a step id are one flow with two names, and the wires, which remember the steps they run between,
// would follow the wrong one.
public class WorkflowCopyTests
{
    // A flow you have not read is not one that should already be running.
    [Fact]
    public void ACopy_IsNeverArmed_HoweverTheOriginalCame()
    {
        var source = new Workflow { Id = "w", Name = "Flow", IsActive = true };

        Assert.False(WorkflowCopy.Of(source, "Flow").IsActive);
    }

    // A hand-edited or truncated file can name a step that is not there. Dropping that wire beats carrying it into a
    // flow that would fail to run for reasons nobody could see on the canvas.
}
