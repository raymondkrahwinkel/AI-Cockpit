using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// Locks the on-disk migration for <see cref="VoiceSettingsEntry.ModelAutoSelected"/> (AC-68 slice 2): a config
/// written before that key existed had a hand-picked transcription model under the old free-text box, so a
/// missing key must read as an explicit choice rather than silently opting the operator into the recommendation.
/// </summary>
public class VoiceSettingsEntryTests
{
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
