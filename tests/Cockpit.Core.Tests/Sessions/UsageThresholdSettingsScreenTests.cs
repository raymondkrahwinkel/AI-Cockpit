using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// The settings screen is built from what the providers declared (AC-233) — nothing about any signal is written
/// here, so a provider that adds one appears without a change to the host.
/// </summary>
public class UsageThresholdSettingsScreenTests
{
    private sealed class InMemoryStore : IUsageThresholdStore
    {
        public UsageThresholdSettings Settings { get; set; } = new();

        public Task<UsageThresholdSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings);

        public Task SaveAsync(UsageThresholdSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    // What a provider hands over. Shaped like Claude's, but written here rather than imported: the host must not
    // depend on any provider's assembly, and the Core tests cannot reference one either. That the real Claude
    // declarations say 50/90/90 is proven where they live, in the provider's own tests.
    private static readonly IReadOnlyList<PluginUsageSignal> Declared =
    [
        new("context", "ctx", PluginUsageSignalKind.Fill, 50) { Description = "Context window" },
        new("five-hour", "5h", PluginUsageSignalKind.Allowance, 90) { Description = "Session (5 hours)" },
        new("weekly", "wk", PluginUsageSignalKind.Allowance, 90) { Description = "Week" },
    ];

    [Fact]
    public async Task WhatTheProviderDeclared_FillsTheScreen()
    {
        var screen = new UsageThresholdsViewModel(new InMemoryStore());

        await screen.LoadAsync([("claude", "Claude", Declared)]);

        Assert.True(screen.HasProviders);
        var provider = Assert.Single(screen.Providers);
        Assert.Equal("Claude", provider.DisplayName);
        Assert.Equivalent(
            new object[] { "Context window", "Session (5 hours)", "Week" },
            provider.Signals.Select(row => row.Label));
        Assert.Equivalent(new object[] { 50d, 90d, 90d }, provider.Signals.Select(row => row.Declared));
    }

    [Fact]
    public async Task AProviderThatMeasuresNothing_ProducesNoSection()
    {
        var screen = new UsageThresholdsViewModel(new InMemoryStore());

        await screen.LoadAsync([("shell", "Shell", Array.Empty<PluginUsageSignal>())]);

        Assert.False(screen.HasProviders, "a frame around controls that would do nothing is worse than no frame");
        Assert.Empty(screen.Providers);
    }

    [Fact]
    public async Task AnEnteredNumber_IsSaved_AndAnEmptyFieldClearsTheOverride()
    {
        var store = new InMemoryStore();
        var screen = new UsageThresholdsViewModel(store);
        await screen.LoadAsync([("claude", "Claude", Declared)]);

        var week = screen.Providers[0].Signals.Single(row => row.SignalKey == "weekly");
        week.Threshold = 70;
        await screen.SaveAsync();

        Assert.Equal(70, store.Settings.Resolve("claude", null, "weekly", declared: 90, isAssistant: false));

        week.Threshold = null;
        await screen.SaveAsync();

        Assert.Equal(90, store.Settings.Resolve("claude", null, "weekly", declared: 90, isAssistant: false));
    }

    [Fact]
    public async Task AnAlreadySavedNumber_ComesBackInTheField()
    {
        var store = new InMemoryStore();
        store.Settings.Set(store.Settings.ByProvider, "claude", "context", 35);
        var screen = new UsageThresholdsViewModel(store);

        await screen.LoadAsync([("claude", "Claude", Declared)]);

        var context = screen.Providers[0].Signals.Single(row => row.SignalKey == "context");
        Assert.Equal(35, context.Threshold);
        Assert.Equal("Follows the provider (50%)", context.FollowsLabel);
    }

    [Fact]
    public async Task AssistantRows_AreBuiltFromTheSameDeclarations_ButSavedSeparately()
    {
        // AC-805: the Assistant section mirrors the provider section row-for-row, but reads and writes
        // `ByAssistant` rather than `ByProvider` — the two must not collide.
        var store = new InMemoryStore();
        var screen = new UsageThresholdsViewModel(store);
        await screen.LoadAsync([("claude", "Claude", Declared)]);

        Assert.True(screen.HasAssistantProviders);
        var assistantProvider = Assert.Single(screen.AssistantProviders);
        Assert.Equal("Claude", assistantProvider.DisplayName);

        var assistantContext = assistantProvider.Signals.Single(row => row.SignalKey == "context");
        assistantContext.Threshold = 25;
        await screen.SaveAsync();

        Assert.Equal(25, store.Settings.Resolve("claude", null, "context", declared: 50, isAssistant: true));
        Assert.Equal(50, store.Settings.Resolve("claude", null, "context", declared: 50, isAssistant: false));
    }

    [Fact]
    public async Task AProviderOverride_ChangesWhatTheEmptyAssistantFieldFollows()
    {
        // AC-805: leaving the Assistant field empty does not mean "follows the raw declaration" once a provider
        // override exists — it means "follows the provider level", whatever that resolves to right now.
        var store = new InMemoryStore();
        store.Settings.Set(store.Settings.ByProvider, "claude", "context", 75);
        var screen = new UsageThresholdsViewModel(store);

        await screen.LoadAsync([("claude", "Claude", Declared)]);

        var assistantContext = screen.AssistantProviders[0].Signals.Single(row => row.SignalKey == "context");
        Assert.Null(assistantContext.Threshold);
        Assert.Equal("Follows the provider (75%)", assistantContext.FollowsLabel);
    }
}
