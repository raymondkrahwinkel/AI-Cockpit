using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeSdkSessionDriver"/> (Fase 4, SDK route) driven against a <see cref="FakeClaudeSdkSubprocess"/> —
/// the turn-taking and, above all, the in-band permission round-trip that replaces the host's HTTP MCP permission
/// server: a <c>can_use_tool</c> control_request surfaces as <see cref="PluginPermissionRequested"/>, and the
/// operator's answer is written back as a <c>control_response</c> echoing the request's own <c>request_id</c>.
/// The live CLI end (that it emits <c>can_use_tool</c> for this spawn) needs a manual eyeball check; everything the
/// cockpit does with the line is proven here.
/// </summary>
public class ClaudeSdkSessionDriverTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("claude-sdk-driver-tests").FullName;

    [Fact]
    public async Task CanUseTool_SurfacesPermissionRequested_ThenRespondEchoesRequestId()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        // StartAsync puts an SDK client on the control channel first (so the CLI routes approvals here), then applies
        // the launch effort as the session's initial thinking-token budget (default medium).
        Assert.Equal(2, System.Linq.Enumerable.Count(fake.WrittenLines));
        Assert.Equal("initialize", JsonDocument.Parse(fake.WrittenLines[0]).RootElement.GetProperty("request").GetProperty("subtype").GetString());
        Assert.Equal("set_max_thinking_tokens", JsonDocument.Parse(fake.WrittenLines[1]).RootElement.GetProperty("request").GetProperty("subtype").GetString());

        await fake.PushStdoutAsync("""
        {"type":"control_request","request_id":"req-42","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm -rf /"},"tool_use_id":"toolu_7"}}
        """);

        var permission = (PluginPermissionRequested)await _ReadEventAsync(driver, e => e is PluginPermissionRequested);
        Assert.Equal("toolu_7", permission.ToolUseId);
        Assert.Equal("Bash", permission.ToolName);

        await driver.RespondToPermissionAsync("toolu_7", allow: false, CancellationToken.None);

        // The deny is written back as a control_response keyed on the CLI's own request_id, not the tool_use_id.
        var response = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("response");
        Assert.Equal("req-42", response.GetProperty("request_id").GetString());
        Assert.Equal("deny", response.GetProperty("response").GetProperty("behavior").GetString());
    }

    [Fact]
    public async Task CanUseTool_Allow_EchoesTheOriginalToolInputAsUpdatedInput()
    {
        // Proven red before the fix: the driver hard-coded originalInputJson to "{}", so an approved Bash call went
        // back with updatedInput:{} and the CLI would have run it with no command at all. The real input the CLI sent
        // must ride back verbatim — the cockpit approves the call, it does not rewrite it.
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await fake.PushStdoutAsync("""
        {"type":"control_request","request_id":"req-5","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls -la"},"tool_use_id":"toolu_3"}}
        """);
        await _ReadEventAsync(driver, e => e is PluginPermissionRequested);

        await driver.RespondToPermissionAsync("toolu_3", allow: true, CancellationToken.None);

        var decision = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("response").GetProperty("response");
        Assert.Equal("allow", decision.GetProperty("behavior").GetString());
        Assert.Equal("ls -la", decision.GetProperty("updatedInput").GetProperty("command").GetString());
    }

    [Fact]
    public async Task RespondToPermission_ForUnknownTool_WritesNothing()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);
        var writtenAfterStart = fake.WrittenLines.Count;

        // No pending approval under this id (the CLI auto-allowed, or it already resolved) — nothing to feed back.
        await driver.RespondToPermissionAsync("never-seen", allow: true, CancellationToken.None);

        Assert.Equal(writtenAfterStart, fake.WrittenLines.Count);
    }

    [Fact]
    public async Task StreamJsonLine_IsMappedToTranscriptEvents()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await fake.PushStdoutAsync("""
        {"type":"stream_event","session_id":"s-1","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}}
        """);

        var delta = (PluginAssistantTextDelta)await _ReadEventAsync(driver, e => e is PluginAssistantTextDelta);
        Assert.Equal("Hi", delta.Text);
    }

    [Fact]
    public async Task SendUserMessage_WritesTheStreamJsonUserPayload()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await driver.SendUserMessageAsync("hello", CancellationToken.None);

        var payload = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement;
        Assert.Equal("user", payload.GetProperty("type").GetString());
        var message = payload.GetProperty("message");
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("hello", message.GetProperty("content").GetString());
    }

    [Fact]
    public async Task SendUserMessage_WithImages_WritesTextAndImageContentBlocks()
    {
        // Regression: moving Claude to a plugin must not lose image input the in-tree route had. With an attachment the
        // content becomes an array (a text block + one base64 image block), not a plain string.
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await driver.SendUserMessageAsync(
            "what is this?",
            new[] { new PluginImageAttachment("image/png", "aGVsbG8=") },
            CancellationToken.None);

        var content = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("what is this?", content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        var source = content[1].GetProperty("source");
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal("aGVsbG8=", source.GetProperty("data").GetString());
    }

    [Fact]
    public void Capabilities_ReportSupportsVision()
    {
        var fake = new FakeClaudeSdkSubprocess();
        var driver = _CreateDriver(fake);

        Assert.True(driver.Capabilities.SupportsVision);
    }

    [Fact]
    public async Task SetLiveOption_Model_SendsSetModelControlRequest()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: "opus", workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await driver.SetLiveOptionAsync(ClaudeSdkSessionDriver.ModelOptionKey, "sonnet", CancellationToken.None);

        var request = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("request");
        Assert.Equal("set_model", request.GetProperty("subtype").GetString());
        Assert.Equal("sonnet", request.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SetLiveOption_Effort_SwitchesTheThinkingTokenBudget_ForTheLevel()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await driver.SetLiveOptionAsync(ClaudeSdkSessionDriver.EffortOptionKey, "high", CancellationToken.None);

        // Effort is the CLI's thinking-token budget (set_max_thinking_tokens); "high" maps to the plugin's own
        // per-level tuning (24k) — the same budget the host's SessionOptionCatalog carried before Claude became a plugin.
        // The field is snake_case (max_thinking_tokens) exactly as the Agent SDK's Query.set_max_thinking_tokens
        // writes it; camelCase is silently dropped by the CLI, so the budget would never change — the effort-not-live bug.
        var request = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("request");
        Assert.Equal("set_max_thinking_tokens", request.GetProperty("subtype").GetString());
        Assert.Equal(24_000, request.GetProperty("max_thinking_tokens").GetInt32());
    }

    [Fact]
    public async Task LiveOptions_IncludeEffort_WithFriendlyLabels()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        var effort = driver.LiveOptions.Single(option => option.Key == ClaudeSdkSessionDriver.EffortOptionKey);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, effort.Choices);
        Assert.Equal("Extra high", effort.ChoiceLabels!["xhigh"]);
        Assert.Equal("medium", effort.DefaultValue);
    }

    [Fact]
    public async Task LiveOptions_PermissionMode_ExcludesBypass_WhichIsLaunchOnly()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        var permissionOption = driver.LiveOptions.Single(option => option.Key == ClaudeSdkSessionDriver.PermissionModeOptionKey);
        Assert.Equivalent(new object[] { "default", "acceptEdits", "plan" }, permissionOption.Choices);
        Assert.DoesNotContain("bypassPermissions", permissionOption.Choices);
    }

    [Fact]
    public async Task LiveOptions_OmitPermissionMode_WhenLaunchedInBypass()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(
            model: null,
            workingDirectory: _tempDir,
            resumeSessionId: null,
            options: new Dictionary<string, string> { ["permission-mode"] = "bypassPermissions" },
            mcpServers: null,
            CancellationToken.None);

        // Bypass cannot be left mid-session, so no live permission-mode switch is offered at all.
        Assert.DoesNotContain(driver.LiveOptions, option => option.Key == ClaudeSdkSessionDriver.PermissionModeOptionKey);
    }

    // The profile's environment variables (AC-22) ride the environment-carrying StartAsync overload into the
    // spawn; the driver's own rules — the ANTHROPIC_* drop and the config-dir export — keep the last word.
    [Fact]
    public async Task Start_AppliesTheProfilesEnvironmentVariablesToTheSpawn()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null,
            environment: new Dictionary<string, string> { ["AI_OS_ROOT"] = "/home/raymond/AI-OS" },
            CancellationToken.None);

        Assert.NotNull(fake.EnvironmentVariables);
        Assert.Contains(new KeyValuePair<string, string?>("AI_OS_ROOT", "/home/raymond/AI-OS"), fake.EnvironmentVariables);
    }

    // AC-146: sub-agent activity is worth seeing by default (Raymond, 2026-07-29) — an env var rather than a CLI
    // flag, since an older CLI that does not know it just ignores it, where an unknown flag would refuse to start.
    [Fact]
    public async Task Start_SetsTheForwardSubagentTextEnvironmentVariable_OnByDefault()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null,
            CancellationToken.None);

        Assert.NotNull(fake.EnvironmentVariables);
        Assert.Contains(new KeyValuePair<string, string?>("CLAUDE_CODE_FORWARD_SUBAGENT_TEXT", "1"), fake.EnvironmentVariables);
    }

    [Fact]
    public async Task Start_AProfileSuppliedForwardSubagentTextValue_IsNotOverridden()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null,
            environment: new Dictionary<string, string> { ["CLAUDE_CODE_FORWARD_SUBAGENT_TEXT"] = "0" },
            CancellationToken.None);

        Assert.NotNull(fake.EnvironmentVariables);
        Assert.Contains(new KeyValuePair<string, string?>("CLAUDE_CODE_FORWARD_SUBAGENT_TEXT", "0"), fake.EnvironmentVariables);
    }

    [Fact]
    public async Task Start_AProfileSuppliedAnthropicCredential_IsRemovedFromTheSpawnNotHandedToTheCli()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null,
            environment: new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "smuggled" },
            CancellationToken.None);

        // Null = remove at the subprocess seam: the key must be an explicit removal, never the smuggled value.
        Assert.NotNull(fake.EnvironmentVariables);
        Assert.Contains(new KeyValuePair<string, string?>("ANTHROPIC_API_KEY", null), fake.EnvironmentVariables);
    }

    [Fact]
    public async Task Start_AProfileVariableCannotRedirectTheConfigDir_TheProfilesOwnDirWins()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null,
            environment: new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = "/somebody/elses/profile" },
            CancellationToken.None);

        Assert.NotNull(fake.EnvironmentVariables);
        Assert.Contains(new KeyValuePair<string, string?>("CLAUDE_CONFIG_DIR", _tempDir), fake.EnvironmentVariables);
    }

    // AC-378: with registry servers resolved, the spawn carries --strict-mcp-config alongside --mcp-config, so the
    // CLI never unions in its own user/project claude.ai-connectors on top of what the resolution produced.
    [Fact]
    public async Task Start_WithMcpServers_SpawnsWithStrictMcpConfig()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null,
            mcpServers: [new PluginMcpServer { Name = "youtrack", Url = "http://example/mcp" }],
            CancellationToken.None);

        Assert.Contains("--mcp-config", fake.Arguments!);
        Assert.Contains("--strict-mcp-config", fake.Arguments!);
    }

    // AC-378, criterion 4 — the empty-resolution trap: a narrowing that resolves to zero eligible servers must
    // still spawn with an explicit (empty) --mcp-config and --strict-mcp-config, never with the flag dropped
    // entirely (which would let the CLI fall back to its own full user/project config — MORE servers than an
    // empty, narrowed resolution asked for).
    [Fact]
    public async Task Start_WithNoMcpServers_StillSpawnsWithAnExplicitEmptyMcpConfig_AndStrict()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(
            model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null,
            mcpServers: [],
            CancellationToken.None);

        Assert.Contains("--mcp-config", fake.Arguments!);
        Assert.Contains("--strict-mcp-config", fake.Arguments!);

        var mcpConfigIndex = fake.Arguments!.ToList().IndexOf("--mcp-config");
        var path = fake.Arguments![mcpConfigIndex + 1];
        Assert.True(File.Exists(path), "the strict path must write an explicit config file rather than dropping the flag");
        Assert.Empty(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!.AsObject());
    }

    // Same as above for the mcpServers: null case (a route that never even attempted resolution) — must behave
    // exactly like an empty list, not fall back to dropping --mcp-config.
    [Fact]
    public async Task Start_WithNullMcpServers_StillSpawnsWithAnExplicitEmptyMcpConfig_AndStrict()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);

        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        Assert.Contains("--mcp-config", fake.Arguments!);
        Assert.Contains("--strict-mcp-config", fake.Arguments!);
    }

    private ClaudeSdkSessionDriver _CreateDriver(FakeClaudeSdkSubprocess fake) =>
        // A temp config dir keeps StartAsync's workspace-trust write off the real ~/.claude.json.
        new(() => fake, new ClaudeProviderConfig(ConfigDir: _tempDir), executablePath: "claude");

    private static async Task<PluginSessionEvent> _ReadEventAsync(ClaudeSdkSessionDriver driver, Func<PluginSessionEvent, bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in driver.Events.WithCancellation(cts.Token))
        {
            if (predicate(evt))
            {
                return evt;
            }
        }

        throw new InvalidOperationException("The expected event never arrived before the stream completed.");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
