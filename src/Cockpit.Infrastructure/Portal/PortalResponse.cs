namespace Cockpit.Infrastructure.Portal;

/// <summary>
/// What an XDG portal request came back with: the spec's response code plus whatever the call returned.
/// Handed back raw because what a non-zero code <em>means</em> is the caller's to decide — for push-to-talk
/// any non-success is a failure to arm, for a screenshot <see cref="IsCancelled"/> is the operator pressing
/// Escape on the picker, which is an ordinary answer.
/// </summary>
/// <param name="ResponseCode">The portal's own code: 0 success, 1 cancelled by the operator, 2 ended some other way.</param>
/// <param name="Results">The call's return values, keyed as that portal interface documents them (e.g. <c>uri</c> for a screenshot).</param>
internal readonly record struct PortalResponse(uint ResponseCode, IDictionary<string, object> Results)
{
    /// <summary>The request succeeded and <see cref="Results"/> holds its answer.</summary>
    public bool IsSuccess => ResponseCode == 0;

    /// <summary>The operator dismissed the portal's own dialog. A cancel, not a failure.</summary>
    public bool IsCancelled => ResponseCode == 1;
}
