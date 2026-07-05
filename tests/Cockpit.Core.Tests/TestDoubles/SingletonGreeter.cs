using Cockpit.Core.Abstractions;

namespace Cockpit.Core.Tests.TestDoubles;

internal sealed class SingletonGreeter : ISingletonService, IGreeter
{
    public string Greet() => "Hello from SingletonGreeter";
}
