using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The assistant's own profile editor.
/// </summary>
/// <remarks>
/// The defect this dialog replaces was not a bug in any one line: the Assistant Profile slot has always carried a
/// whole <see cref="SessionProfile"/> of its own, and Options presented it as a selection from the profile list —
/// so an operator set <c>bypassPermissions</c> on "default", saw the assistant still naming "default", and was
/// still asked to confirm every tool call. These tests hold the three things the new shape has to be true about:
/// the record is the assistant's own, copying one in is a one-time fill rather than a link, and nothing the dialog
/// declines to show is lost when it saves.
/// <para>
/// In <c>Cockpit.App.ViewTests</c> because <see cref="EditableProfileViewModel"/> builds Avalonia-owned collections;
/// <c>Cockpit.Core.Tests</c> may not touch a dispatcher at all (RS0030).
/// </para>
/// </remarks>
[Collection("avalonia")]
public class AssistantProfileDialogTests
{
    [Fact]
    public void TheMcpHint_NamesTheServersTheMountRuleActuallyAlwaysAdds()
    {
        // Read off AssistantIdentity, not typed here: the checklist tells the operator these arrive whatever they
        // tick, and AssistantSessionHost.McpSelection is what makes that true. Two copies of that claim is one that
        // can quietly stop matching — which is the same failure mode AssistantPaneId's own doc warns about.
        Assert.Contains(AssistantIdentity.McpServerName, AssistantProfileDialogViewModel.AlwaysMountedMcpServers, StringComparison.Ordinal);
        Assert.Contains(AssistantIdentity.ActMcpServerName, AssistantProfileDialogViewModel.AlwaysMountedMcpServers, StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_OpensOnTheAssistantsOwnRecord()
    {
        var slot = new AssistantProfileSlot(_Record("Claude (assistant)", permissionMode: "bypassPermissions"));
        var viewModel = Dispatcher.UIThread.Invoke(() => _Dialog(slot));

        Dispatcher.UIThread.Invoke(() => viewModel.LoadAsync().GetAwaiter().GetResult());

        Assert.Equal("Claude (assistant)", viewModel.Profile.Label);
    }

    /// <summary>
    /// A slot that has never been set up opens on a blank record rather than refusing: this dialog is where an
    /// assistant profile comes into being, so "there is none yet" is its empty state and not an error.
    /// </summary>
    [Fact]
    public void Loading_WithNoRecordYet_OpensOnABlankOne_AndSaysWhyItWasEmpty()
    {
        var viewModel = Dispatcher.UIThread.Invoke(() => _Dialog(AssistantProfileSlot.Unset("No assistant profile has been set up yet.")));

        Dispatcher.UIThread.Invoke(() => viewModel.LoadAsync().GetAwaiter().GetResult());

        Assert.Equal(AssistantProfileSlot.DisplayName, viewModel.Profile.Label);
        Assert.Contains("set up", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copying an existing profile in is a fill, not a link — and the whole reason the previous design needed a
    /// paragraph explaining that its copy was a copy.
    /// </summary>
    [Fact]
    public void CopyingAProfileIn_FillsTheEditor_AndWritesNothingUntilSaved()
    {
        var slotStore = _SlotStore(new AssistantProfileSlot(_Record("Claude (assistant)", permissionMode: "default")));
        var source = _Record("work", permissionMode: "bypassPermissions");
        var viewModel = Dispatcher.UIThread.Invoke(() => _Dialog(slotStore: slotStore, profiles: [source]));
        Dispatcher.UIThread.Invoke(() => viewModel.LoadAsync().GetAwaiter().GetResult());

        Dispatcher.UIThread.Invoke(() =>
        {
            viewModel.CopyFromProfile = source;
            viewModel.CopyFromCommand.Execute(null);
        });

        Assert.Equal("work", viewModel.Profile.Label);
        Assert.Equal(
            "bypassPermissions",
            viewModel.Profile.PluginOptionDefaults.Single(option => option.Key == "permission-mode").Value);

        // Nothing on disk yet: a fill the operator can still look at and cancel out of.
        slotStore.DidNotReceive().RepointAsync(Arg.Any<SessionProfile>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Saving_WritesTheEditedRecordOntoTheSlot()
    {
        var slotStore = _SlotStore(new AssistantProfileSlot(_Record("Claude (assistant)", permissionMode: "default")));
        var viewModel = Dispatcher.UIThread.Invoke(() => _Dialog(slotStore: slotStore));
        Dispatcher.UIThread.Invoke(() => viewModel.LoadAsync().GetAwaiter().GetResult());

        Dispatcher.UIThread.Invoke(() =>
        {
            viewModel.Profile.PluginOptionDefaults.Single(option => option.Key == "permission-mode").Value = "bypassPermissions";
            viewModel.SaveCommand.Execute(null);
        });

        var saved = (SessionProfile)slotStore.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAssistantProfileStore.RepointAsync))
            .GetArguments()[0]!;
        Assert.Equal("bypassPermissions", saved.Defaults!.OptionDefaults!["permission-mode"]);
    }

    /// <summary>
    /// The valkuil this dialog is most exposed to: it renders five blocks out of a record that has more fields
    /// than that, and a dialog that rebuilds a record from only what it shows wipes the rest without saying so
    /// (this repo has done it twice, in <c>ProjectDialogViewModel</c>).
    /// </summary>
    /// <remarks>
    /// The fields it checks are not listed. Only the ones the editor <em>rebuilds</em> are named; everything else on
    /// <see cref="SessionProfile"/> is discovered by reflection and has to come back unchanged. That direction is
    /// the point: a field added to the record later lands in the checked set on the day it is added, without anyone
    /// remembering this test — which is exactly the memory the two earlier losses depended on.
    /// </remarks>
    [Fact]
    public void Saving_CarriesEveryFieldTheDialogDoesNotShow_RatherThanDefaultingItAway()
    {
        var stored = _Record("Claude (assistant)", permissionMode: "default") with
        {
            Purpose = "the voice assistant",
            Delegation = new DelegationPolicy(AllowedAsTarget: true, MaxConcurrent: 3, Purpose: "bulk work"),
        };
        stored = stored with { DefaultWorkingDirectory = "/srv/work" };

        var slotStore = _SlotStore(new AssistantProfileSlot(stored));
        var viewModel = Dispatcher.UIThread.Invoke(() => _Dialog(slotStore: slotStore));
        Dispatcher.UIThread.Invoke(() => viewModel.LoadAsync().GetAwaiter().GetResult());

        Dispatcher.UIThread.Invoke(() => viewModel.SaveCommand.Execute(null));

        var saved = (SessionProfile)slotStore.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IAssistantProfileStore.RepointAsync))
            .GetArguments()[0]!;

        // What the editor genuinely produces afresh — the five blocks the dialog renders, plus what follows from
        // them. Every other property has to survive the round trip untouched.
        var rebuilt = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(SessionProfile.Label),
            nameof(SessionProfile.ProviderConfig),
            nameof(SessionProfile.Provider),
            nameof(SessionProfile.Claude),
            nameof(SessionProfile.Defaults),
            nameof(SessionProfile.EnvironmentVariables),
            nameof(SessionProfile.EnabledMcpServerNames),
            nameof(SessionProfile.SystemPrompt),
            // Recomputed from whether the provider has a TTY route, never from the record. Dead for the assistant,
            // which is always minted as an SDK session — see the view model's own remarks.
            nameof(SessionProfile.DefaultKind),
        };

        var carried = typeof(SessionProfile).GetProperties()
            .Where(property => property.CanRead && !rebuilt.Contains(property.Name))
            .ToList();

        // A reflection query that found nothing would make the loop below vacuously true.
        Assert.NotEmpty(carried);
        Assert.All(carried, property =>
            Assert.Equal(property.GetValue(stored), property.GetValue(saved)));

        // And one level down: the reading level lives inside Defaults, which the editor does rebuild, so the
        // sweep above cannot see it. It is the field AC-546 moved to AssistantSettings and this dialog therefore
        // does not show — the easiest one of all to lose.
        Assert.Equal(stored.Defaults!.DefaultReadingLevel, saved.Defaults!.DefaultReadingLevel);
    }

    /// <summary>
    /// The restart lives here, next to the permission mode, and it saves first — the two halves of one sentence.
    /// Split across two windows, as it briefly was, the operator had to make the connection themselves.
    /// </summary>
    [Fact]
    public void SaveAndRestart_PersistsFirst_ThenRestartsTheAssistant()
    {
        var slotStore = _SlotStore(new AssistantProfileSlot(_Record("Claude (assistant)", permissionMode: "default")));
        var assistant = Substitute.For<IAssistantSessionHost>();
        var viewModel = Dispatcher.UIThread.Invoke(() => _Dialog(slotStore: slotStore, assistant: assistant));
        Dispatcher.UIThread.Invoke(() => viewModel.LoadAsync().GetAwaiter().GetResult());

        Dispatcher.UIThread.Invoke(() =>
        {
            viewModel.Profile.PluginOptionDefaults.Single(option => option.Key == "permission-mode").Value = "bypassPermissions";
            viewModel.SaveAndRestartCommand.Execute(null);
        });

        // Order matters and is the whole point: a restart that ran before the write would bring the assistant back
        // up on the setting the operator just replaced.
        Received.InOrder(() =>
        {
            slotStore.RepointAsync(Arg.Is<SessionProfile>(record =>
                record.Defaults!.OptionDefaults!["permission-mode"] == "bypassPermissions"), Arg.Any<bool>(), Arg.Any<CancellationToken>());
            assistant.RestartAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public void WithNoLivingAssistant_TheRestartIsNotOffered()
    {
        // Rather than a button that would silently do half of what it says. The dialog stays fully usable — saving
        // still works, and the settings apply at whatever start comes next.
        var viewModel = Dispatcher.UIThread.Invoke(() => _Dialog(assistant: null));

        Assert.False(viewModel.CanRestartAssistant);
    }

    /// <summary>A record shaped like a plugin profile: its start defaults live in OptionDefaults, which is where the assistant's launch reads them (<c>AssistantSessionHost._LaunchOptions</c>).</summary>
    private static SessionProfile _Record(string label, string permissionMode) =>
        new(label, ClaudePluginProfile.Create("/home/raymond/.claude", null))
        {
            Defaults = new ProfileDefaults(string.Empty, string.Empty, string.Empty)
            {
                OptionDefaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["permission-mode"] = permissionMode,
                },
                DefaultReadingLevel = Cockpit.Core.Sessions.ReadingLevel.Simple,
            },
        };

    private static IAssistantProfileStore _SlotStore(AssistantProfileSlot slot)
    {
        var store = Substitute.For<IAssistantProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(slot);
        store.RepointAsync(Arg.Any<SessionProfile>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => new AssistantProfileSlot(call.Arg<SessionProfile>(), null, call.Arg<bool>()));
        return store;
    }

    private static AssistantProfileDialogViewModel _Dialog(
        AssistantProfileSlot? slot = null,
        IAssistantProfileStore? slotStore = null,
        IReadOnlyList<SessionProfile>? profiles = null,
        IAssistantSessionHost? assistant = null)
    {
        var sessionProfiles = Substitute.For<ISessionProfileStore>();
        sessionProfiles.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles ?? []);

        return new AssistantProfileDialogViewModel(
            slotStore ?? _SlotStore(slot ?? new AssistantProfileSlot(null, "No assistant profile has been set up yet.")),
            sessionProfiles,
            assistant,
            pluginProviderRegistry: _Registry());
    }

    /// <summary>
    /// A registry standing in for the bundled Claude provider plugin: the same provider id a Claude profile carries
    /// and the same three start options it declares. Needed rather than optional — the session-defaults block is
    /// rendered from a provider's declaration, so without one there is nothing for a test about the permission mode
    /// to act on, and a plugin profile would not even validate.
    /// </summary>
    private static IPluginProviderRegistry _Registry()
    {
        var registration = new SessionProviderRegistration(
            ClaudePluginProfile.ProviderId,
            "Claude",
            _ => throw new NotSupportedException("No session is started in these tests."),
            new PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true),
            _ => new _ConfigView())
        {
            Options =
            [
                new PluginSessionLaunchOption("permission-mode", "Permission mode", ["default", "acceptEdits", "plan", "bypassPermissions"], "default"),
                new PluginSessionLaunchOption("model", "Model", ["opus", "sonnet", "haiku"], "sonnet"),
                new PluginSessionLaunchOption("effort", "Effort", ["low", "medium", "high"], "medium"),
            ],
        };

        var registry = Substitute.For<IPluginProviderRegistry>();
        registry.Resolve(ClaudePluginProfile.ProviderId).Returns(registration);
        registry.Registrations.Returns([registration]);
        return registry;
    }

    /// <summary>The plugin's config panel, reduced to what the editor asks of it: a control to embed and config JSON that validates.</summary>
    private sealed class _ConfigView : IPluginProviderConfigView
    {
        public Avalonia.Controls.Control View { get; } = new Avalonia.Controls.TextBlock();

        public bool TryGetConfigJson(out string configJson)
        {
            configJson = "{}";
            return true;
        }
    }
}
