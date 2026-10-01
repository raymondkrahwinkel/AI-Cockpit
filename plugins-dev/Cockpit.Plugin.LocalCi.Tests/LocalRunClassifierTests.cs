using Cockpit.Plugin.LocalCi.Workflows;

namespace Cockpit.Plugin.LocalCi.Tests;

public class LocalRunClassifierTests
{
    [Fact]
    public void PlainLinuxJobWithFreeActionsOnly_CanRunLocally()
    {
        var verdict = _ClassifyOne("""
            name: CI
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v7
                  - uses: actions/setup-dotnet@v6
                    with:
                      dotnet-version: '10.0.x'
                  - name: Build
                    run: dotnet build
                  - if: ${{ always() && !env.ACT }}
                    uses: actions/upload-artifact@v7
                    with:
                      name: test-results
                      path: TestResults
            """);

        Assert.True(verdict.CanRunLocally);
        Assert.Null(verdict.Reason);
    }

    [Fact]
    public void OneAxisMatrix_CanRunLocally()
    {
        var verdict = _ClassifyOne("""
            jobs:
              publish:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    rid: [linux-x64, win-x64]
                steps:
                  - run: dotnet publish
            """);

        Assert.True(verdict.CanRunLocally, verdict.Reason);
    }

    [Fact]
    public void DynamicOneAxisMatrix_CanRunLocally()
    {
        var verdict = _ClassifyOne("""
            jobs:
              plugins:
                runs-on: ubuntu-latest
                strategy:
                  matrix:
                    plugin: ${{ fromJSON(needs.changes.outputs.plugins) }}
                steps:
                  - run: dotnet test ${{ matrix.plugin }}
            """);

        Assert.True(verdict.CanRunLocally, verdict.Reason);
    }

