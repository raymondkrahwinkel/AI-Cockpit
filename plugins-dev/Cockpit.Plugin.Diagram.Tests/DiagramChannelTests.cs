using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using NSubstitute;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Infrastructure.Diagrams;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-1400 (F2.12): the windows reach the access registries through the plugin's channel only — calls one way,
// events the other, in the backend's seq order — and an open_* tool with no UI part says nothing opened.
[Collection("avalonia")]
public class DiagramChannelTests
{
    private const string Flow = "flowchart LR\n  A-->B";

    // Wider than the Avalonia classes the ticket names: every type outside the backend part is window-side and is
    // scanned with its lambdas and state machines, so a helper a window calls cannot reach around the channel either.
    private static readonly HashSet<Type> BackendPart =
        [typeof(DiagramPlugin), typeof(DiagramChannel), typeof(DiagramMcpTools), typeof(WhiteboardMcpTools), typeof(WireframeMcpTools)];

    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void UiClasses_NeverReachTheContainer_AndTheScanNamesOneThatDoes()
    {
        var offenders = typeof(DiagramPlugin).Assembly.GetTypes()
            .Where(type => type.DeclaringType is null && !BackendPart.Contains(type))
            .Where(_ReachesTheContainer)
            .Select(type => type.FullName);

        Assert.Empty(offenders);
        Assert.True(_ReachesTheContainer(typeof(_ContainerReachingControl)));
    }

