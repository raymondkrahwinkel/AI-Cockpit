using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The conversation-picker contribution point: a plugin that can browse a provider's history lends that to the
/// New-session dialog, so an operator resuming a conversation picks one instead of typing an id. The cockpit
/// itself stays ignorant of any provider's transcripts — it only knows that someone offers a picker.
/// </summary>
public class ConversationPickerTests
{
    [Fact]
    public void APluginPicker_BecomesAvailableToTheNewSessionDialog()
    {
        var registry = new ConversationPickerRegistry();
        var host = NewHost(registry);

        host.AddConversationPicker(new ConversationPickerRegistration("Search transcripts", () => Task.FromResult<string?>("abc-123")));

        Assert.Equal("Search transcripts", Assert.Single(registry.Pickers).Title);
    }

    [Fact]
    public async Task ThePicker_HandsBackTheChosenConversation()
    {
        var registry = new ConversationPickerRegistry();
        var host = NewHost(registry);
        host.AddConversationPicker(new ConversationPickerRegistration("Search transcripts", () => Task.FromResult<string?>("abc-123")));

        var picked = await registry.Pickers[0].PickAsync();

        Assert.Equal("abc-123", picked);
    }

    // A provider whose history is folder-scoped hands back where the conversation ran as well as its id, through
    // the optional richer picker, so the dialog can resume it in the right place.
    [Fact]
    public async Task ThePicker_CanAlsoHandBackWhereTheConversationRan()
    {
        var registry = new ConversationPickerRegistry();
        var host = NewHost(registry);
        host.AddConversationPicker(new ConversationPickerRegistration("Search transcripts", () => Task.FromResult<string?>("abc-123"))
        {
            PickWithLocationAsync = () => Task.FromResult<PickedConversation?>(new PickedConversation("abc-123", "/home/me/project")),
        });

        var pickWithLocation = registry.Pickers[0].PickWithLocationAsync;
        Assert.NotNull(pickWithLocation);

        var picked = pickWithLocation is null ? null : await pickWithLocation();

        Assert.Equal(new PickedConversation("abc-123", "/home/me/project"), picked);
    }

    // No plugin that browses a provider's history installed is the normal case: the dialog then simply shows no
    // search button, and the id can still be typed by hand.
    [Fact]
    public void WithNoPluginInstalled_ThereIsNoPicker()
    {
        Assert.Empty(new ConversationPickerRegistry().Pickers);
    }

    private static ICockpitHost NewHost(IConversationPickerRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(registry);

        return new CockpitHost(
            "test-plugin",
            "Test Plugin",
            services.BuildServiceProvider(),
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());
    }
}
