using Whisper.net.Ggml;

namespace Cockpit.Infrastructure.Voice;

// Resolves a `Core.Voice.VoiceSettings.ModelName` string to the matching Whisper.net `GgmlType`.
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

    // Falls back to `GgmlType.Base` for an unrecognized name rather than throwing — a typo'd model name in `cockpit.json` should not brick voice input.
    public static GgmlType Resolve(string modelName) =>
        ByName.GetValueOrDefault(modelName, GgmlType.Base);

    // Whether this name maps to a real curated model rather than the `GgmlType.Base` fallback.
    // The calibration ladder only times known models, since a custom/quantized name would otherwise be measured as
    // Base but shown under its own label.
    public static bool IsKnown(string modelName) => ByName.ContainsKey(modelName);
}
