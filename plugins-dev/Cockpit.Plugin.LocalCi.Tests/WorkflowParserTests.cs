using Cockpit.Plugin.LocalCi.Workflows;

namespace Cockpit.Plugin.LocalCi.Tests;

public class WorkflowParserTests
{

    [Fact]
    public void MoreThanOneYamlDocument_IsReportedInsteadOfSilentlyReadingTheFirst()
    {
        var result = WorkflowParser.Parse("two.yml", """
            jobs:
              build:
                runs-on: ubuntu-latest
            ---
            jobs:
              other:
                runs-on: self-hosted
            """);

        Assert.False(result.IsParsed);
        Assert.Contains("more than one YAML document", result.Error);
    }

}
