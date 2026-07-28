using Cockpit.Plugin.LocalCi.Workflows;

namespace Cockpit.Plugin.LocalCi.Tests;

public class WorkflowParserTests
{
    [Fact]
    public void BrokenYaml_ReportsTheProblemInsteadOfThrowing()
    {
        var result = WorkflowParser.Parse("broken.yml", "jobs:\n  build:\n   - this: [is\n");

        Assert.False(result.IsParsed);
        Assert.StartsWith("This file is not valid YAML:", result.Error);
    }

    [Fact]
    public void FileWithoutJobs_ReportsThatThereIsNothingToRun()
    {
        var result = WorkflowParser.Parse("no-jobs.yml", "name: Nothing\non:\n  push:\n");

        Assert.False(result.IsParsed);
        Assert.Contains("no jobs: block", result.Error);
    }

    [Fact]
    public void FileThatIsNotAMapping_IsReportedNotThrown()
    {
        var result = WorkflowParser.Parse("list.yml", "- one\n- two\n");

        Assert.False(result.IsParsed);
        Assert.Contains("no top-level mapping", result.Error);
    }

    [Fact]
    public void WorkflowWithoutAName_FallsBackToItsFileName()
    {
        var result = WorkflowParser.Parse("ci.yml", "jobs:\n  build:\n    runs-on: ubuntu-latest\n");

        Assert.Equal("ci.yml", result.Document!.Name);
    }

    [Fact]
    public void JobKeysAndStepKeysAreKeptAsWritten()
    {
        // The classifier can only refuse what the parser hands over, so everything written has to survive parsing.
        var result = WorkflowParser.Parse("ci.yml", """
            name: CI
            jobs:
              build:
                name: Build it
                runs-on: ubuntu-latest
                needs: gate
                steps:
                  - uses: actions/checkout@v7
                    with:
                      fetch-depth: 0
            """);

        var job = Assert.Single(result.Document!.Jobs);
        Assert.Equal("build", job.Id);
        Assert.Equal("Build it", job.Name);
        Assert.Equal(RunsOnKind.Label, job.RunsOn.Kind);
        Assert.Equal("ubuntu-latest", job.RunsOn.Label);
        Assert.Equal(["name", "runs-on", "needs", "steps"], job.Keys);

        var step = Assert.Single(job.Steps);
        Assert.Equal(["uses", "with"], step.Keys);
        Assert.Equal("actions/checkout", step.ActionId);
    }

    [Fact]
    public void JobWhoseNameIsNotAPlainString_IsReportedInsteadOfDroppedSilently()
    {
        // Dropping it would leave a shorter job list with nothing saying anything was skipped — a job that vanishes
        // is worse than one that is refused, because the operator cannot see that it happened.
        var result = WorkflowParser.Parse("odd.yml", """
            jobs:
              ? [a, b]
              : runs-on: ubuntu-latest
              build:
                runs-on: ubuntu-latest
            """);

        Assert.False(result.IsParsed);
        Assert.Contains("not a plain string", result.Error);
    }

    [Fact]
    public void MoreThanOneYamlDocument_IsReportedInsteadOfSilentlyReadingTheFirst()
    {
        var result = WorkflowParser.Parse("two.yml", """
            jobs:
              build:
                runs-on: ubuntu-latest
            ---
            jobs:
              other:
                runs-on: self-hosted
            """);

        Assert.False(result.IsParsed);
        Assert.Contains("more than one YAML document", result.Error);
    }

    [Fact]
    public void TopLevelKeysAreKept()
    {
        var result = WorkflowParser.Parse("ci.yml", "name: CI\non:\n  push:\njobs:\n  build:\n    runs-on: ubuntu-latest\n");

        Assert.Equal(["name", "on", "jobs"], result.Document!.Keys);
    }

    [Fact]
    public void StepThatIsNotAMapping_IsReportedInsteadOfReadAsAnEmptyStep()
    {
        // Same class as the non-scalar job name: an unreadable shape must be reported, not turned into a step with
        // no keys — which would pass every check the classifier makes and come out runnable.
        var result = WorkflowParser.Parse("odd.yml", """
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - just a string
            """);

        Assert.False(result.IsParsed);
        Assert.Contains("not a mapping of keys", result.Error);
    }

    [Fact]
    public void StepsThatAreNotAList_IsReported()
    {
        var result = WorkflowParser.Parse("odd.yml", """
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  run: dotnet build
            """);

        Assert.False(result.IsParsed);
        Assert.Contains("not a list of steps", result.Error);
    }

    [Fact]
    public void StrategyKeysAreKeptSoAStrategyWithoutAMatrixCanBeToldApart()
    {
        var result = WorkflowParser.Parse("ci.yml", """
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  fail-fast: false
                steps:
                  - run: dotnet build
            """);

        var job = Assert.Single(result.Document!.Jobs);
        Assert.Equal(["fail-fast"], job.StrategyKeys);
        Assert.False(job.HasMatrix);
    }

    [Fact]
    public void StrategyWithoutAMatrixIsNotReadAsOne()
    {
        var result = WorkflowParser.Parse("ci.yml", """
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  fail-fast: false
                steps:
                  - run: dotnet build
            """);

        Assert.False(Assert.Single(result.Document!.Jobs).HasMatrix);
    }
}
