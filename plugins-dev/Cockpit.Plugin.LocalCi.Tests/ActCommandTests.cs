using Cockpit.Plugin.LocalCi.Execution;

namespace Cockpit.Plugin.LocalCi.Tests;

public class ActCommandTests
{
    private static readonly LocalRunRequest Request = new(
        ProjectRoot: Path.Combine("C:", "work", "cockpit"),
        WorkflowPath: Path.Combine("C:", "work", "cockpit", ".github", "workflows", "ci.yml"),
        JobId: "plugins");

    private static readonly ActRunOptions Options = new("catthehacker/ubuntu:act-latest", CpuLimit: 8);

    [Fact]
    public void TheRunnerImageIsAlwaysNamed()
    {
        var arguments = ActCommand.Build(Request, "ubuntu-latest", Options, "r1");

        // act asks the operator to pick an image the first time it runs on a machine. Nothing is attached to this
        // process's input, so an unanswered prompt is a run that dies before it starts — naming the image is what
        // stops the question being asked at all.
        Assert.Equal("ubuntu-latest=catthehacker/ubuntu:act-latest", _After(arguments, "-P"));
    }

    [Fact]
    public void TheRunNeitherRefetchesWhatItHasNorLeavesContainersBehind()
    {
        var arguments = ActCommand.Build(Request, "ubuntu-latest", Options, "r1");

        // act pulls images and actions on every run unless told otherwise, which would undo the caches that make a
        // second run worth doing — the whole measured difference between a cold run and a warm one.
        Assert.Contains("--action-offline-mode", arguments);

        // And a run that ends badly cleans up after itself; without this only the stop path does, and an ordinary
        // failure leaves a container holding the cores.
        Assert.Contains("--rm", arguments);
    }

    [Fact]
    public void TheWorkflowIsNamedRelativeToTheCheckout()
    {
        var arguments = ActCommand.Build(Request, "ubuntu-latest", Options, "r1");

        Assert.Equal(".github/workflows/ci.yml", _After(arguments, "-W"));
    }

    [Fact]
    public void TheCheckoutIsTheWorkingDirectoryAndTheJobIsNamed()
    {
        var arguments = ActCommand.Build(Request, "ubuntu-latest", Options, "r1");

        Assert.Equal(Request.ProjectRoot, _After(arguments, "-C"));
        Assert.Equal("plugins", _After(arguments, "-j"));
    }

    [Fact]
    public void BothCachesAreMountedAndPointedAt()
    {
        var arguments = ActCommand.Build(Request, "ubuntu-latest", Options, "r1");
        var containerOptions = _After(arguments, "--container-options");
        var environment = arguments.Index().Where(entry => entry.Item == "--env").Select(entry => arguments[entry.Index + 1]).ToList();

        Assert.Contains($"-v {ActRunOptions.NugetVolume}:{ActRunOptions.NugetMount}", containerOptions);
        Assert.Contains($"-v {ActRunOptions.DotnetVolume}:{ActRunOptions.DotnetMount}", containerOptions);

        // Mounting without saying where is a cache nobody reads: the image decides HOME, and a job that resolves
        // its package folder from HOME would restore into the container and lose it when the container goes.
        Assert.Contains($"NUGET_PACKAGES={ActRunOptions.NugetMount}", environment);
        Assert.Contains($"DOTNET_INSTALL_DIR={ActRunOptions.DotnetMount}", environment);
    }

    [Fact]
    public void EveryContainerCarriesTheRunLabelAndTheCoreLimit()
    {
        var containerOptions = ActCommand.ContainerOptions(Options, "run-42");

        Assert.Contains($"--label {ActRunOptions.RunLabel}=run-42", containerOptions);
        Assert.Contains("--cpus=8", containerOptions);
    }

    [Theory]
    [InlineData(16, 8)]
    [InlineData(4, 2)]
    [InlineData(2, 2)]
    [InlineData(1, 2)]
    public void HalfTheCoresNeverFewerThanTwo(int processorCount, int expected) =>
        Assert.Equal(expected, ActRunOptions.For(processorCount).CpuLimit);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnsetImageFallsBackToTheDefault(string? configured) =>
        Assert.Equal(ActRunOptions.DefaultRunnerImage, ActRunOptions.For(8, configured).RunnerImage);

    [Fact]
    public void AConfiguredImageIsUsedAsGiven() =>
        Assert.Equal("catthehacker/ubuntu:full-latest", ActRunOptions.For(8, "  catthehacker/ubuntu:full-latest ").RunnerImage);

    [Fact]
    public void TheDescriptionIsTheCommandItself()
    {
        var arguments = ActCommand.Build(Request, "ubuntu-latest", Options, "r1");
        var described = ActCommand.Describe(arguments);

        // This text goes in front of the operator as the thing they are approving, so every argument has to be in
        // it — and the one with spaces has to survive as one argument rather than reading as several.
        Assert.StartsWith("act ", described);
        Assert.Contains($"\"{ActCommand.ContainerOptions(Options, "r1")}\"", described);
        Assert.All(arguments.Where(argument => !argument.Contains(' ')), argument => Assert.Contains(argument, described));
    }

    private static string _After(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"{flag} is not in the command.");
        return arguments[index + 1];
    }
}
