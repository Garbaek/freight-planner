namespace FreightCopilot.Tests;

public class AccountSessionVaultTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private static AuthSession Session() => new()
    {
        AccessToken = "access-secret",
        AccessTokenExpiresAtUtc = Now.AddMinutes(15),
        RefreshToken = "refresh-secret",
        RefreshTokenExpiresAtUtc = Now.AddDays(30),
        Account = new AccountIdentity
        {
            Id = Guid.Parse("37e8ba39-1145-483d-857a-f3a5d0917537"),
            Email = "pilot@example.test",
            DisplayName = "Nomad Pilot",
            CreatedAtUtc = Now.AddDays(-20)
        }
    };

    [Fact]
    public void Account_session_round_trips_with_its_exact_server_binding()
    {
        var json = AccountSessionVault.EncodeAccount("https://example.test/", Session());

        var saved = Assert.IsType<SavedAccountSession>(AccountSessionVault.DecodeAccount(json));

        Assert.Equal("https://example.test", saved.ServerUrl);
        Assert.Equal("refresh-secret", saved.Session.RefreshToken);
        Assert.Equal("Nomad Pilot", saved.Session.Account.DisplayName);
        Assert.True(AccountSessionVault.SameServer("https://EXAMPLE.test/", saved.ServerUrl));
        Assert.False(AccountSessionVault.SameServer("https://other.test", saved.ServerUrl));
    }

    [Fact]
    public void Account_session_rejects_insecure_remote_or_incomplete_secrets()
    {
        Assert.Throws<UriFormatException>(() => AccountSessionVault.EncodeAccount("http://example.test", Session()));
        var incomplete = Session();
        incomplete.RefreshToken = "";
        Assert.Throws<InvalidDataException>(() => AccountSessionVault.EncodeAccount("https://example.test", incomplete));
        Assert.Null(AccountSessionVault.DecodeAccount("not json"));
        Assert.Null(AccountSessionVault.DecodeAccount("{\"serverUrl\":null}"));
    }

    [Fact]
    public void Pairing_cursor_round_trips_so_commands_are_not_replayed_after_restart()
    {
        var pairing = new SavedPairingSession
        {
            ServerUrl = "https://example.test",
            AccountId = Session().Account.Id,
            SessionId = Guid.Parse("32893327-bb40-4b55-bea4-e5188a32ccbf"),
            ReceiverToken = "receiver-secret",
            DeviceName = "Galaxy Tab",
            LastCommandId = 87
        };

        var saved = Assert.IsType<SavedPairingSession>(AccountSessionVault.DecodePairing(AccountSessionVault.EncodePairing(pairing)));

        Assert.Equal(87, saved.LastCommandId);
        Assert.Equal("receiver-secret", saved.ReceiverToken);
        Assert.Equal("Galaxy Tab", saved.DeviceName);
    }

    [Fact]
    public void Localhost_http_remains_available_for_development_only()
    {
        var saved = Assert.IsType<SavedAccountSession>(AccountSessionVault.DecodeAccount(
            AccountSessionVault.EncodeAccount("http://localhost:5080", Session())));
        Assert.Equal("http://localhost:5080", saved.ServerUrl);
        Assert.Throws<UriFormatException>(() => AccountSessionVault.EncodeAccount("http://192.168.1.20:5080", Session()));
    }
}
