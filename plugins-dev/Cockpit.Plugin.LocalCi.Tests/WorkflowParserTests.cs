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
