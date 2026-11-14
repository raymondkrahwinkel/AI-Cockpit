namespace Cockpit.Infrastructure.Vault;

internal sealed record VaultGrant(VaultChannel Channel, bool VerifyOnly, string Variable, string Reference, string[] Argv, string? Target, bool StdinNewline)
{
    public int Version => 2;
}
