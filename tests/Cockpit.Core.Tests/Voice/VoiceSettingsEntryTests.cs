using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Configuration;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// Locks the on-disk migration onto the shared, provider-neutral voice-LLM keys: an existing config wrote the
/// older <c>CleanupBaseUrl</c>/<c>CleanupModel</c> (or the still-older Ollama-specific <c>OllamaBaseUrl</c>),
/// and loading it must surface those values under the new <c>VoiceLlmBaseUrl</c>/<c>VoiceLlmModel</c> — otherwise
/// a laptop that customized the server/model would silently reset to the default on the first run.
/// </summary>
public class VoiceSettingsEntryTests
{
    [Fact]
    public void ToDomain_MigratesLegacyOllamaBaseUrl_WhenNewerKeysAbsent()
    {
        var entry = new VoiceSettingsEntry { VoiceLlmBaseUrl = null, CleanupBaseUrl = null, OllamaBaseUrl = "http://legacy:9999" };

        entry.ToDomain().VoiceLlmBaseUrl.Should().Be("http://legacy:9999");
    }

    [Fact]
    public void ToDomain_MigratesRenamedCleanupBaseUrl_WhenNeutralKeyAbsent()
    {
        var entry = new VoiceSettingsEntry { VoiceLlmBaseUrl = null, CleanupBaseUrl = "http://cleanup:1234" };

        entry.ToDomain().VoiceLlmBaseUrl.Should().Be("http://cleanup:1234");
    }

    [Fact]
    public void ToDomain_PrefersNeutralKey_OverLegacy()
    {
        var entry = new VoiceSettingsEntry { VoiceLlmBaseUrl = "http://new:1234", CleanupBaseUrl = "http://old:1", OllamaBaseUrl = "http://legacy:9999" };

        entry.ToDomain().VoiceLlmBaseUrl.Should().Be("http://new:1234");
    }

    [Fact]
    public void ToDomain_FallsBackToDefault_WhenNoKeyPresent()
    {
        var entry = new VoiceSettingsEntry { VoiceLlmBaseUrl = null, CleanupBaseUrl = null, OllamaBaseUrl = null };

        entry.ToDomain().VoiceLlmBaseUrl.Should().Be("http://localhost:11434");
    }

    [Fact]
    public void ToDomain_MigratesRenamedCleanupModel_WhenNeutralKeyAbsent()
    {
        var entry = new VoiceSettingsEntry { VoiceLlmModel = null, CleanupModel = "qwen2.5:3b-instruct" };

        entry.ToDomain().VoiceLlmModel.Should().Be("qwen2.5:3b-instruct");
    }

    [Fact]
    public void ToDomain_ModelFallsBackToAuto_WhenNoKeyPresent()
    {
        // No model key at all = "Auto" (empty), which the resolver reads as "let auto-detect choose".
        var entry = new VoiceSettingsEntry { VoiceLlmModel = null, CleanupModel = null };

        entry.ToDomain().VoiceLlmModel.Should().BeEmpty();
    }

    [Fact]
    public void FromDomain_NeverPopulatesTheLegacyKeys()
    {
        var entry = VoiceSettingsEntry.FromDomain(new VoiceSettings { VoiceLlmBaseUrl = "http://x:1", VoiceLlmModel = "m" });

        // Legacy keys stay null so they are not written back (JsonIgnore WhenWritingNull), leaving only the neutral keys on disk.
        entry.OllamaBaseUrl.Should().BeNull();
        entry.CleanupBaseUrl.Should().BeNull();
        entry.CleanupModel.Should().BeNull();
        entry.VoiceLlmBaseUrl.Should().Be("http://x:1");
        entry.VoiceLlmModel.Should().Be("m");
    }

    [Fact]
    public void ToDomain_TreatsAMissingModelAutoKey_AsAnExplicitChoice()
    {
        // AC-68 slice 2: a config saved before the key existed had a hand-picked model under the old free-text box,
        // so a missing key must not flip the model to the recommendation behind the operator's back.
        var entry = new VoiceSettingsEntry { ModelName = "small", ModelAutoSelected = null };

        entry.ToDomain().ModelAutoSelected.Should().BeFalse();
    }

    [Fact]
    public void ModelAutoSelected_RoundTrips_WhenSetExplicitly()
    {
        new VoiceSettingsEntry { ModelAutoSelected = true }.ToDomain().ModelAutoSelected.Should().BeTrue();
        VoiceSettingsEntry.FromDomain(new VoiceSettings { ModelAutoSelected = true }).ModelAutoSelected.Should().Be(true);
    }

    [Fact]
    public void AFreshInstall_DefaultsToTheAutoModel()
    {
        // The domain default is Auto, so a brand-new config (no voice section on disk) starts on the recommendation.
        new VoiceSettings().ModelAutoSelected.Should().BeTrue();
    }
}
