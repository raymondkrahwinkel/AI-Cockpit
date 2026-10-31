using System.Text.Json.Serialization;

namespace Cockpit.Core.Profiles;

public sealed record LmStudioLoadOptions(
    double? GpuOffloadRatio = null,
    int? Parallel = null,
    bool? FlashAttention = null,
    string? KCacheQuantization = null,
    string? VCacheQuantization = null,
    bool? OffloadKvCacheToGpu = null,
    bool? DraftMtp = null,
    string? DraftModel = null,
    int? DraftMaxTokens = null,
    int? DraftMinTokens = null,
    double? DraftMinContinueProbability = null,
    int? EvalBatchSize = null,
    int? PhysicalBatchSize = null,
    int? CpuThreads = null)
{
    public static IReadOnlyList<string> CacheQuantizations { get; } =
        ["f32", "f16", "q8_0", "q4_0", "q4_1", "iq4_nl", "q5_0", "q5_1"];

    [JsonIgnore]
    public string? ValidationError =>
        GpuOffloadRatio is { } gpu && (!double.IsFinite(gpu) || gpu is < 0 or > 1) ? "GPU offload must be between 0 and 1." :
        Parallel is < 1 ? "Parallel sessions must be at least 1." :
        EvalBatchSize is < 1 ? "Evaluation batch size must be at least 1." :
        PhysicalBatchSize is < 1 ? "Physical batch size must be at least 1." :
        PhysicalBatchSize > EvalBatchSize ? "Physical batch size cannot exceed the evaluation batch size." :
        CpuThreads is < 1 ? "CPU threads must be at least 1." :
        KCacheQuantization is { } k && !CacheQuantizations.Contains(k) ? "Choose a supported K cache type." :
        VCacheQuantization is { } v && !CacheQuantizations.Contains(v) ? "Choose a supported V cache type." :
        FlashAttention == false && (KCacheQuantization is not null || VCacheQuantization is not null) ? "Cache quantization requires Flash Attention." :
        DraftMtp == true && !string.IsNullOrWhiteSpace(DraftModel) ? "Choose MTP or a draft model." :
        DraftMaxTokens is < 1 || DraftMinTokens is < 0 ? "Draft token counts must be positive (minimum may be zero)." :
        DraftMinTokens > DraftMaxTokens ? "Draft minimum cannot exceed its maximum." :
        DraftMinContinueProbability is { } probability && (!double.IsFinite(probability) || probability is < 0 or > 1) ? "Draft probability must be between 0 and 1." :
        null;
}
