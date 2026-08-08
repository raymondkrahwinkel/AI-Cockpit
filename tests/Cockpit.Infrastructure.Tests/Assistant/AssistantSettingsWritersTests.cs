using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Tests.Assistant;

/// <summary>
/// The hardest thing #AC-575 claims: <b>the assistant cannot change its own consent bypass.</b> The switches
/// live in <see cref="AssistantSettings"/>, and no MCP tool anywhere in the cockpit may reach the one door that
/// writes them.
/// </summary>
/// <remarks>
/// <b>What #AC-637 changed about this claim.</b> The bypass now has one switch above the per-source lists —
/// <see cref="AssistantSettings.ConsentBypassAll"/>, on by default, covering both risk classes — so on a stock
/// install the assistant has nothing left to widen. What the walk holds shut is then the other direction as much
/// as the first: an assistant that could switch the operator's bypass back on after they turned it off. That makes
/// the write door matter more, not less, and it makes the ceiling at the end of this list the default rather than
/// something the operator opted into.
/// <para>
/// <b>Why this is a call-graph walk and not a list.</b> The obvious version of this test names the assistant's four
/// or five tools and checks each. That test contains a list, and the list is the hole: the tool added next month is
/// not on it, and the test goes on passing while the guarantee is gone. So both ends are derived from what actually
/// ships — the roots are every <c>[McpServerTool]</c> method on every tools type the app <em>registers</em>
/// (<c>AddInfrastructure</c>, resolved the way <c>CockpitMcpEndpointHost</c> resolves it, the same trick
/// <see cref="AssistantActMountRuleTests"/> uses for the mount flags), and reachability is read out of the compiled
/// IL rather than asserted about the shape of a constructor.
/// </para>
/// <para>
/// It deliberately covers <em>every</em> cockpit MCP endpoint, not only the assistant's two. The assistant mounts
/// more servers than its own — cockpit-agents is always mounted, and its profile can name any of the rest — so
/// "no tool the assistant can call writes this" is only true if no tool at all does.
/// </para>
/// <para>
/// <b>Known ceilings, written down rather than implied.</b> The walk follows <c>call</c>, <c>callvirt</c>,
/// <c>newobj</c> and <c>ldftn</c>/<c>ldvirtftn</c> through methods declared in the cockpit's own assemblies, and at a
/// virtually dispatched call it also queues every implementation in those assemblies (see
/// <see cref="_WithImplementations"/>) rather than stopping on the bodyless declaration. It does not follow a call
/// made by reflection or by a delegate handed in from outside those assemblies, and it does not see a plugin, which
/// is a separately-installed assembly this test cannot enumerate. A plugin that wrote the assistant's settings would
/// be doing so with the operator's install-time consent, which is a larger decision than this ticket's; a tool that
/// reached the door by <c>MethodInfo.Invoke</c> would not be caught here. Both are the price of a test that is
/// derived rather than listed, and both are smaller holes than the list would have been.
/// </para>
/// <para>
/// <b>The ceiling this walk cannot close at all: the file, not the code.</b> The switches live in
/// <c>cockpit.json</c>, and a call graph says nothing about who can write that file. With the operator's Terminal
/// bypass on for the dangerous class, <c>terminal.drive</c> is a tool that types arbitrary text into a shell — so it
/// can rewrite <c>cockpit.json</c> directly and widen the bypass, taking effect at the next start (the settings are
/// read on load, so it is not immediate, which is the only thing that makes this less than total). No call-graph
/// analysis can cover that route: the write never passes through a cockpit method, and the same is true of any
/// general-purpose command execution the operator switches on. It is the shape of the trade AC-575 made — the
/// dangerous switch was a second, separate, default-off decision precisely because turning it on for a source that
/// runs shell commands hands over more than the source's own name suggests. #AC-637 makes that trade the default:
/// allow-all covers the dangerous class too, so <c>terminal.drive</c> is reachable without a card on a stock
/// install and this route is open unless the operator switches allow-all off. Named here so it is a known ceiling
/// rather than a gap this file's green implies is absent.
/// </para>
/// </remarks>
public sealed class AssistantSettingsWritersTests
{
    /// <summary>
    /// The doors that write <see cref="AssistantSettings"/> to disk. The store's own <c>SaveAsync</c> — interface
    /// and implementations, since a tool could hold either — and the on-disk projection, which is the only other way
    /// to turn the record into bytes and would otherwise be a way around the abstraction rather than through it.
    /// </summary>
    private static bool _IsAWriteDoor(MethodBase method)
    {
        var declaring = method.DeclaringType;
        if (declaring is null)
        {
            return false;
        }

        if (declaring == typeof(AssistantSettingsEntry) && method.Name == nameof(AssistantSettingsEntry.FromDomain))
        {
            return true;
        }

        var isTheStore = declaring == typeof(IAssistantSettingsStore)
            || typeof(IAssistantSettingsStore).IsAssignableFrom(declaring);

        return isTheStore && method.Name == nameof(IAssistantSettingsStore.SaveAsync);
    }

