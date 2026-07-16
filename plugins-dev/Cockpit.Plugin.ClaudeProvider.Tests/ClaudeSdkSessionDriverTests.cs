using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeSdkSessionDriver"/> (Fase 4, SDK route) driven against a <see cref="FakeClaudeSdkSubprocess"/> —
/// the turn-taking and, above all, the in-band permission round-trip that replaces the host's HTTP MCP permission
/// server: a <c>can_use_tool</c> control_request surfaces as <see cref="PluginPermissionRequested"/>, and the
/// operator's answer is written back as a <c>control_response</c> echoing the request's own <c>request_id</c>.
/// The live CLI end (that it emits <c>can_use_tool</c> for this spawn) is Raymond's eyeball item; everything the
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
        fake.WrittenLines.Should().HaveCount(2);
        JsonDocument.Parse(fake.WrittenLines[0]).RootElement.GetProperty("request").GetProperty("subtype").GetString().Should().Be("initialize");
        JsonDocument.Parse(fake.WrittenLines[1]).RootElement.GetProperty("request").GetProperty("subtype").GetString().Should().Be("set_max_thinking_tokens");

        await fake.PushStdoutAsync("""
        {"type":"control_request","request_id":"req-42","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"rm -rf /"},"tool_use_id":"toolu_7"}}
        """);

        var permission = (PluginPermissionRequested)await _ReadEventAsync(driver, e => e is PluginPermissionRequested);
        permission.ToolUseId.Should().Be("toolu_7");
        permission.ToolName.Should().Be("Bash");

        await driver.RespondToPermissionAsync("toolu_7", allow: false, CancellationToken.None);

        // The deny is written back as a control_response keyed on the CLI's own request_id, not the tool_use_id.
        var response = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("response");
        response.GetProperty("request_id").GetString().Should().Be("req-42");
        response.GetProperty("response").GetProperty("behavior").GetString().Should().Be("deny");
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
        decision.GetProperty("behavior").GetString().Should().Be("allow");
        decision.GetProperty("updatedInput").GetProperty("command").GetString().Should().Be("ls -la");
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

        fake.WrittenLines.Count.Should().Be(writtenAfterStart);
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
        delta.Text.Should().Be("Hi");
    }

    [Fact]
    public async Task SendUserMessage_WritesTheStreamJsonUserPayload()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await driver.SendUserMessageAsync("hello", CancellationToken.None);

        var payload = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement;
        payload.GetProperty("type").GetString().Should().Be("user");
        var message = payload.GetProperty("message");
        message.GetProperty("role").GetString().Should().Be("user");
        message.GetProperty("content").GetString().Should().Be("hello");
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
        content.ValueKind.Should().Be(JsonValueKind.Array);
        content[0].GetProperty("type").GetString().Should().Be("text");
        content[0].GetProperty("text").GetString().Should().Be("what is this?");
        content[1].GetProperty("type").GetString().Should().Be("image");
        var source = content[1].GetProperty("source");
        source.GetProperty("media_type").GetString().Should().Be("image/png");
        source.GetProperty("data").GetString().Should().Be("aGVsbG8=");
    }

    [Fact]
    public void Capabilities_ReportSupportsVision()
    {
        var fake = new FakeClaudeSdkSubprocess();
        var driver = _CreateDriver(fake);

        driver.Capabilities.SupportsVision.Should().BeTrue();
    }

    [Fact]
    public async Task SetLiveOption_Model_SendsSetModelControlRequest()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: "opus", workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        await driver.SetLiveOptionAsync(ClaudeSdkSessionDriver.ModelOptionKey, "sonnet", CancellationToken.None);

        var request = JsonDocument.Parse(fake.WrittenLines[^1]).RootElement.GetProperty("request");
        request.GetProperty("subtype").GetString().Should().Be("set_model");
        request.GetProperty("model").GetString().Should().Be("sonnet");
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
        request.GetProperty("subtype").GetString().Should().Be("set_max_thinking_tokens");
        request.GetProperty("max_thinking_tokens").GetInt32().Should().Be(24_000);
    }

    [Fact]
    public async Task LiveOptions_IncludeEffort_WithFriendlyLabels()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        var effort = driver.LiveOptions.Single(option => option.Key == ClaudeSdkSessionDriver.EffortOptionKey);
        effort.Choices.Should().Equal("low", "medium", "high", "xhigh", "max");
        effort.ChoiceLabels!["xhigh"].Should().Be("Extra high");
        effort.DefaultValue.Should().Be("medium");
    }

    [Fact]
    public async Task LiveOptions_PermissionMode_ExcludesBypass_WhichIsLaunchOnly()
    {
        var fake = new FakeClaudeSdkSubprocess();
        await using var driver = _CreateDriver(fake);
        await driver.StartAsync(model: null, workingDirectory: _tempDir, resumeSessionId: null, options: null, mcpServers: null, CancellationToken.None);

        var permissionOption = driver.LiveOptions.Single(option => option.Key == ClaudeSdkSessionDriver.PermissionModeOptionKey);
        permissionOption.Choices.Should().BeEquivalentTo(["default", "acceptEdits", "plan"]);
        permissionOption.Choices.Should().NotContain("bypassPermissions");
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
        driver.LiveOptions.Should().NotContain(option => option.Key == ClaudeSdkSessionDriver.PermissionModeOptionKey);
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

        fake.EnvironmentVariables.Should().Contain(new KeyValuePair<string, string?>("AI_OS_ROOT", "/home/raymond/AI-OS"));
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
        fake.EnvironmentVariables.Should().Contain(new KeyValuePair<string, string?>("ANTHROPIC_API_KEY", null));
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

        fake.EnvironmentVariables.Should().Contain(new KeyValuePair<string, string?>("CLAUDE_CONFIG_DIR", _tempDir));
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
