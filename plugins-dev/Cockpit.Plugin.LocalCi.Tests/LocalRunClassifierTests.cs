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

    private static JobVerdict _ClassifyOne(string yaml)
    {
        var parsed = WorkflowParser.Parse("test.yml", yaml);
        Assert.Null(parsed.Error);
        return Assert.Single(LocalRunClassifier.Classify(parsed.Document!));
    }
}