    /// <summary>Every tools type the app registers an MCP endpoint for — read off the registration, never typed out here.</summary>
    private static IReadOnlyList<Type> _EveryRegisteredToolsType() =>
        [.. new ServiceCollection().AddInfrastructure().BuildServiceProvider()
            .GetServices<CockpitMcpEndpoint>()
            .Select(endpoint => endpoint.ToolsType)
            .Distinct()];

    private static IReadOnlyList<MethodInfo> _EveryToolMethod() =>
        [.. _EveryRegisteredToolsType()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)];

    [Fact]
    public void NoMcpToolTheCockpitHosts_CanReachAnythingThatWritesTheAssistantsSettings()
    {
        var tools = _EveryToolMethod();

        // A reflection query that found nothing would pass everything below it.
        Assert.NotEmpty(tools);

        var offenders = tools
            .Select(tool => (Tool: tool, Path: _PathToAWriteDoor(tool)))
            .Where(found => found.Path is not null)
            .Select(found => $"{found.Tool.DeclaringType!.Name}.{found.Tool.Name} -> {found.Path}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "An MCP tool can write AssistantSettings — which is where the consent bypass lives, so the assistant "
                + "could switch its own exemptions on:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheWalkerFindsAWriteDoorWhenThereIsOne()
    {
        // The positive control. Without it the test above passes just as happily on a walker that returns nothing —
        // which is the failure a derived test is most likely to have, and the least likely to notice.
        var path = _PathToAWriteDoor(typeof(WritesTheSettings).GetMethod(nameof(WritesTheSettings.Tool))!);

        Assert.NotNull(path);
        Assert.Contains(nameof(IAssistantSettingsStore.SaveAsync), path);
    }

    [Fact]
    public void TheWalkerActuallyTraversesTheCockpitsOwnCode()
    {
        // The second half of the control: the walker must go deeper than the root. A stand-in for the real thing —
        // the tool calls a helper which calls the door — so a walker that only ever inspected the root's own body
        // fails here instead of quietly under-reporting on the real tools.
        Assert.NotNull(_PathToAWriteDoor(typeof(WritesTheSettings).GetMethod(nameof(WritesTheSettings.IndirectTool))!));
    }

    /// <summary>
    /// The control that this file exists in its current form because of. Every MCP tool in this repo is
    /// <c>async</c>, and an async method's own body holds nothing but a call into the BCL's builder — the code is in
    /// a compiler-generated state machine. The first version of this walk did not follow that hop, so it read every
    /// tool as an empty method and reported no offenders at all; a probe tool that wrote the settings was added to
    /// the assistant's own acting server and the test stayed green. This keeps that hop pinned.
    /// </summary>
    [Fact]
    public void TheWalkerFollowsAnAsyncMethodIntoItsStateMachine()
    {
        Assert.NotNull(_PathToAWriteDoor(typeof(WritesTheSettings).GetMethod(nameof(WritesTheSettings.AsyncTool))!));
    }

    /// <summary>
    /// A call through an interface has to be followed into the implementation. <c>ResolveMethod</c> on the
    /// <c>callvirt</c> yields <c>IWriteDoorHop.Hop</c>, which is abstract and has no body, so the walk used to stop
    /// on the declaration and report nothing — one <c>private readonly IFoo</c> between a tool and the door was
    /// enough to hide it. Not a path that exists in the cockpit today, which is exactly why it needs a control
    /// rather than a comment.
    /// </summary>
    [Fact]
    public void TheWalkerFollowsAnInterfaceCallIntoItsImplementation()
    {
        Assert.NotNull(_PathToAWriteDoor(typeof(WritesTheSettings).GetMethod(nameof(WritesTheSettings.ThroughAnInterface))!));
    }

    /// <summary>
    /// The same hole one step over: a <c>virtual</c> that does nothing and an override that writes. Here the
    /// declared method does have a body, so the walk happily read the base's empty one and never looked at the
    /// override the call actually lands on.
    /// </summary>
    [Fact]
    public void TheWalkerFollowsAVirtualCallIntoItsOverride()
    {
        Assert.NotNull(_PathToAWriteDoor(typeof(WritesTheSettings).GetMethod(nameof(WritesTheSettings.ThroughAVirtual))!));
    }

    /// <summary>A stand-in tool that does the forbidden thing, so the walker's own correctness is asserted rather than assumed.</summary>
    public sealed class WritesTheSettings(IAssistantSettingsStore store)
    {
        // Writes what an assistant would actually aim at since #AC-637: the one switch that covers every source and
        // both risk classes, rather than a single source's everyday list.
        public Task Tool() => store.SaveAsync(new AssistantSettings { ConsentBypassAll = true });

        public Task IndirectTool() => _OneStepRemoved();

        /// <summary>Shaped like a real tool: async, awaits a read, then writes.</summary>
        public async Task<string> AsyncTool()
        {
            var settings = await store.LoadAsync().ConfigureAwait(false);
            await store.SaveAsync(settings with { ConsentBypassAll = true }).ConfigureAwait(false);
            return "done";
        }

        /// <summary>Reaches the door through an interface the caller's IL only names abstractly.</summary>
        public Task ThroughAnInterface(IWriteDoorHop hop) => hop.Hop(store);

        /// <summary>And through a virtual whose base body does nothing at all.</summary>
        public Task ThroughAVirtual(WriteDoorHopBase hop) => hop.Hop(store);

        private Task _OneStepRemoved() => Tool();
    }

    /// <summary>The interface half of the indirection controls above.</summary>
    public interface IWriteDoorHop
    {
        Task Hop(IAssistantSettingsStore store);
    }

    public sealed class WriteDoorHop : IWriteDoorHop
    {
        public Task Hop(IAssistantSettingsStore store) => store.SaveAsync(new AssistantSettings());
    }

    /// <summary>The virtual half: a base that writes nothing, and an override that does.</summary>
    public class WriteDoorHopBase
    {
        public virtual Task Hop(IAssistantSettingsStore store) => Task.CompletedTask;
    }

    public sealed class WriteDoorHopOverride : WriteDoorHopBase
    {
        public override Task Hop(IAssistantSettingsStore store) => store.SaveAsync(new AssistantSettings());
    }

    // ── The walk ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A breadth-first walk from <paramref name="root"/> through everything it can call, bounded to the cockpit's
    /// own assemblies. Returns a readable "a -> b -> c" trail to the first write door found, or null when there is
    /// none — the trail rather than a bool so a failure names the path instead of only the tool.
    /// </summary>
    private static string? _PathToAWriteDoor(MethodBase root)
    {
        var seen = new HashSet<MethodBase>();
        var queue = new Queue<(MethodBase Method, string Path)>();
        queue.Enqueue((root, $"{root.DeclaringType?.Name}.{root.Name}"));
        seen.Add(root);

        while (queue.Count > 0)
        {
            var (method, path) = queue.Dequeue();

            foreach (var callee in _Callees(method).SelectMany(_WithImplementations))
            {
                var trail = $"{path} -> {callee.DeclaringType?.Name}.{callee.Name}";
                if (_IsAWriteDoor(callee))
                {
                    return trail;
                }

                // Only the cockpit's own code is walked into. Following the BCL and the MCP SDK would take the walk
                // through every delegate in the framework without ever reaching a door that lives in this repo.
                if (!_IsOurs(callee.DeclaringType) || !seen.Add(callee))
                {
                    continue;
                }

                queue.Enqueue((callee, trail));
            }
        }

        return null;
    }

    private static bool _IsOurs(Type? type) =>
        type?.Assembly.GetName().Name?.StartsWith("Cockpit.", StringComparison.Ordinal) == true;

    /// <summary>
    /// <paramref name="callee"/> and, when the call is dispatched virtually, every implementation of it in the
    /// cockpit's own assemblies.
    /// </summary>
    /// <remarks>
    /// <c>Module.ResolveMethod</c> on a <c>callvirt</c> hands back the method as <em>declared</em>. For an interface
    /// or abstract member that method has no body at all, so the walk stopped dead at the declaration; for an
    /// ordinary <c>virtual</c> it walked the base implementation and never saw the override. Either way a door
    /// reached through one layer of indirection — a tool calling <c>ISomething.Do()</c> whose implementation writes
    /// the settings — went unreported, which is wider than the ceilings this file writes down. Each implementation
    /// is a real place the call can land, so each one is queued.
    /// </remarks>
    private static IEnumerable<MethodBase> _WithImplementations(MethodBase callee)
    {
        yield return callee;

        // Bounded to declarations in our own assemblies, matching the walk itself: expanding every virtual call into
        // the BCL would mean scanning every cockpit type for an override of ToString on each instruction.
        if (callee is not MethodInfo { IsVirtual: true } declared
            || declared.DeclaringType is not { } declaringType
            || !_IsOurs(declaringType))
        {
            yield break;
        }

        var baseDefinition = declared.GetBaseDefinition();
        foreach (var type in OurConcreteTypes.Value)
        {
            if (type == declaringType || !declaringType.IsAssignableFrom(type))
            {
                continue;
            }

            if (declaringType.IsInterface)
            {
                InterfaceMapping mapping;
                try
                {
                    mapping = type.GetInterfaceMap(declaringType);
                }
                catch (Exception)
                {
                    // A generic definition, or a type this runtime will not map. Nothing to add.
                    continue;
                }

                for (var i = 0; i < mapping.InterfaceMethods.Length; i++)
                {
                    if (mapping.InterfaceMethods[i] == declared)
                    {
                        yield return mapping.TargetMethods[i];
                    }
                }

                continue;
            }

            foreach (var candidate in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (candidate.IsVirtual && candidate.GetBaseDefinition() == baseDefinition)
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>Every concrete type the cockpit's own loaded assemblies define — the candidates an override can live on.</summary>
    private static readonly Lazy<IReadOnlyList<Type>> OurConcreteTypes = new(() =>
        [.. AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith("Cockpit.", StringComparison.Ordinal) == true)
            .SelectMany(_TypesOf)
            .Where(type => type is { IsInterface: false, IsAbstract: false, IsGenericTypeDefinition: false })]);

    private static IEnumerable<Type> _TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // A type whose own dependencies did not load cannot hold an override anyone can reach either.
            return exception.Types.OfType<Type>();
        }
    }

    /// <summary>
    /// Every method named by a call-like instruction in <paramref name="method"/>'s body. Read by walking the IL
    /// instruction by instruction rather than by scanning for opcode bytes: an operand can hold any byte, so a scan
    /// invents calls that are not there — and on a "nothing reaches this" assertion, an invented call is a red
    /// build with no bug behind it.
    /// </summary>
    private static IEnumerable<MethodBase> _Callees(MethodBase method)
    {
        // An async (or iterator) method's own body does almost nothing: the compiler moves the code into a
        // generated state machine and leaves behind a call to AsyncTaskMethodBuilder.Start, which lives in the BCL
        // and is not walked into. Every MCP tool in this repo is async, so without this the walk saw an empty body
        // for every root and the whole test passed vacuously — it did, and a probe tool that wrote the settings
        // went undetected until this line existed. The state machine's MoveNext is where the tool actually is.
        if (method.GetCustomAttribute<StateMachineAttribute>() is { StateMachineType: { } stateMachine }
            && stateMachine.GetMethod(nameof(IEnumerator.MoveNext), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) is { } moveNext)
        {
            yield return moveNext;
        }

        byte[]? il;
        try
        {
            il = method.GetMethodBody()?.GetILAsByteArray();
        }
        catch (Exception)
        {
            // Abstract, extern, or a body this runtime will not hand over. Nothing to walk.
            yield break;
        }

        if (il is null)
        {
            yield break;
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethodDefinition || method.IsGenericMethod ? method.GetGenericArguments() : null;

        var offset = 0;
        while (offset < il.Length)
        {
            var code = (short)il[offset++];
            if (code == 0xFE && offset < il.Length)
            {
                code = (short)(0xFE00 | il[offset++]);
            }

            if (!OpCodesByValue.TryGetValue(code, out var opCode))
            {
                // An opcode this table does not know means the rest of this body can no longer be located reliably.
                // Stopping is the honest answer; guessing would either invent calls or silently skip real ones.
                yield break;
            }

            if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineTok && offset + 4 <= il.Length)
            {
                MethodBase? callee = null;
                try
                {
                    callee = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset), typeArguments, methodArguments);
                }
                catch (Exception)
                {
                    // InlineTok also carries fields and types, and a token in a generic context can fail to resolve.
                    // Neither is a call, so neither is a finding.
                }

                if (callee is not null)
                {
                    yield return callee;
                }
            }

            offset += _OperandSize(opCode, il, offset);
        }
    }

    private static int _OperandSize(OpCode opCode, byte[] il, int offset) => opCode.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        // The jump table's length is in its first four bytes, and each target is four more.
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, offset)),
        _ => 4,
    };

    /// <summary>The opcode table, built from <see cref="OpCodes"/> itself so it cannot drift from the runtime's.</summary>
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value, opCode => opCode);
}
