using System.Collections.Concurrent;
using Cockpit.App.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>The in-memory + write-through per-plugin key/value store behind IPluginStorage (#14).</summary>
public class PluginStorageTests
{
    [Fact]
    public void SetThenGet_RoundTripsTypedValues()
    {
        var storage = new PluginStorage(new Dictionary<string, string>(), _ => { });

        storage.Set("token", "ghp_secret");
        storage.Set("count", 42);

        Assert.Equal("ghp_secret", storage.Get<string>("token"));
        Assert.Equal(42, storage.Get<int>("count"));
    }

    [Fact]
    public void Get_MissingKey_ReturnsDefault()
    {
        var storage = new PluginStorage(new Dictionary<string, string>(), _ => { });

        Assert.Null(storage.Get<string>("nope"));
        Assert.Equal(0, storage.Get<int>("nope"));
    }

    [Fact]
    public void SeededValues_AreReadable()
    {
        var storage = new PluginStorage(new Dictionary<string, string> { ["repo"] = "\"owner/name\"" }, _ => { });

        Assert.Equal("owner/name", storage.Get<string>("repo"));
    }

    [Fact]
    public void Set_WritesThroughToPersist()
    {
        IReadOnlyDictionary<string, string>? persisted = null;
        var storage = new PluginStorage(new Dictionary<string, string>(), values => persisted = values);

        storage.Set("k", "v");

        Assert.NotNull(persisted);
        Assert.Contains("k", persisted.Keys);
    }

    /// <summary>
    /// AC-515 blocker 1: <see cref="PluginStorage.Set{T}"/> used to hand its own live <c>_values</c> dictionary to
    /// <c>persist</c>, which is a fire-and-forget callback (the host schedules an async file write from it, well
    /// after <see cref="PluginStorage.Set{T}"/> itself returns). A later, unrelated <c>Set</c> call — from any
    /// thread, e.g. a plugin's background poll timer racing a UI-thread write — mutated that same dictionary while
    /// the callback could still be reading it, which is exactly the "Collection was modified" failure a fire-and-
    /// forget persist can never surface: the exception has nowhere to go. Proven here without any real threading,
    /// deterministically: a second <c>Set</c> call must never change what an earlier persist call already received.
    /// </summary>
    [Fact]
    public void Set_HandsPersistASnapshot_ThatALaterSetDoesNotMutate()
    {
        var persistedCalls = new List<IReadOnlyDictionary<string, string>>();
        var storage = new PluginStorage(new Dictionary<string, string>(), values => persistedCalls.Add(values));

        storage.Set("a", "1");
        var firstCallSnapshot = persistedCalls[0];

        storage.Set("b", "2");

        Assert.Single(firstCallSnapshot);
        Assert.DoesNotContain("b", firstCallSnapshot.Keys);
    }

    /// <summary>
    /// The same bug under real concurrency (AC-515 blocker 1's actual failure mode): a slow persist callback that
    /// is still enumerating the dictionary <c>Set</c> handed it, while other threads call <c>Set</c> for different
    /// keys. Before the fix this reliably throws <see cref="InvalidOperationException"/> ("Collection was
    /// modified") because every call shared and mutated the one live dictionary; after the fix each call gets its
    /// own snapshot, so concurrent callers never see each other's writes mid-enumeration.
    /// </summary>
    [Fact]
    public async Task ConcurrentSets_FromMultipleThreads_DoNotThrow_WhilePersistEnumeratesSlowly()
    {
        var storage = new PluginStorage(new Dictionary<string, string>(), values =>
        {
            foreach (var _ in values)
            {
                Thread.Sleep(1);
            }
        });

        var exceptions = new ConcurrentBag<Exception>();
        var writers = Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 20; i++)
                {
                    storage.Set($"key-{writer}-{i}", i);
                }
            }
            catch (Exception exception)
            {
                exceptions.Add(exception);
            }
        }));

        await Task.WhenAll(writers);

        Assert.Empty(exceptions);
    }
}
