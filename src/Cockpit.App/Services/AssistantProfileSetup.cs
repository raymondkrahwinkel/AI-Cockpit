using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;

namespace Cockpit.App.Services;

public sealed record AssistantProfileSetup(
    IReadOnlyList<SessionProviderOption> Providers,
    SessionProviderOption? PreferredProvider,
    Func<SessionProfile, CancellationToken, Task> SetUpLaterAsync);
