namespace Cockpit.Infrastructure.Portal;

// What an XDG portal request came back with: the spec's response code plus whatever the call returned.
// Handed back raw because what a non-zero code *means* is the caller's to decide — for push-to-talk
// any non-success is a failure to arm, for a screenshot `IsCancelled` is the operator pressing
// Escape on the picker, which is an ordinary answer.
//
// `ResponseCode`: The portal's own code: 0 success, 1 cancelled by the operator, 2 ended some other way.
// `Results`: The call's return values, keyed as that portal interface documents them (e.g. `uri` for a screenshot).
internal readonly record struct PortalResponse(uint ResponseCode, IDictionary<string, object> Results)
{
    // The request succeeded and `Results` holds its answer.
    public bool IsSuccess => ResponseCode == 0;

    // The operator dismissed the portal's own dialog. A cancel, not a failure.
    public bool IsCancelled => ResponseCode == 1;
}
