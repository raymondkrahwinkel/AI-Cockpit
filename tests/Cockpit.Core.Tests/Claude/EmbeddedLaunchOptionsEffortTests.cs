using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Core.Tests.Claude;

// AC-1342: a step's reasoning-effort choice rides the same host-assembled launch options a permission mode or a
// hidden system prompt does (CockpitViewModel._EmbeddedLaunchOptions) — folded in under the well-known "effort"
// key when the request names one, left out otherwise; a provider that does not know the key simply never reads it.
public class EmbeddedLaunchOptionsEffortTests
{
    private static SessionProfile _Profile() => new("work", new ClaudeConfig("/config/dir"));

    [Theory]
    // A step whose plan names an effort level: the option key is present on the assembled launch options.
    [InlineData("high", true)]
    // A step whose plan names none (the common case, and every non-step embedded request): the request is still
    // accepted — no exception, no refusal — and the key is simply absent, exactly as it would be for a provider
    // that declares no effort option at all.
    [InlineData(null, false)]
    public void EmbeddedLaunchOptions_EffortRequest_SetsOrOmitsTheOptionKey(string? effort, bool expectedPresent)
    {
        var options = CockpitViewModel._EmbeddedLaunchOptions(_Profile(), new EmbeddedSessionRequest { ProfileId = "work", Effort = effort });

        Assert.Equal(expectedPresent, options?.ContainsKey(WellKnownPluginSessionOptions.Effort) == true);
    }
}
