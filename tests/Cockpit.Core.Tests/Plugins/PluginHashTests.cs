using System.Text;
using Cockpit.Core.Plugins;
using FluentAssertions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The SHA-256 pin used to detect a changed/tampered plugin assembly (#14).</summary>
public class PluginHashTests
{
    [Fact]
    public void Compute_IsDeterministic_AndLowercaseHex()
    {
        var bytes = Encoding.UTF8.GetBytes("plugin-assembly-contents");

        var first = PluginHash.Compute(bytes);
        var second = PluginHash.Compute(bytes);

        first.Should().Be(second);
        first.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Compute_DifferentBytes_DifferentHash()
    {
        PluginHash.Compute(Encoding.UTF8.GetBytes("v1"))
            .Should().NotBe(PluginHash.Compute(Encoding.UTF8.GetBytes("v2")));
    }

    [Fact]
    public void ComputeClosure_IsIndependentOfFileOrder_AndPathSeparator()
    {
        var a = PluginHash.ComputeClosure(
        [
            new PluginClosureFile("Plugin.dll", "aaaa"),
            new PluginClosureFile("runtimes/linux/native/lib.so", "bbbb"),
        ]);
        var reordered = PluginHash.ComputeClosure(
        [
            new PluginClosureFile(@"runtimes\linux\native\lib.so", "bbbb"),
            new PluginClosureFile("Plugin.dll", "aaaa"),
        ]);

        a.Should().Be(reordered).And.MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeClosure_AChangedDependencyHash_ChangesTheClosure()
    {
        var original = PluginHash.ComputeClosure(
            [new PluginClosureFile("Plugin.dll", "entry"), new PluginClosureFile("Dep.dll", "dep-v1")]);
        var depChanged = PluginHash.ComputeClosure(
            [new PluginClosureFile("Plugin.dll", "entry"), new PluginClosureFile("Dep.dll", "dep-v2")]);

        depChanged.Should().NotBe(original);
    }

    [Fact]
    public void ComputeClosure_BindsEachHashToItsPath()
    {
        // The same two hashes on swapped paths must not collide — otherwise moving bytes between files would be a
        // silent no-op the pin never sees.
        var one = PluginHash.ComputeClosure(
            [new PluginClosureFile("a.dll", "x"), new PluginClosureFile("b.dll", "y")]);
        var swapped = PluginHash.ComputeClosure(
            [new PluginClosureFile("a.dll", "y"), new PluginClosureFile("b.dll", "x")]);

        swapped.Should().NotBe(one);
    }

    [Fact]
    public void ComputeClosure_CannotBeForgedByADelimiterInAPath()
    {
        // A Unix path may contain a newline. A manifest that joined entries with one would render these two
        // different closures identically ("1  a\n2  b"), letting a crafted filename forge the pin; length-prefixed
        // framing must keep them distinct.
        var honest = PluginHash.ComputeClosure(
            [new PluginClosureFile("a", "1"), new PluginClosureFile("b", "2")]);
        var forged = PluginHash.ComputeClosure(
            [new PluginClosureFile("a\n2  b", "1")]);

        forged.Should().NotBe(honest);
    }
}
