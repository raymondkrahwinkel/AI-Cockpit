using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>AC-731: a link step must stay visible once shown, even once the CLI moves on to a linkless prompt.</summary>
public class LoginFlowRowViewModelTests
{
    [Fact]
    public async Task ALinkStep_FollowedByALinklessStep_KeepsTheLinkVisible()
    {
        var link = new Uri("https://example.com/authorize");
        var flow = Substitute.For<ILoginFlow>();
        flow.Steps.Returns(_Steps(
            new LoginFlowStep("Open this link to sign in: " + link, link, AwaitsInput: false),
            new LoginFlowStep("Paste code here if prompted >", null, AwaitsInput: true)));
        flow.Completion.Returns(Task.FromResult(new LoginFlowResult(Success: false, ErrorMessage: null)));

        var vm = new LoginFlowRowViewModel(flow);
        await _WaitUntilAsync(() => vm.IsCompleted);

        Assert.True(vm.HasLink, "the CLI's own linkless prompt must not blank out a link already shown");
        Assert.Equal(link, vm.LinkToOpen);
        Assert.True(vm.AwaitsInput);

        await vm.DisposeAsync();
    }

    private static async IAsyncEnumerable<LoginFlowStep> _Steps(params LoginFlowStep[] steps)
    {
        foreach (var step in steps)
        {
            yield return step;
        }

        await Task.CompletedTask;
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}
