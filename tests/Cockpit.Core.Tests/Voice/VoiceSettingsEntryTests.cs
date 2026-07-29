using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Configuration;

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

        Assert.Equal("http://legacy:9999", entry.ToDomain().VoiceLlmBaseUrl);
    }

    [Fact]
    public void ToDomain_MigratesRenamedCleanupBaseUrl_WhenNeutralKeyAbsent()
    {
        var entry = new VoiceSettingsEntry { VoiceLlmBaseUrl = null, CleanupBaseUrl = "http://cleanup:1234" };

        Assert.Equal("http://cleanup:1234", entry.ToDomain().VoiceLlmBaseUrl);
    }

    [Fact]
    public void ToDomain_PrefersNeutralKey_OverLegacy()
    {
        var entry = new VoiceSettingsEntry { VoiceLlmBaseUrl = "http://new:1234", CleanupBaseUrl = "http://old:1", OllamaBaseUrl = "http://legacy:9999" };

        Assert.Equal("http://new:1234", entry.ToDomain().VoiceLlmBaseUrl);
    }

    [Fact]
    public void ToDomain_FallsBackToDefault_WhenNoKeyPresent()
    {
        var entry = new VoiceSettingsEntry { VoiceLlmBaseUrl = null, CleanupBaseUrl = null, OllamaBaseUrl = null };

        Assert.Equal("http://localhost:11434", entry.ToDomain().VoiceLlmBaseUrl);
    }

    [Fact]
    public void ToDomain_MigratesRenamedCleanupModel_WhenNeutralKeyAbsent()
    {
        var entry = new VoiceSettingsEntry { VoiceLlmModel = null, CleanupModel = "qwen2.5:3b-instruct" };

        Assert.Equal("qwen2.5:3b-instruct", entry.ToDomain().VoiceLlmModel);
    }

    [Fact]
    public void ToDomain_ModelFallsBackToAuto_WhenNoKeyPresent()
    {
        // No model key at all = "Auto" (empty), which the resolver reads as "let auto-detect choose".
        var entry = new VoiceSettingsEntry { VoiceLlmModel = null, CleanupModel = null };

        Assert.Empty(entry.ToDomain().VoiceLlmModel);
    }

    [Fact]
    public void FromDomain_NeverPopulatesTheLegacyKeys()
    {
        var entry = VoiceSettingsEntry.FromDomain(new VoiceSettings { VoiceLlmBaseUrl = "http://x:1", VoiceLlmModel = "m" });

        // Legacy keys stay null so they are not written back (JsonIgnore WhenWritingNull), leaving only the neutral keys on disk.
        Assert.Null(entry.OllamaBaseUrl);
        Assert.Null(entry.CleanupBaseUrl);
        Assert.Null(entry.CleanupModel);
        Assert.Equal("http://x:1", entry.VoiceLlmBaseUrl);
        Assert.Equal("m", entry.VoiceLlmModel);
    }

    [Fact]
    public void ToDomain_TreatsAMissingModelAutoKey_AsAnExplicitChoice()
    {
        // AC-68 slice 2: a config saved before the key existed had a hand-picked model under the old free-text box,
        // so a missing key must not flip the model to the recommendation behind the operator's back.
        var entry = new VoiceSettingsEntry { ModelName = "small", ModelAutoSelected = null };

        Assert.False(entry.ToDomain().ModelAutoSelected);
    }

    [Fact]
    public void ModelAutoSelected_RoundTrips_WhenSetExplicitly()
    {
        Assert.True(new VoiceSettingsEntry { ModelAutoSelected = true }.ToDomain().ModelAutoSelected);
        Assert.Equal(true, VoiceSettingsEntry.FromDomain(new VoiceSettings { ModelAutoSelected = true }).ModelAutoSelected);
    }

    [Fact]
    public void AFreshInstall_DefaultsToTheAutoModel()
    {
        // The domain default is Auto, so a brand-new config (no voice section on disk) starts on the recommendation.
        Assert.True(new VoiceSettings().ModelAutoSelected);
    }
}