    [Fact]
    public void AnAgentEdit_ReachesTwoOpenWindowsOnTheSameSurface_InTheSameOrder()
    {
        var registry = Substitute.For<IDiagramAccessRegistry>();
        var channel = TestChannel.For(diagrams: registry);
        var first = _TextsSeenBy(new DiagramChannelClient(channel));
        var second = _TextsSeenBy(new DiagramChannelClient(channel));

        registry.TextChanged += Raise.Event<Action<string, string>>("d1", "v1");
        registry.TextChanged += Raise.Event<Action<string, string>>("d1", "v2");
        registry.TextChanged += Raise.Event<Action<string, string>>("d1", "v3");

        Assert.Equal(["v1", "v2", "v3"], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void AStateEventOlderThanTheLastOneSeenForThatSurface_IsIgnored()
    {
        var (channel, deliver) = _HandDeliveredChannel();
        var seen = _TextsSeenBy(new DiagramChannelClient(channel));

        deliver(new PluginChannelEvent("diagram.TextChanged", 5, _Payload("d1", "newer")));
        deliver(new PluginChannelEvent("diagram.TextChanged", 4, _Payload("d1", "older")));
        deliver(new PluginChannelEvent("diagram.TextChanged", 3, _Payload("d2", "other surface")));

        Assert.Equal(["newer", "other surface"], seen);
    }

    [Fact]
    public void AnObjectPlacedOutOfOrder_IsStillDelivered_SinceSkippingItWouldLoseTheObject()
    {
        var (channel, deliver) = _HandDeliveredChannel();
        var client = new WhiteboardChannelClient(channel);
        var placed = new List<string>();
        client.ObjectPlaced += (_, objectId, _) => placed.Add(objectId);
        var placement = new WhiteboardPlacement("sticky", "Idee", 1, 2, 3, 4);

        deliver(new PluginChannelEvent("whiteboard.ObjectPlaced", 5, _Payload("b1", "o-5", placement)));
        deliver(new PluginChannelEvent("whiteboard.ObjectPlaced", 4, _Payload("b1", "o-4", placement)));

        Assert.Equal(["o-5", "o-4"], placed);
    }

    [Fact]
    public void AHandEdit_ReachesTheRegistrysApplyHandEdit_AndItsRefusalComesBack()
    {
        var registry = Substitute.For<IDiagramAccessRegistry>();
        var edit = new DiagramHandEdit(DiagramHandEditKind.RenameNode, "A", Label: "Start") { Shape = DiagramNodeShape.Rounded };
        registry.ApplyHandEdit("d1", edit).Returns("That node is being edited by the agent.");

        var refusal = new DiagramChannelClient(TestChannel.For(diagrams: registry)).ApplyHandEdit("d1", edit);

        Assert.Equal("That node is being edited by the agent.", refusal);
        registry.Received(1).ApplyHandEdit("d1", edit);
    }

    [Fact]
    public void CouplingASurfaceAnotherAgentHolds_IsRefusedInTheWindow_AndTheHolderKeepsIt()
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("d1", "Flow", Flow);
        registry.Couple("pane-a", "d1");
        var client = new DiagramChannelClient(TestChannel.For(diagrams: registry));

        var refusal = Assert.Throws<InvalidOperationException>(() => client.Couple("pane-b", "d1"));

        Assert.False(string.IsNullOrWhiteSpace(refusal.Message));
        Assert.NotNull(registry.CouplingOf("pane-a", "d1"));
        Assert.Null(registry.CouplingOf("pane-b", "d1"));
    }

    [Fact]
    public void AWireframeWindow_GetsTheComponentIdOnALine_AndHoldsItInTheRegistry()
    {
        var registry = Substitute.For<IWireframeAccessRegistry>();
        registry.EnsureComponentId("w1", 4).Returns("save");
        var client = new WireframeChannelClient(TestChannel.For(wireframes: registry));

        var id = client.EnsureComponentId("w1", 4);
        client.HoldComponent("w1", "save");

        Assert.Equal("save", id);
        registry.Received(1).HoldComponent("w1", "save");
    }

    [Fact]
    public async Task OpenDiagram_OnABackendWithNoUiPart_AnswersOkButNotOpened_WithoutAskingTheOperator()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        var registry = new DiagramAccessRegistry();
        var settings = new DiagramSettings(new FakePluginStorage());
        var tools = new DiagramMcpTools(host, registry, settings, TestChannel.Wire(host, diagrams: registry).Backend);

        var json = JsonNode.Parse(await tools.OpenDiagram("pane-a", "Onboarding flow", Flow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(true, json?["ok"]?.GetValue<bool>());
        Assert.Equal(false, json?["opened"]?.GetValue<bool>());
        await host.DidNotReceive().RequestConsentAsync(Arg.Any<ConsentRequest>());
        await host.DidNotReceive().ShowDialogAsync(Arg.Any<string>(), Arg.Any<Func<Control>>(), Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>());
    }

    [Fact]
    public void TheBoundSessionClosing_ReachesTheWindowOverTheChannel_AndAnotherSessionClosingDoesNot()
    {
        var sessions = new _Sessions();
        var host = Substitute.For<ICockpitHost>();
        host.Sessions.Returns(sessions);
        var session = Substitute.For<IPluginSessionBinding>();
        session.PaneId.Returns("pane-1");
        session.IsLive.Returns(true);
        session.SessionName.Returns("Werksessie");
        host.BindToSession("pane-1").Returns(session);
        var changes = 0;
        var binding = new SurfaceSessionBinding(host, TestChannel.Wire(host).Ui, "pane-1", () => changes++);

        sessions.Close("pane-2");
        Dispatcher.UIThread.RunJobs();
        var afterOther = binding.EndedSessionName;
        sessions.Close("pane-1");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(afterOther);
        Assert.Equal("Werksessie", binding.EndedSessionName);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AClosedWindowsClient_NoLongerReceivesEvents()
    {
        var registry = Substitute.For<IDiagramAccessRegistry>();
        var client = new DiagramChannelClient(TestChannel.For(diagrams: registry));
        var seen = _TextsSeenBy(client);

        registry.TextChanged += Raise.Event<Action<string, string>>("d1", "while open");
        client.Dispose();
        registry.TextChanged += Raise.Event<Action<string, string>>("d1", "after close");

        Assert.Equal(["while open"], seen);
    }

    private static List<string> _TextsSeenBy(DiagramChannelClient client)
    {
        var seen = new List<string>();
        client.TextChanged += (_, text) => seen.Add(text);
        return seen;
    }

    private static JsonElement _Payload(params object[] args) => JsonSerializer.SerializeToElement(args, DiagramChannel.Json);

    // A UI channel whose events the test hands in itself, seq and all — the order a racing publisher could produce.
    private static (IPluginUiChannel Channel, Action<PluginChannelEvent> Deliver) _HandDeliveredChannel()
    {
        var handlers = new Dictionary<string, Action<PluginChannelEvent>>();
        var channel = Substitute.For<IPluginUiChannel>();
        channel.Subscribe(Arg.Any<string>(), Arg.Any<Action<PluginChannelEvent>>()).Returns(call =>
        {
            handlers[call.ArgAt<string>(0)] = call.ArgAt<Action<PluginChannelEvent>>(1);
            return Substitute.For<IDisposable>();
        });
        return (channel, channelEvent => handlers[channelEvent.Name](channelEvent));
    }

    // Every call, callvirt and newobj in the type's own methods and in its nested types (lambdas, async state
    // machines), resolved against the module: one that lands on IServiceProvider or ICockpitHost.Services is a
    // window reaching around its channel.
    private static bool _ReachesTheContainer(Type type) =>
        _WithNested(type)
            .SelectMany(candidate => candidate.GetMethods(DeclaredMembers).Cast<MethodBase>().Concat(candidate.GetConstructors(DeclaredMembers)))
            .SelectMany(_Callees)
            .Any(callee => callee.DeclaringType == typeof(IServiceProvider)
                || (callee.DeclaringType == typeof(ICockpitHost) && callee.Name == $"get_{nameof(ICockpitHost.Services)}"));

    private static IEnumerable<Type> _WithNested(Type type) =>
        new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(_WithNested));

    private static IEnumerable<MethodBase> _Callees(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        return Enumerable.Range(0, Math.Max(0, il.Length - 4))
            .Where(index => il[index] is 0x28 or 0x6F or 0x73)
            .Select(index => _Resolve(method, BitConverter.ToInt32(il, index + 1)))
            .OfType<MethodBase>();
    }

    // Not every byte that looks like a call opcode is one; a token that resolves to nothing is skipped.
    private static MethodBase? _Resolve(MethodBase method, int token)
    {
        try
        {
            return method.Module.ResolveMethod(
                token,
                method.DeclaringType?.GetGenericArguments(),
                method.IsGenericMethod ? method.GetGenericArguments() : null);
        }
        catch (Exception exception) when (exception is ArgumentException or BadImageFormatException)
        {
            return null;
        }
    }

    // The positive control for the scan: a window that still takes a registry from the container, from a lambda.
    private sealed class _ContainerReachingControl(ICockpitHost host) : UserControl
    {
        public Func<object?> Registry => () => host.Services.GetService(typeof(IDiagramAccessRegistry));
    }

    private sealed class _Sessions : ICockpitSessionObserver
    {
        public string? ActiveSessionWorkingDirectory => null;

        public event EventHandler? ActiveSessionChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SessionOutputText>? OutputProduced
        {
            add { }
            remove { }
        }

        public event EventHandler<string>? SessionClosed;

        public void Close(string paneId) => SessionClosed?.Invoke(this, paneId);
    }
}
