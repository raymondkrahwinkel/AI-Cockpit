using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

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

        screen.HasProviders.Should().BeTrue();
        var provider = screen.Providers.Should().ContainSingle().Which;
        provider.DisplayName.Should().Be("Claude");
        provider.Signals.Select(row => row.Label)
            .Should().BeEquivalentTo(["Context window", "Session (5 hours)", "Week"]);
        provider.Signals.Select(row => row.Declared).Should().BeEquivalentTo([50d, 90d, 90d]);
    }

    [Fact]
    public async Task AProviderThatMeasuresNothing_ProducesNoSection()
    {
        var screen = new UsageThresholdsViewModel(new InMemoryStore());

        await screen.LoadAsync([("shell", "Shell", Array.Empty<PluginUsageSignal>())]);

        screen.HasProviders.Should().BeFalse("a frame around controls that would do nothing is worse than no frame");
        screen.Providers.Should().BeEmpty();
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

        store.Settings.Resolve("claude", null, "weekly", declared: 90).Should().Be(70);

        week.Threshold = null;
        await screen.SaveAsync();

        store.Settings.Resolve("claude", null, "weekly", declared: 90).Should().Be(90);
    }

    [Fact]
    public async Task AnAlreadySavedNumber_ComesBackInTheField()
    {
        var store = new InMemoryStore();
        store.Settings.Set(store.Settings.ByProvider, "claude", "context", 35);
        var screen = new UsageThresholdsViewModel(store);

        await screen.LoadAsync([("claude", "Claude", Declared)]);

        var context = screen.Providers[0].Signals.Single(row => row.SignalKey == "context");
        context.Threshold.Should().Be(35);
        context.FollowsLabel.Should().Be("Follows the provider (50%)");
    }
}
