using Cockpit.Plugin.Depot.Secrets;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// How a project's data key travels to Depot (AC-607): wrapped twice, once under the operator's password and once
// under a recovery code shown once at creation, so a forgotten password is not fatal to fields already encrypted
// under it. Built and unwrapped only by CockpitProjectPasswordEnvelopeFactory.
public sealed class CockpitProjectPasswordEnvelope
{
    public string Kdf { get; set; } = ProjectSecretKey.Pbkdf2Sha512;

    public int Iterations { get; set; } = ProjectSecretKey.DefaultIterations;

    public CockpitProjectKeyWrapper Password { get; set; } = new();

    public CockpitProjectKeyWrapper Recovery { get; set; } = new();
}
