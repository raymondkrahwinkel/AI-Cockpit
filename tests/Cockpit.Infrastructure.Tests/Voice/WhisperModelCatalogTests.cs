using Cockpit.Infrastructure.Voice;
using Whisper.net.Ggml;

namespace Cockpit.Infrastructure.Tests.Voice;

/// <summary>Custom quantized names (AC-706) used to fall back to Base silently; this pins the parsing that fixed it.</summary>
public sealed class WhisperModelCatalogTests
{
    [Fact]
    public void Resolve_QuantizedName_ReturnsBaseTypeAndQuantization()
    {
        var (type, quantization) = WhisperModelCatalog.Resolve("large-v3-turbo-q5_0");

        Assert.Equal(GgmlType.LargeV3Turbo, type);
        Assert.Equal(QuantizationType.Q5_0, quantization);
    }

    [Fact]
    public void Resolve_UnknownName_FallsBackToBaseWithNoQuantization()
    {
        var (type, quantization) = WhisperModelCatalog.Resolve("not-a-real-model");

        Assert.Equal(GgmlType.Base, type);
        Assert.Equal(QuantizationType.NoQuantization, quantization);
    }

    [Fact]
    public void Resolve_PlainName_ReturnsNoQuantization()
    {
        var (type, quantization) = WhisperModelCatalog.Resolve("large-v3-turbo");

        Assert.Equal(GgmlType.LargeV3Turbo, type);
        Assert.Equal(QuantizationType.NoQuantization, quantization);
    }

    [Fact]
    public void IsKnown_QuantizedName_IsTrue() =>
        Assert.True(WhisperModelCatalog.IsKnown("large-v3-turbo-q8_0"));

    [Fact]
    public void IsKnown_UnknownName_IsFalse() =>
        Assert.False(WhisperModelCatalog.IsKnown("not-a-real-model"));
}
