namespace Cockpit.TestSupport;

/// <summary>
/// Subsequence check used by tests migrated off FluentAssertions' <c>Should().ContainInOrder</c> (AC-372):
/// xunit has no built-in equivalent, and every call site needs the same "these appear, in this relative
/// order, other items may be interleaved" semantics.
/// </summary>
public static class SequenceAssert
{
    public static bool ContainsInOrder<T>(IEnumerable<T> actual, params T[] expected)
    {
        using var enumerator = actual.GetEnumerator();
        foreach (var item in expected)
        {
            var found = false;
            while (enumerator.MoveNext())
            {
                if (EqualityComparer<T>.Default.Equals(enumerator.Current, item))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return true;
    }
}
