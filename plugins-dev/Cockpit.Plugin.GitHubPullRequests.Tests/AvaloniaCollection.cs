namespace Cockpit.Plugin.GitHubPullRequests.Tests;

/// <summary>Marks the tests that need a platform; xunit builds the fixture once for the whole collection.</summary>
[CollectionDefinition("avalonia")]
public sealed class AvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;
