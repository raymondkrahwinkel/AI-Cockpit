using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Cockpit.Core.Startup;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Startup;

// AC-1827, category 2: the splash's 4 s and 12 s scaled down; no plugin step can hold the start.
public class StartupSequenceTests
{
    [Fact]
    public async Task APluginStep_IsCalledSlowThenGivenUpOn_AFailureIsAStep_AndNoStepHoldsTheStart()
    {
        var sequence = new StartupSequence(slowAfter: TimeSpan.FromMilliseconds(100), giveUpAfter: TimeSpan.FromMilliseconds(400));
        var host = new PluginBackendHost(
            "example",
            "Example",
            new ServiceCollection().AddSingleton(sequence).BuildServiceProvider(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<ICockpitSessionObserver>(),
            Substitute.For<ICockpitActions>(),
            new PluginDiagnostics());
        host.AddStartupStep("Connecting to Example…", async token => await Task.Delay(Timeout.Infinite, token));
        host.AddStartupStep("Blocking…", _ =>
        {
            Thread.Sleep(1500);

            return Task.CompletedTask;
        });
        host.AddStartupStep("Broken…", _ => throw new InvalidOperationException("no answer"));
        var essential = sequence.AddCore("Loading plugins…", async _ => await Task.Delay(600), essential: true);
        var (hung, blocking, broken) = (sequence.Steps[0], sequence.Steps[1], sequence.Steps[2]);
        var seen = new List<StartupStepState>();
        sequence.Changed += () =>
        {
            lock (seen)
            {
                seen.Add(hung.State);
            }
        };

        await sequence.RunPendingAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            [StartupStepState.Skipped, StartupStepState.Skipped, StartupStepState.Failed, StartupStepState.Done],
            [hung.State, blocking.State, broken.State, essential.State]);
        Assert.Contains(StartupStepState.Slow, seen);
        Assert.Equal("“Connecting to Example” is taking too long — carrying on without it.", hung.Message);
        Assert.Equal("Couldn’t complete “Broken” — carrying on without it.", broken.Message);
        Assert.All([hung, blocking, broken], step => Assert.True(step.IsPlugin));
    }
}
