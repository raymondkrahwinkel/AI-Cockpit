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
    public void Matrix_IsRefused()
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

        Assert.False(verdict.CanRunLocally);
        Assert.Equal("it uses a matrix, so it is several runs rather than one", verdict.Reason);
    }

    [Fact]
    public void NonLinuxRunner_IsRefusedByName()
    {
        var verdict = _ClassifyOne("""
            jobs:
              mac:
                runs-on: macos-latest
                steps:
                  - run: xcodebuild
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal("it needs a macos-latest runner, and only Linux runners can run here", verdict.Reason);
    }

    [Fact]
    public void SelfHostedRunner_IsRefused()
    {
        var verdict = _ClassifyOne("""
            jobs:
              own:
                runs-on: self-hosted
                steps:
                  - run: make
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains("self-hosted", verdict.Reason);
    }

    [Fact]
    public void ExpressionInRunsOn_IsRefusedRatherThanGuessed()
    {
        var verdict = _ClassifyOne("""
            jobs:
              publish:
                runs-on: ${{ matrix.os }}
                steps:
                  - run: dotnet publish
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains("expression", verdict.Reason);
    }

    [Fact]
    public void ListRunsOn_IsRefusedAsNotUnderstood()
    {
        var verdict = _ClassifyOne("""
            jobs:
              own:
                runs-on: [self-hosted, linux]
                steps:
                  - run: make
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains("does not understand", verdict.Reason);
    }

    [Fact]
    public void MissingRunsOn_IsRefused()
    {
        var verdict = _ClassifyOne("""
            jobs:
              nowhere:
                steps:
                  - run: make
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal("it does not say what it runs on", verdict.Reason);
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

    [Fact]
    public void UploadGuardedAgainstAct_CanRunLocally()
    {
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - if: ${{ always() && !env.ACT }}
                    uses: actions/upload-artifact@v7
            """);

        Assert.True(verdict.CanRunLocally, verdict.Reason);
    }

    [Fact]
    public void UploadWithAlwaysOnly_IsStillRefused()
    {
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - if: always()
                    uses: actions/upload-artifact@v7
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains("exchanges artifacts with another job", verdict.Reason);
    }

    [Fact]
    public void UploadWithUnknownCondition_IsStillRefused()
    {
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - if: ${{ !env.SOMETHING_ELSE }}
                    uses: actions/upload-artifact@v7
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains("exchanges artifacts with another job", verdict.Reason);
    }

    [Fact]
    public void ActionOutsideTheAllowlist_IsRefusedAndNamed()
    {
        var verdict = _ClassifyOne("""
            jobs:
              release:
                runs-on: ubuntu-latest
                steps:
                  - uses: softprops/action-gh-release@v2
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal("it uses softprops/action-gh-release, which only means something on GitHub", verdict.Reason);
    }

    [Fact]
    public void UnknownJobKey_IsRefusedRatherThanIgnored()
    {
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                some-future-key: true
                steps:
                  - run: dotnet build
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal("it uses \"some-future-key\", which this check does not understand", verdict.Reason);
    }

    [Fact]
    public void UnknownStepKey_IsRefusedRatherThanIgnored()
    {
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: dotnet build
                    some-future-key: true
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains("a step uses \"some-future-key\"", verdict.Reason);
    }

    [Fact]
    public void JobContainer_IsRefusedWithItsOwnReason()
    {
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                container: node:20
                steps:
                  - run: node --version
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains("inside a container of its own", verdict.Reason);
    }

    [Fact]
    public void ContinueOnError_IsRefusedBecauseActIgnoresIt()
    {
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                continue-on-error: true
                steps:
                  - run: dotnet build
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains("act ignores it", verdict.Reason);
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
    public void UnknownStrategyKey_IsStillRefused()
    {
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                strategy:
                  some-future-key: true
                steps:
                  - run: dotnet build
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal("its strategy uses \"some-future-key\", which this check does not understand", verdict.Reason);
    }

    [Fact]
    public void LocalActionFromThisRepository_IsRefusedWithoutBlamingGitHub()
    {
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: ./.github/actions/setup
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal("it uses ./.github/actions/setup, an action from this repository, which this check does not run", verdict.Reason);
        Assert.DoesNotContain("GitHub", verdict.Reason);
    }

    [Fact]
    public void ContainerAction_IsRefusedWithoutBlamingGitHub()
    {
        // docker:// is the one shape act runs most naturally of all; saying it "only means something on GitHub"
        // would be the opposite of true.
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: docker://alpine:3.19
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal("it uses docker://alpine:3.19, a container action, which this check does not run", verdict.Reason);
        Assert.DoesNotContain("GitHub", verdict.Reason);
    }

    [Fact]
    public void EmptyUses_IsRefusedRatherThanTreatedAsARunStep()
    {
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: "   "
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Contains("empty uses:", verdict.Reason);
    }

    [Fact]
    public void ActionWhoseNameMerelyStartsWithAnAllowedOne_IsRefused()
    {
        var verdict = _ClassifyOne("""
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - uses: actions/checkout-but-not-really@v1
            """);

        Assert.False(verdict.CanRunLocally);
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
    public void JobThatCallsAnotherWorkflow_IsRefusedForWhatItIs()
    {
        var verdict = _ClassifyOne("""
            jobs:
              shared:
                uses: ./.github/workflows/build.yml
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal("it calls another workflow instead of running steps of its own", verdict.Reason);
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

    [Fact]
    public void UnknownWorkflowLevelKey_RefusesToo()
    {
        var verdict = _ClassifyOne("""
            some-future-key: true
            jobs:
              build:
                runs-on: ubuntu-latest
                steps:
                  - run: dotnet build
            """);

        Assert.False(verdict.CanRunLocally);
        Assert.Equal("the workflow uses \"some-future-key\", which this check does not understand", verdict.Reason);
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
                    os: [ubuntu-latest, macos-latest]
                steps:
                  - uses: softprops/action-gh-release@v2
            """);

        Assert.Equal("it uses a matrix, so it is several runs rather than one", verdict.Reason);
    }

    private static JobVerdict _ClassifyOne(string yaml) => Assert.Single(_Classify(yaml));

    private static IReadOnlyList<JobVerdict> _Classify(string yaml)
    {
        var parsed = WorkflowParser.Parse("test.yml", yaml);
        Assert.Null(parsed.Error);
        return LocalRunClassifier.Classify(parsed.Document!);
    }
}