    [Theory]
    [InlineData("{ include: [{ os: ubuntu-latest }] }")]
    [InlineData("{ exclude: [{ os: windows-latest }] }")]
    [InlineData("{ os: [ubuntu-latest], runtime: [net10.0] }")]
    [InlineData("{ plugin: [{ name: Clock }] }")]
    public void ComplexMatrices_AreRefused(string matrix)
    {
        var verdict = _ClassifyOne($"""
            jobs:
              spread:
                runs-on: ubuntu-latest
                strategy:
                  matrix: {matrix}
                steps:
                  - run: echo building
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains("matrix", verdict.Reason);
    }

    [Theory]
    [InlineData("runs-on: macos-latest", "it needs a macos-latest runner, and only Linux runners can run here")]
    [InlineData("runs-on: self-hosted", "it needs a self-hosted runner, and only Linux runners can run here")]
    [InlineData("runs-on: ${{ matrix.os }}", "its runs-on is an expression, and what that resolves to is only known on GitHub")]
    [InlineData("runs-on: [self-hosted, linux]", "its runs-on is written in a form this check does not understand")]
    [InlineData("", "it does not say what it runs on")]
    public void UnsupportedRunsOn_IsRefused(string runsOn, string expectedReason)
    {
        var verdict = _ClassifyOne($"""
            jobs:
              build:
                {runsOn}
                steps:
                  - run: dotnet build
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal(expectedReason, verdict.Reason);
    }

    [Theory]
    [InlineData("actions/upload-artifact@v7")]
    [InlineData("actions/download-artifact@v8")]
    public void ArtifactExchange_IsRefusedWithItsOwnWording(string uses)
    {
        var verdict = _ClassifyOne($"""
            jobs:
              release:
                runs-on: ubuntu-latest
                steps:
                  - uses: {uses}
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains("exchanges artifacts with another job", verdict.Reason);
    }

    [Theory]
    [InlineData("${{ always() && !env.ACT }}", true, null)]
    [InlineData("always()", false, "it exchanges artifacts with another job (it uses actions/upload-artifact)")]
    [InlineData("${{ !env.SOMETHING_ELSE }}", false, "it exchanges artifacts with another job (it uses actions/upload-artifact)")]
    public void ArtifactUploadConditions_AreClassified(string condition, bool canRunLocally, string? expectedReason)
    {
        var verdict = _ClassifyOne($"""
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - if: {condition}
                    uses: actions/upload-artifact@v7
            """);

        Assert.Equal(canRunLocally, verdict.CanRunLocally);
        Assert.Equal(expectedReason, verdict.Reason);
    }

    [Theory]
    [InlineData("softprops/action-gh-release@v2", "it uses softprops/action-gh-release, which only means something on GitHub")]
    [InlineData("./.github/actions/setup", "it uses ./.github/actions/setup, an action from this repository, which this check does not run")]
    [InlineData("docker://alpine:3.19", "it uses docker://alpine:3.19, a container action, which this check does not run")]
    [InlineData("   ", "a step has an empty uses:, and an empty action is not something to assume about")]
    [InlineData("actions/checkout-but-not-really@v1", "it uses actions/checkout-but-not-really, which only means something on GitHub")]
    public void UnsupportedActions_AreRefused(string uses, string expectedReason)
    {
        var verdict = _ClassifyOne($"""
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: {uses}
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal(expectedReason, verdict.Reason);
    }

    [Theory]
    [InlineData("jobs:\n  build:\n    runs-on: ubuntu-latest\n    some-future-key: true\n    steps:\n      - run: dotnet build", "it uses \"some-future-key\", which this check does not understand")]
    [InlineData("jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet build\n        some-future-key: true", "a step uses \"some-future-key\", which this check does not understand")]
    [InlineData("jobs:\n  build:\n    runs-on: ubuntu-latest\n    strategy:\n      some-future-key: true\n    steps:\n      - run: dotnet build", "its strategy uses \"some-future-key\", which this check does not understand")]
    [InlineData("some-future-key: true\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet build", "the workflow uses \"some-future-key\", which this check does not understand")]
    public void UnknownKeys_AreRefusedRatherThanIgnored(string yaml, string expectedReason)
    {
        var verdict = _ClassifyOne(yaml);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal(expectedReason, verdict.Reason);
    }

    [Theory]
    [InlineData("container: node:20", "it runs the whole job inside a container of its own, which this plugin does not set up")]
    [InlineData("continue-on-error: true", "it uses continue-on-error, which decides whether a failure counts — and act ignores it, so a local result would not mean the same thing")]
    [InlineData("uses: ./.github/workflows/build.yml", "it calls another workflow instead of running steps of its own")]
    public void UnsupportedJobFeatures_AreRefused(string jobFeature, string expectedReason)
    {
        var verdict = _ClassifyOne($"""
            jobs:
              build:
                runs-on: ubuntu-latest
                {jobFeature}
                steps:
                  - run: dotnet build
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal(expectedReason, verdict.Reason);
    }

    [Fact]
    public void StrategyWithoutAMatrix_DoesNotBlock()
    {
        // fail-fast and max-parallel only govern how GitHub schedules a set of runs. There is one run here, so
        // refusing the job for carrying a strategy at all would be a refusal with nothing behind it.
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  fail-fast: false
                  max-parallel: 2
                steps:
                  - run: dotnet build
            """);

        Assert.True(verdict.CanRunLocally, verdict.Reason);
    }


    [Fact]
    public void NeedsAlone_DoesNotBlock()
    {
        // Ordering between jobs is not the same as exchanging artifacts, and only the second one is a reason.
        var verdict = _ClassifyOne("""
            jobs:
              finalize:
                needs: publish
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout@v7
            """);

        Assert.True(verdict.CanRunLocally);
    }


    [Fact]
    public void JobWithNoSteps_IsRefusedRatherThanCalledRunnable()
    {
        // "Nothing to do" reported as a green tick is the shape of result this whole classification exists to avoid.
        var verdict = _ClassifyOne("""
            jobs:
              empty:
                runs-on: ubuntu-latest
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal("it has no steps", verdict.Reason);
    }

    [Fact]
    public void WorkflowLevelDefaults_RefuseEveryJobInTheFile()
    {
        // The setting sits above the job and changes what its run steps do. Reporting the job as runnable would be
        // reading only the half of the file the job is written in.
        var verdicts = _Classify("""
            name: CI
            defaults:
              run:
                shell: pwsh
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: dotnet build
            """);

        var verdict = Assert.Single(verdicts);
        Assert.False(verdict.CanRunLocally);
        Assert.Contains("defaults for every run step", verdict.Reason);
    }


    [Theory]
    [InlineData("ubuntu-latest-4-cores")]
    [InlineData("ubuntu-24.04-arm")]
    [InlineData("ubuntu-our-own-box")]
    public void RunnerLabelThatOnlyLooksLikeAStandardLinuxOne_IsRefused(string label)
    {
        // A larger runner, an arm image and a self-hosted box someone named after ubuntu all start with the same
        // seven characters and none of them is the runner this check means.
        var verdict = _ClassifyOne($"""
            jobs:
              build:
                runs-on: {label}
                steps:
                  - run: dotnet build
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains(label, verdict.Reason);
    }

    [Theory]
    [InlineData("ubuntu-latest")]
    [InlineData("ubuntu-24.04")]
    [InlineData("ubuntu-22.04")]
    public void StandardLinuxRunners_AreAccepted(string label)
    {
        var verdict = _ClassifyOne($"""
            jobs:
              build:
                runs-on: {label}
                steps:
                  - run: dotnet build
            """);

        Assert.True(verdict.CanRunLocally, verdict.Reason);
    }

    [Fact]
    public void MatrixWinsOverTheOtherProblems()
    {
        // Two problems, one reported — and always the same one, so a reason can be asserted at all.
        var verdict = _ClassifyOne("""
            jobs:
              publish:
                runs-on: ${{ matrix.os }}
                strategy:
                  matrix:
                    include:
                      - os: ubuntu-latest
                      - os: macos-latest
                steps:
                  - uses: softprops/action-gh-release@v2
            """);

        Assert.Equal("it uses a matrix more complex than one list of values", verdict.Reason);
    }

    private static JobVerdict _ClassifyOne(string yaml) => Assert.Single(_Classify(yaml));

    private static IReadOnlyList<JobVerdict> _Classify(string yaml)
    {
        var parsed = WorkflowParser.Parse("test.yml", yaml);
        Assert.Null(parsed.Error);
        return LocalRunClassifier.Classify(parsed.Document!);
    }
}
