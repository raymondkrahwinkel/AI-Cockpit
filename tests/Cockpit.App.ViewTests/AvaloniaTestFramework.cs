using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("Cockpit.App.ViewTests.AvaloniaTestFramework", "Cockpit.App.ViewTests")]

namespace Cockpit.App.ViewTests;

public sealed class AvaloniaTestFramework : XunitTestFramework
{
    public AvaloniaTestFramework(IMessageSink messageSink) : base(messageSink)
    {
        _ = new HeadlessAvalonia();
    }
}
