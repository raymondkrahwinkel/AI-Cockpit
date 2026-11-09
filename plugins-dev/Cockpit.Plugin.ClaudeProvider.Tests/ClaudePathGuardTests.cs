using System.Text.Json;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Shared;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// Category A: the session boundary; a wrong answer here lets a write through without anyone noticing.
public class ClaudePathGuardTests
{
    [Theory]
    [InlineData(GuardrailMode.Strict, "Write", """{"file_path":"{root}/a.txt"}""", false, false)]
    [InlineData(GuardrailMode.Strict, "Write", """{"file_path":"{outside}/guard-probe.txt"}""", true, true)]
    [InlineData(GuardrailMode.Strict, "Edit", """{"file_path":"{root}/../guard-probe.txt"}""", true, true)]
    [InlineData(GuardrailMode.Strict, "Bash", """{"command":"echo x > {outside}/guard-probe.txt"}""", true, true)]
    [InlineData(GuardrailMode.Strict, "PowerShell", """{"command":"Set-Content -Value x -Path {outside}/guard-probe.txt"}""", true, true)]
    [InlineData(GuardrailMode.Strict, "Write", """{"file_path":"sub/a.txt"}""", false, false)]
    [InlineData(GuardrailMode.Warn, "Write", """{"file_path":"{outside}/guard-probe.txt"}""", false, true)]
    public void Evaluate_DeniesWritesOutsideTheRootsInStrict_AndOnlyWarnsInWarn(GuardrailMode mode, string tool, string input, bool deny, bool flagged)
    {
        var root = Path.Combine(Path.GetTempPath(), "cockpit-guard-root");
        var outside = Path.Combine(Path.GetTempPath(), "cockpit-guard-outside");
        var json = input.Replace("{root}", JsonEncodedText.Encode(root).ToString()).Replace("{outside}", JsonEncodedText.Encode(outside).ToString());

        var decision = ClaudePathGuard.Evaluate(tool, json, root, mode, [root]);

        Assert.Equal((deny, flagged), (decision.Deny, decision.Message is not null));
    }
}
