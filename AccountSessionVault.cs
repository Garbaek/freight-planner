using System.Text.Json;

namespace FreightCopilot;

public sealed class SavedAccountSession
{
    public int SchemaVersion { get; set; } = 1;
    public string ServerUrl { get; set; } = "";
    public AuthSession Session { get; set; } = new();
}

public sealed class SavedPairingSession
{
    public int SchemaVersion { get; set; } = 1;
    public string ServerUrl { get; set; } = "";
    public Guid AccountId { get; set; }
    public Guid SessionId { get; set; }
    public string ReceiverToken { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public long LastCommandId { get; set; }
}

// Refresh and receiver tokens never enter settings.json. Windows encrypts these two Credential Manager
// entries for the signed-in Windows user; the server and account binding prevents forwarding either secret
// to a different endpoint if settings.json is edited later.
public static class AccountSessionVault
{
    public const string AccountTarget = "FreightPlanner: account session";
    public const string PairingTarget = "FreightPlanner: tablet pairing";

    public static SavedAccountSession? Account() => DecodeAccount(CredentialVault.Read(AccountTarget));
    public static SavedPairingSession? Pairing() => DecodePairing(CredentialVault.Read(PairingTarget));

    public static void SaveAccount(string serverUrl, AuthSession session) =>
        CredentialVault.Write(AccountTarget, EncodeAccount(serverUrl, session));

    public static void SavePairing(string serverUrl, Guid accountId, PairingRegistration registration,
        string? deviceName, long lastCommandId) => CredentialVault.Write(PairingTarget, EncodePairing(new SavedPairingSession
        {
            ServerUrl = NormalizeServer(serverUrl),
            AccountId = accountId,
            SessionId = registration.SessionId,
            ReceiverToken = registration.ReceiverToken,
            DeviceName = deviceName?.Trim() ?? "",
            LastCommandId = Math.Max(0, lastCommandId)
        }));

    public static void ClearAccount() => CredentialVault.Delete(AccountTarget);
    public static void ClearPairing() => CredentialVault.Delete(PairingTarget);

    internal static string EncodeAccount(string serverUrl, AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var saved = new SavedAccountSession { ServerUrl = NormalizeServer(serverUrl), Session = session };
        Validate(saved);
        return JsonSerializer.Serialize(saved, JsonDefaults.Options());
    }

    internal static SavedAccountSession? DecodeAccount(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<SavedAccountSession>(json, JsonDefaults.Options());
            if (saved is null) return null;
            saved.ServerUrl = NormalizeServer(saved.ServerUrl);
            Validate(saved);
            return saved;
        }
        catch (Exception ex) when (ex is JsonException or UriFormatException or InvalidDataException) { return null; }
    }

    internal static string EncodePairing(SavedPairingSession saved)
    {
        Validate(saved);
        saved.ServerUrl = NormalizeServer(saved.ServerUrl);
        return JsonSerializer.Serialize(saved, JsonDefaults.Options());
    }

    internal static SavedPairingSession? DecodePairing(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var saved = JsonSerializer.Deserialize<SavedPairingSession>(json, JsonDefaults.Options());
            if (saved is null) return null;
            saved.ServerUrl = NormalizeServer(saved.ServerUrl);
            saved.DeviceName ??= "";
            Validate(saved);
            return saved;
        }
        catch (Exception ex) when (ex is JsonException or UriFormatException or InvalidDataException) { return null; }
    }

    public static bool SameServer(string left, string right)
    {
        try { return NormalizeServer(left).Equals(NormalizeServer(right), StringComparison.OrdinalIgnoreCase); }
        catch (UriFormatException) { return false; }
    }

    private static string NormalizeServer(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new UriFormatException("The saved account server is not a valid HTTPS URL.");
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static void Validate(SavedAccountSession saved)
    {
        _ = NormalizeServer(saved.ServerUrl);
        var session = saved.Session ?? throw new InvalidDataException("The saved account session is empty.");
        if (session.Account is null || session.Account.Id == Guid.Empty || string.IsNullOrWhiteSpace(session.Account.Email) ||
            string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.RefreshToken) ||
            session.AccessTokenExpiresAtUtc == default || session.RefreshTokenExpiresAtUtc == default)
            throw new InvalidDataException("The saved account session is incomplete.");
    }

    private static void Validate(SavedPairingSession saved)
    {
        _ = NormalizeServer(saved.ServerUrl);
        if (saved.AccountId == Guid.Empty || saved.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(saved.ReceiverToken) || saved.LastCommandId < 0)
            throw new InvalidDataException("The saved tablet pairing is incomplete.");
    }
}
