namespace FreightCopilot.Tests;

public class CredentialVaultTests
{
    [Fact]
    public void A_secret_is_stored_read_back_and_removed()
    {
        // A throwaway entry of its own, so the real UEX credentials are never touched.
        var name = $"FreightPlanner: test {Guid.NewGuid():N}";
        try
        {
            Assert.Null(CredentialVault.Read(name));
            CredentialVault.Write(name, "not a real secret ¤ æøå");
            Assert.Equal("not a real secret ¤ æøå", CredentialVault.Read(name));
            CredentialVault.Write(name, "replaced");
            Assert.Equal("replaced", CredentialVault.Read(name));
        }
        finally
        {
            CredentialVault.Delete(name);
        }
        Assert.Null(CredentialVault.Read(name));
    }
}
