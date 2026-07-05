using Zyra.Voice.Core.Abstractions;

namespace Zyra.Voice.Core.Tests.TestDoubles;

internal sealed class SingletonGreeter : ISingletonService, IGreeter
{
    public string Greet() => "Hello from SingletonGreeter";
}
