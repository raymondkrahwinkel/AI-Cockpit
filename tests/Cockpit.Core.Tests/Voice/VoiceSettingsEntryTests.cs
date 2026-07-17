using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Configuration;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// Locks the on-disk migration from the Ollama-specific cleanup key to the neutral OpenAI-compatible one:
/// an existing config wrote <c>OllamaBaseUrl</c>, and loading it must surface that value under the new
/// <c>CleanupBaseUrl</c> — otherwise a laptop that customized the Ollama URL would silently reset to the
/// default on the first run of the generalized build.
/// </summary>
public class VoiceSettingsEntryTests
{
    [Fact]
    public void ToDomain_MigratesLegacyOllamaBaseUrl_WhenNeutralKeyAbsent()
    {
        var entry = new VoiceSettingsEntry { CleanupBaseUrl = null, OllamaBaseUrl = "http://legacy:9999" };

        entry.ToDomain().CleanupBaseUrl.Should().Be("http://legacy:9999");
    }

    [Fact]
    public void ToDomain_PrefersNeutralKey_OverLegacy()
    {
        var entry = new VoiceSettingsEntry { CleanupBaseUrl = "http://new:1234", OllamaBaseUrl = "http://legacy:9999" };

        entry.ToDomain().CleanupBaseUrl.Should().Be("http://new:1234");
    }

    [Fact]
    public void ToDomain_FallsBackToDefault_WhenNeitherKeyPresent()
    {
        var entry = new VoiceSettingsEntry { CleanupBaseUrl = null, OllamaBaseUrl = null };

        entry.ToDomain().CleanupBaseUrl.Should().Be("http://localhost:11434");
    }

    [Fact]
    public void FromDomain_NeverPopulatesTheLegacyKey()
    {
        var entry = VoiceSettingsEntry.FromDomain(new VoiceSettings { CleanupBaseUrl = "http://x:1" });

        // Legacy key stays null so it is not written back (JsonIgnore WhenWritingNull), leaving only the neutral key on disk.
        entry.OllamaBaseUrl.Should().BeNull();
        entry.CleanupBaseUrl.Should().Be("http://x:1");
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
