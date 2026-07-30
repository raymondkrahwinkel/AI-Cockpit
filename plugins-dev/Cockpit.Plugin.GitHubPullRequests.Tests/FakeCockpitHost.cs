using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>
/// A minimal <see cref="ICockpitHost"/> for rendering <see cref="GitHubPullRequestsSideSectionControl"/>/
/// <see cref="GitHubPullRequestsWidget"/> — neither touches <see cref="Services"/>, <see cref="Actions"/> or
/// <see cref="Storage"/> during construction or the stale-marker render path under test, so those throw rather
/// than pretend to work; every other member falls back to <see cref="ICockpitHost"/>'s own defaults (a no-op
/// <see cref="ICockpitHost.Sessions"/>, settings-saved, etc.).
/// </summary>
internal sealed class FakeCockpitHost : ICockpitHost
{
    public IServiceProvider Services => throw new NotSupportedException();

    public ICockpitActions Actions => throw new NotSupportedException();

    public IPluginStorage Storage => throw new NotSupportedException();

    public void AddSettings(Func<Control> createView) => throw new NotSupportedException();

    public void AddSideMenuButton(string title, Action onInvoke) => throw new NotSupportedException();

    public void AddSideMenuSection(string title, Func<Control> createView) => throw new NotSupportedException();

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        Task.CompletedTask;
}
