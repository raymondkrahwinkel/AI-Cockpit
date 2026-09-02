using System.Runtime.CompilerServices;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// AC-1127: resolving the SDK overload is a mount-time failure with SDK-shape diagnostics, never a cached type-initializer failure.
/// </summary>
public sealed class CockpitMcpEndpointHostReflectionTests
{
    [Fact]
    public void NoMatchingWithToolsOverload_ReportsTheAvailableOverloads()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => CockpitMcpEndpointHost.ResolveWithToolsGeneric(typeof(_NoMatchingWithTools)));

        Assert.Contains("Could not resolve the generic WithTools", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithTools(Object, T)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleMatchingWithToolsOverloads_ReportsTheAvailableOverloads()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => CockpitMcpEndpointHost.ResolveWithToolsGeneric(typeof(_MultipleMatchingWithTools)));

        Assert.Contains("Could not resolve the generic WithTools", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithTools(Object, T, Object)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WithTools(Object, T, Int32)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalSdkShape_ResolvesTheGenericWithToolsOverload()
    {
        var method = CockpitMcpEndpointHost.ResolveWithToolsGeneric(typeof(Microsoft.Extensions.DependencyInjection.McpServerBuilderExtensions));

        Assert.Equal("WithTools", method.Name);
        Assert.True(method.IsGenericMethodDefinition);
    }

    [Fact]
    public void HostTypeInitializer_DoesNotThrow()
    {
        RuntimeHelpers.RunClassConstructor(typeof(CockpitMcpEndpointHost).TypeHandle);
    }

    private static class _NoMatchingWithTools
    {
        public static void WithTools<T>(object builder, T target) { }
    }

    private static class _MultipleMatchingWithTools
    {
        public static void WithTools<T>(object builder, T target, object options) { }
        public static void WithTools<T>(object builder, T target, int options) { }
    }

}
