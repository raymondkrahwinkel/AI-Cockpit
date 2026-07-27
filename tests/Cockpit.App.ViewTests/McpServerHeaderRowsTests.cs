using Cockpit.App.ViewModels;
using Cockpit.Core.Mcp;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The custom-header rows on an HTTP MCP server (AC-354): <see cref="EditableMcpServerViewModel.ToConfig"/> keeps
/// only complete rows, existing headers come back on load, and stdio drops the whole list — headers are a request
/// property, and a stdio server has no request to put them on.
/// </summary>
public class McpServerHeaderRowsTests
{
    private static McpServerConfig _HttpServer(string url = "https://x/mcp") => new()
    {
        Name = "x",
        Transport = McpTransport.Http,
        Url = url,
    };

    [Fact]
    public void ACompleteRow_ReachesToConfig()
    {
        var editable = new EditableMcpServerViewModel(_HttpServer());

        editable.AddHeaderCommand.Execute(null);
        editable.Headers[0].Name = "X-Api-Key";
        editable.Headers[0].Value = "secret";

        var config = editable.ToConfig();

        var header = Assert.Single(config.Headers);
        Assert.Equal("X-Api-Key", header.Name);
        Assert.Equal("secret", header.Value);
    }

    [Fact]
    public void AHalfFilledRow_IsDroppedFromToConfig()
    {
        var editable = new EditableMcpServerViewModel(_HttpServer());

        editable.AddHeaderCommand.Execute(null);
        editable.Headers[0].Name = "X-Api-Key";
        // Value left empty — still being typed, not a header yet.

        var config = editable.ToConfig();

        Assert.Empty(config.Headers);
    }

    [Fact]
    public void ExistingHeaders_AreVisibleWhenTheServerIsOpened()
    {
        var server = _HttpServer() with { Headers = [new McpHeader("X-Api-Key", "secret")] };

        var editable = new EditableMcpServerViewModel(server);

        var row = Assert.Single(editable.Headers);
        Assert.Equal("X-Api-Key", row.Name);
        Assert.Equal("secret", row.Value);
    }

    [Fact]
    public void SwitchingToStdio_DropsHeadersFromToConfig()
    {
        var server = _HttpServer() with { Headers = [new McpHeader("X-Api-Key", "secret")] };
        var editable = new EditableMcpServerViewModel(server)
        {
            Transport = McpTransport.Stdio,
            Command = "npx",
        };

        var config = editable.ToConfig();

        Assert.Empty(config.Headers);
    }

    [Fact]
    public void RemoveHeader_TakesTheRowOutBeforeSave()
    {
        var editable = new EditableMcpServerViewModel(_HttpServer());
        editable.AddHeaderCommand.Execute(null);
        editable.Headers[0].Name = "X-Api-Key";
        editable.Headers[0].Value = "secret";

        editable.RemoveHeaderCommand.Execute(editable.Headers[0]);

        Assert.Empty(editable.Headers);
        Assert.Empty(editable.ToConfig().Headers);
    }
}
