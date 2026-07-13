namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Which way a ticket may move (#75). Two kinds of board, and only one of them has an answer in the API.
/// <para>
/// A <b>state-machine</b> project has a real transition graph, and YouTrack hands it over per issue
/// (<see cref="YouTrackStateField.PossibleEvents"/>): those, and only those, are the moves. Nothing here needs to be
/// invented — the board already said.
/// </para>
/// <para>
/// An <b>ordinary</b> status field has no graph at all. YouTrack will let you put a ticket from Backlog straight to
/// Released, and the API offers nothing that says otherwise: the "workflow" everyone follows lives in the *order of
/// the columns*, which is the one thing the API does hand over — the bundle's values, in the board's own order.
/// So forward is the next column, back is the previous one, and everything else is a jump the operator can still make
/// deliberately. Pretending we know more than that would be inventing a rule and blaming YouTrack for it.
/// </para>
/// </summary>
internal static class StateFlow
{
    /// <summary>The next column: where "done with this bit" goes. Null at the end of the board, or when nothing says where we are.</summary>
    public static string? Forward(YouTrackStateField state) => _Neighbour(state, +1);

    /// <summary>The previous column: putting something back. Null at the start of the board.</summary>
    public static string? Back(YouTrackStateField state) => _Neighbour(state, -1);

    /// <summary>
    /// Every move this board allows from here, in the board's own order. For a state-machine that is the workflow
    /// itself; otherwise it is every other column, because YouTrack allows every other column and a menu that hid
    /// them would be lying about what the operator can do.
    /// </summary>
    public static IReadOnlyList<string> Elsewhere(YouTrackStateField state)
    {
        var forward = Forward(state);
        var back = Back(state);

        return state.AvailableTargets
            .Where(target => !string.Equals(target, forward, StringComparison.Ordinal)
                && !string.Equals(target, back, StringComparison.Ordinal))
            .ToList();
    }

    private static string? _Neighbour(YouTrackStateField state, int step)
    {
        // A workflow-governed board answers for itself; there is no "next column" to reason about, only the events it
        // allows, and those are already the whole truth.
        if (state.IsStateMachine || state.CurrentValue is not { Length: > 0 } current)
        {
            return null;
        }

        var index = state.Values
            .ToList()
            .FindIndex(value => string.Equals(value, current, StringComparison.Ordinal));

        if (index < 0)
        {
            return null;
        }

        var neighbour = index + step;

        return neighbour >= 0 && neighbour < state.Values.Count ? state.Values[neighbour] : null;
    }
}
