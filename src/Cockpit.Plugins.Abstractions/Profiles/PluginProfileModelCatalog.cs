namespace Cockpit.Plugins.Abstractions.Profiles;

/// <summary>Whether a provider could enumerate a set of values.</summary>
public enum PluginCatalogStatus
{
    /// <summary>No verified list is available.</summary>
    Unknown,
    /// <summary>The provider enumerated the values. An empty list means it offers none.</summary>
    Known,
}

/// <summary>The available models for one configured session profile.</summary>
public sealed record PluginProfileModelCatalog(PluginCatalogStatus Status, IReadOnlyList<PluginProfileModel> Models)
{
    public static PluginProfileModelCatalog Unknown { get; } = new(PluginCatalogStatus.Unknown, []);
}

/// <summary>A model and the effort values the provider reports for it.</summary>
public sealed record PluginProfileModel(string Id, PluginCatalogStatus EffortStatus, IReadOnlyList<string> Efforts);
