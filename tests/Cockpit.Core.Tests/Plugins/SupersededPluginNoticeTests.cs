using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// Which plugins make the notice speak. <see cref="SupersededPlugin.ShouldOffer"/> decides <em>whether</em> to
/// say something; this is about the set it is asked about, which is the part that was wrong. It used to ask the
/// registration store for its keys, and a registration is not a loaded plugin — only a loaded one claims the
/// widget type that is the whole reason to say anything.
/// </summary>
public class SupersededPluginNoticeTests
{
    [Fact]
    public async Task WhenTheOldPluginAndASuccessorBothLoaded_ItIsOfferedForRemoval()
    {
        var toasts = _NewNotice(out var notice, ("widgets", PluginLoadDecision.Load), ("clock", PluginLoadDecision.Load));

        await notice.CheckAsync();

        toasts.ReceivedWithAnyArgs(1).Show(default!, default);
    }

    /// <summary>
    /// A plugin the operator switched off is not loaded (<c>PluginLoadPolicy</c> → <c>Disabled</c>), so it claims
    /// nothing and nothing is competing for its types. Offering to remove it undoes a decision they made on
    /// purpose — one the installer goes out of its way to respect, and that this used to talk straight over.
    /// </summary>
    [Fact]
    public async Task AnOldPluginTheOperatorSwitchedOff_IsLeftAlone()
    {
        var toasts = _NewNotice(out var notice, ("widgets", PluginLoadDecision.Disabled), ("clock", PluginLoadDecision.Load));

        await notice.CheckAsync();

        toasts.DidNotReceiveWithAnyArgs().Show(default!, default);
    }

    /// <summary>The other half of the same question: a successor that did not load has taken nothing over, so the old plugin is still the only thing doing the job.</summary>
    [Fact]
    public async Task ASuccessorThatDidNotLoad_HasNotTakenOverFromAnything()
    {
        var toasts = _NewNotice(out var notice, ("widgets", PluginLoadDecision.Load), ("clock", PluginLoadDecision.Disabled));

        await notice.CheckAsync();

        toasts.DidNotReceiveWithAnyArgs().Show(default!, default);
    }

    /// <summary>
    /// The case that outlives "is it enabled": a plugin whose pinned hash no longer matches its assembly is
    /// enabled and still does not load — it is waiting to be consented to again. Raymond produces exactly this by
    /// hand, by copying a rebuilt dll into the plugins folder. Here the successor is the one in that state, so
    /// the old plugin is serving the widgets alone and offering to remove it would take them away.
    /// </summary>
    [Fact]
    public async Task ASuccessorWaitingOnConsent_HasNotTakenOverEither()
    {
        var toasts = _NewNotice(out var notice, ("widgets", PluginLoadDecision.Load), ("clock", PluginLoadDecision.NeedsConsent));

        await notice.CheckAsync();

        toasts.DidNotReceiveWithAnyArgs().Show(default!, default);
    }

    /// <remarks>
    /// The registration store is stocked to match the load decisions rather than left blank, so the registrations
    /// say what they would really say for each of these plugins: every one of them is registered, a
    /// <see cref="PluginLoadDecision.Disabled"/> one is switched off, and a
    /// <see cref="PluginLoadDecision.NeedsConsent"/> one is switched on with a pin that no longer matches its
    /// assembly. Without that, asking the registrations — which is exactly the bug — would answer "nothing is
    /// installed" here and these tests would pass over the top of it.
    /// </remarks>
    private static IToastService _NewNotice(out SupersededPluginNotice notice, params (string FolderId, PluginLoadDecision Decision)[] plugins)
    {
        var manager = new PluginManager(NullLogger<PluginManager>.Instance, new PluginDiagnostics());
        manager.LoadAndConfigure(
            [.. plugins.Select(plugin => new DiscoveredPlugin(
                FolderPath: $"/plugins/{plugin.FolderId}",
                FolderId: plugin.FolderId,
                Manifest: _NewManifest(plugin.FolderId),
                Sha256: "the-assembly-on-disk",
                Decision: plugin.Decision))],
            new ServiceCollection(),
            _ => Substitute.For<ICockpitPlugin>());

        var registrations = Substitute.For<IPluginRegistrationStore>();
        registrations.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(
            plugins.ToDictionary(
                plugin => plugin.FolderId,
                plugin => new PluginRegistration(
                    Enabled: plugin.Decision != PluginLoadDecision.Disabled,
                    PinnedSha256: plugin.Decision == PluginLoadDecision.NeedsConsent ? "a-pin-from-before-the-rebuild" : "the-assembly-on-disk"))
                as IReadOnlyDictionary<string, PluginRegistration>);

        var toasts = Substitute.For<IToastService>();
        notice = new SupersededPluginNotice(
            manager,
            registrations,
            Substitute.For<IPluginInstaller>(),
            toasts,
            NullLogger<SupersededPluginNotice>.Instance);

        return toasts;
    }

    private static PluginManifest _NewManifest(string id)
    {
        PluginManifest.TryParse($$"""
            {
              "id": "{{id}}",
              "name": "{{id}}",
              "version": "1.0.0",
              "entryAssembly": "Cockpit.Plugin.{{id}}.dll",
              "entryType": "Cockpit.Plugin.{{id}}.Entry",
              "abstractionsVersion": 1
            }
            """, out var manifest, out var error);

        return manifest ?? throw new InvalidOperationException($"the test manifest for '{id}' did not parse: {error}");
    }
}
