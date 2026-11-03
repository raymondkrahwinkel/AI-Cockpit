using Cockpit.Plugin.Workflows.Engine;

namespace Cockpit.Plugin.Workflows.Tests;

public class StepDataTests
{
    [Fact]
    public void WithAnEscaper_AComputedValueIsQuotedToo_NotOnlyAPlainField()
    {
        var result = StepData.Resolve("echo {= 'a; b' }", [], escapeValue: ShellQuoting.QuotePosix);

        Assert.Equal("echo 'a; b'", result.Text);
    }
}
