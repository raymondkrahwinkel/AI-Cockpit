using Cockpit.Plugin.LocalCi.Execution;

namespace Cockpit.Plugin.LocalCi.Tests;

// AC-617: a run that fell over before this project's own work began is a fact about the machine, not a verdict on
// the code. The lines here are act's real output — the shape it prints, kept verbatim so a change in that format
// fails a test rather than quietly turning every environment failure back into "build failed".
public class SetupFailureTests
{
    private static readonly string[] TheseActions = ["actions/checkout@v7", "actions/setup-dotnet@v6"];

    [Fact]
    public void AFailureFetchingAnActionIsNotAVerdictOnTheCode()
    {
        // The reported case, verbatim: Docker 29.7.0 refused to copy the action into the container, six seconds in,
        // with nothing of the project compiled.
        string[] output =
        [
            "[CI/build] ⭐ Run Main actions/checkout@v7",
            "[CI/build]   ✅  Success - Main actions/checkout@v7 [1.076647339s]",
            "[CI/build] ⭐ Run Main actions/setup-dotnet@v6",
            "[CI/build]   ❌  Failure - Main actions/setup-dotnet@v6 [22.869215ms]",
            "[CI/build] failed to copy content to container: Error response from daemon: statat var/run/act/actions/actions-setup-dotnet@v6/.git: path escapes from parent",
        ];

        var reason = SetupFailure.Reason(output, TheseActions);

        Assert.NotNull(reason);
        Assert.Contains("actions/setup-dotnet@v6", reason);
        Assert.Contains("says nothing", reason);
    }

    [Fact]
    public void AFailureBringingTheContainerUpIsNotAVerdictEither()
    {
        string[] output = ["[CI/build]   ❌  Failure - Set up job", "[CI/build] Error: failed to pull image"];

        Assert.NotNull(SetupFailure.Reason(output, TheseActions));
    }

    [Fact]
    public void AFailingStepOfTheProjectStaysAFailure()
    {
        // The guard that matters most. A classification that swallows a real failure is worse than the confusion it
        // was written to remove: the operator is told their machine is at fault and pushes a broken diff.
        string[] output =
        [
            "[CI/build] ⭐ Run Main actions/setup-dotnet@v6",
            "[CI/build]   ✅  Success - Main actions/setup-dotnet@v6 [3.1s]",
            "[CI/build] ⭐ Run Main Build",
            "[CI/build] error CS0103: The name 'Foo' does not exist in the current context",
            "[CI/build]   ❌  Failure - Main Build [12.4s]",
        ];

        Assert.Null(SetupFailure.Reason(output, TheseActions));
    }

    [Fact]
    public void AProjectStepNamedLikeAnActionIsStillTheProjects()
    {
        // Why the job's own uses: list is read rather than the name's shape guessed at. A run: step may be called
        // anything at all, and "it has a slash and an @, so it must be an action" would hand a real compile failure
        // back as somebody else's problem.
        string[] output = ["[CI/build]   ❌  Failure - Main deploy/staging@nightly"];

        Assert.Null(SetupFailure.Reason(output, TheseActions));
    }

    [Fact]
    public void TheFirstFailureIsTheOneThatCounts()
    {
        // act stops at the first failure; what follows is the tidying up. Reading the last one would describe the
        // cleanup and call a compile error an environment problem.
        string[] output =
        [
            "[CI/build]   ❌  Failure - Main Build [12.4s]",
            "[CI/build]   ❌  Failure - Main actions/checkout@v7",
        ];

        Assert.Null(SetupFailure.Reason(output, TheseActions));
    }

    [Fact]
    public void OutputWithNoFailureLineAtAllIsNotClaimedAsAnEnvironmentProblem()
    {
        // act exited non-zero without naming a failed step — it never got going itself. Real, but not this
        // classification's to name, and guessing here would relabel anything unfamiliar as "your machine".
        Assert.Null(SetupFailure.Reason(["[CI/build] Error: workflow file could not be read"], TheseActions));
    }

    [Fact]
    public void AJobThatUsesNoActionsCanOnlyFailOnItsOwnSteps()
    {
        Assert.Null(SetupFailure.Reason(["[CI/build]   ❌  Failure - Main Build"], []));
    }
}
