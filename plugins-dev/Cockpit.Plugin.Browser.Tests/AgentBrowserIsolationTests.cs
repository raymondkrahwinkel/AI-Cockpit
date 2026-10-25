using System.Diagnostics;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Browser.Tests;

public class AgentBrowserIsolationTests
{
    private static readonly HttpClient Http = new();

    // AC-1712 acceptance 1 and 2 (L2b, at the plugin boundary): two sessions that mount Playwright MCP each get their
    // own browser, and a tab of one is not a target of the other. The plugin sees both launch routes through the one
    // session-resource seam, so it cannot tell them apart; that each route feeds the seam is the host's to show.
    [Fact]
    public async Task TwoSessionsWithPlaywrightMcp_GetEachTheirOwnBrowser_AndNothingOutlivesThem()
    {
        var executable = new AgentBrowserRuntime(Path.Combine(Path.GetTempPath(), "cockpit-browser-test-none"), Http).CachedExecutable()
            ?? AgentBrowserRuntime.SystemBrowser()
            ?? throw new InvalidOperationException("No Chromium to test against: install Chrome or Edge.");
        var browsers = new AgentBrowserHost(() => executable);
        var provider = new AgentBrowserSessionResources(browsers, () => true);
        var servers = new[] { new SessionResourceMcpServer("playwright", "npx", ["-y", "@playwright/mcp@latest"]) };

        var endpointA = await EndpointAsync(provider, "pane-a", servers);
        var endpointB = await EndpointAsync(provider, "pane-b", servers);
        var processes = new[] { browsers.ProcessIdFor("pane-a")!.Value, browsers.ProcessIdFor("pane-b")!.Value };

        try
        {
            Assert.NotEqual(endpointA, endpointB);
            using (var opened = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"{endpointA}/json/new?data:text/html,only-in-a")))
            {
                opened.EnsureSuccessStatusCode();
            }

            Assert.Contains("only-in-a", await Http.GetStringAsync($"{endpointA}/json/list"));
            Assert.DoesNotContain("only-in-a", await Http.GetStringAsync($"{endpointB}/json/list"));
        }
        finally
        {
            browsers.Dispose();
        }

        // The browsers stop with the host: no process of either session is left behind.
        foreach (var id in processes)
        {
            Assert.True(await GoneAsync(id), $"Chromium process {id} outlived the dispose");
        }
    }

    private static async Task<string> EndpointAsync(AgentBrowserSessionResources provider, string paneId, SessionResourceMcpServer[] servers)
    {
        var contribution = await provider.GetSessionResourcesAsync(new SessionResourceRequest(paneId, null) { McpServers = servers });
        return contribution.EnvironmentVariables[AgentBrowserSessionResources.EndpointVariable];
    }

    private static async Task<bool> GoneAsync(int processId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
