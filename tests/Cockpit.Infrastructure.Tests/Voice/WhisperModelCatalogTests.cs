using Cockpit.Infrastructure.Voice;
using Whisper.net.Ggml;

namespace Cockpit.Infrastructure.Tests.Voice;

/// <summary>Custom quantized names (AC-706) used to fall back to Base silently; this pins the parsing that fixed it.</summary>
public sealed class WhisperModelCatalogTests
{
    // One behaviour, one test: a stored model name splits into the ggml type and the quantization it names. The rows
    // are the three shapes a name can have — quantized, plain, and one this build does not know, which falls back to
    // the base model rather than refusing to start dictation at all.
    [Theory]
    [InlineData("large-v3-turbo-q5_0", GgmlType.LargeV3Turbo, QuantizationType.Q5_0)]
    [InlineData("large-v3-turbo", GgmlType.LargeV3Turbo, QuantizationType.NoQuantization)]
    [InlineData("not-a-real-model", GgmlType.Base, QuantizationType.NoQuantization)]
    public void Resolve_ReadsTheBaseTypeAndTheQuantizationOutOfTheName(
        string name,
        GgmlType expectedType,
        QuantizationType expectedQuantization)
    {
        var (type, quantization) = WhisperModelCatalog.Resolve(name);

        Assert.Equal(expectedType, type);
        Assert.Equal(expectedQuantization, quantization);
    }

    // The catalogue's other question, and a separate one: Resolve always answers, falling back to Base, so it can
    // never say "this name is not one of ours" — which is what the settings surface needs before it offers a name.
    [Theory]
    [InlineData("large-v3-turbo-q8_0", true)]
    [InlineData("not-a-real-model", false)]
    public void IsKnown_SaysWhetherTheNameIsOneTheCatalogueHas(string name, bool known)
    {
        Assert.Equal(known, WhisperModelCatalog.IsKnown(name));
    }
}
