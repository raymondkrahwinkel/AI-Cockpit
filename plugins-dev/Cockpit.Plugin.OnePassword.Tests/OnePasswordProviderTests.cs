namespace Cockpit.Plugin.OnePassword.Tests;

public sealed class OnePasswordProviderTests
{
    [Fact]
    public void TextFields_SshPrivateKeyWithoutReference_ListsOpenSshReference()
    {
        const string itemJson = """
            {
              "id": "item-id",
              "title": "Test SSH key",
              "category": "SSH_KEY",
              "vault": { "id": "vault-id" },
              "fields": [
                { "id": "private_key", "label": "private key", "type": "SSHKEY" }
              ]
            }
            """;

        var item = Assert.Single(OnePasswordProvider.TextFields("Test SSH key", itemJson, "vault-id", "item-id"));

        Assert.Equal("op://vault-id/item-id/private_key?ssh-format=openssh", item.Reference);
    }
}
