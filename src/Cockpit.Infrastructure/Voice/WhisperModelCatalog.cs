using Microsoft.Extensions.Logging;
using Whisper.net.Ggml;

namespace Cockpit.Infrastructure.Voice;

// Resolves a `Core.Voice.VoiceSettings.ModelName` string to the matching Whisper.net `GgmlType` plus
// an optional quantization (AC-706): "large-v3-turbo-q5_0" resolves to (LargeV3Turbo, Q5_0) rather than
// silently falling back to Base, which used to happen for any name outside the plain curated list.
internal static class WhisperModelCatalog
{
    private static readonly Dictionary<string, GgmlType> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tiny"] = GgmlType.Tiny,
        ["tiny.en"] = GgmlType.TinyEn,
        ["base"] = GgmlType.Base,
        ["base.en"] = GgmlType.BaseEn,
        ["small"] = GgmlType.Small,
        ["small.en"] = GgmlType.SmallEn,
        ["medium"] = GgmlType.Medium,
        ["medium.en"] = GgmlType.MediumEn,
        ["large-v1"] = GgmlType.LargeV1,
        ["large-v2"] = GgmlType.LargeV2,
        ["large-v3"] = GgmlType.LargeV3,
        ["large-v3-turbo"] = GgmlType.LargeV3Turbo,
    };

    private static readonly Dictionary<string, QuantizationType> QuantizationByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["q4_0"] = QuantizationType.Q4_0,
        ["q4_1"] = QuantizationType.Q4_1,
        ["q5_0"] = QuantizationType.Q5_0,
        ["q5_1"] = QuantizationType.Q5_1,
        ["q8_0"] = QuantizationType.Q8_0,
    };

    // Falls back to `GgmlType.Base`/`NoQuantization` for an unrecognized name rather than throwing — a typo'd
    // model name in `cockpit.json` should not brick voice input, but it is logged so the fallback is not silent.
    public static (GgmlType Type, QuantizationType Quantization) Resolve(string modelName, ILogger? logger = null)
    {
        if (ByName.TryGetValue(modelName, out var plainType))
        {
            return (plainType, QuantizationType.NoQuantization);
        }

        var separatorIndex = modelName.LastIndexOf('-');
        if (separatorIndex > 0
            && QuantizationByName.TryGetValue(modelName[(separatorIndex + 1)..], out var quantization)
            && ByName.TryGetValue(modelName[..separatorIndex], out var baseType))
        {
            return (baseType, quantization);
        }

        logger?.LogWarning("Unknown Whisper model '{Model}'; falling back to the Base model", modelName);
        return (GgmlType.Base, QuantizationType.NoQuantization);
    }

    // Whether this name maps to a real curated model (plain or quantized) rather than the `GgmlType.Base`
    // fallback. The calibration ladder only times known models, since a typo'd name would otherwise be measured
    // as Base but shown under its own label.
    public static bool IsKnown(string modelName)
    {
        if (ByName.ContainsKey(modelName))
        {
            return true;
        }

        var separatorIndex = modelName.LastIndexOf('-');
        return separatorIndex > 0
               && QuantizationByName.ContainsKey(modelName[(separatorIndex + 1)..])
               && ByName.ContainsKey(modelName[..separatorIndex]);
    }
}
