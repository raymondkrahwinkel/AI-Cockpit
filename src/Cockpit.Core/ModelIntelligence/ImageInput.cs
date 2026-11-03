using Cockpit.Core.Profiles;

namespace Cockpit.Core.ModelIntelligence;

public enum ImageInputMode { Auto, On, Off }

public sealed record ImageInputFact(bool? SupportsImageInput, string Source, DateTimeOffset AsOf, TimeSpan TimeToLive)
{
    public bool? At(DateTimeOffset now) => now >= AsOf && now - AsOf <= TimeToLive ? SupportsImageInput : null;
}

public sealed record ImageInputDecision(bool SupportsVision, string Source);

public static class ImageInput
{
    public static ImageInputDecision Resolve(ImageInputMode mode, bool hasTransport, bool? service, bool? openRouter) =>
        !hasTransport ? new(false, "driver has no image transport") : mode switch
        {
            ImageInputMode.On => new(false, "manual On"),
            ImageInputMode.Off => new(false, "manual Off"),
            _ when service is { } supported => new(supported, "service API"),
            _ when openRouter is { } supported => new(supported, "OpenRouter"),
            _ => new(false, "unknown"),
        };
}

public interface IModelImageInput
{
    /// <summary>Reads separate, current service and public facts for exactly this profile and model.</summary>
    Task<(ImageInputFact? Service, ImageInputFact? OpenRouter)> ReadAsync(SessionProfile profile, string? model, CancellationToken cancellationToken = default);

    /// <summary>Whether this profile's registered driver transports images.</summary>
    bool HasTransport(SessionProfile profile);
}
