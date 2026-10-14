using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FreightCopilot;

public sealed class AccountClient
{
    private readonly HttpClient _http;

    public AccountClient(string baseUrl)
    {
        var normalized = baseUrl.Trim().TrimEnd('/') + "/";
        _http = new HttpClient { BaseAddress = new Uri(normalized), Timeout = TimeSpan.FromSeconds(20) };
    }

    public Task<RegistrationResult> ResendVerificationAsync(string email, CancellationToken cancellationToken) =>
        SendAsync<RegistrationResult>("api/auth/resend-verification", new { email }, cancellationToken);

    public Task<AuthSession> LoginAsync(string email, string password, CancellationToken cancellationToken) =>
        SendAsync<AuthSession>("api/auth/login", new { email, password }, cancellationToken);

    public Task<AuthSession> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        SendAsync<AuthSession>("api/auth/refresh", new { refreshToken }, cancellationToken);

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("api/auth/logout", new { refreshToken }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<PairingRegistration> CreatePairingAsync(string accessToken, string deviceName,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/pairing/pc")
        {
            Content = JsonContent.Create(new { deviceName })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AccountApiException(await ReadErrorAsync(response, cancellationToken), (int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<PairingRegistration>(cancellationToken: cancellationToken)
               ?? throw new AccountApiException("The pairing service returned an empty response.");
    }

    public async Task<CommandPoll> PollCommandsAsync(Guid sessionId, string receiverToken, long after,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/pairing/{sessionId}/commands?after={after}");
        request.Headers.Add("X-Receiver-Token", receiverToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AccountApiException(await ReadErrorAsync(response, cancellationToken), (int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<CommandPoll>(cancellationToken: cancellationToken)
               ?? throw new AccountApiException("The pairing service returned an empty response.");
    }

    public async Task PublishPlanAsync(Guid sessionId, string receiverToken, MobilePlanSnapshot plan,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/pairing/{sessionId}/plan")
        {
            Content = JsonContent.Create(plan)
        };
        request.Headers.Add("X-Receiver-Token", receiverToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AccountApiException(await ReadErrorAsync(response, cancellationToken), (int)response.StatusCode);
    }

    public async Task UploadCompletedTradeAsync(string accessToken, CommunityTradeUpload trade, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/trades/completed") { Content = JsonContent.Create(trade) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AccountApiException(await ReadErrorAsync(response, cancellationToken), (int)response.StatusCode);
    }

    public async Task SetTradeProfileAsync(string accessToken, bool leaderboardOptIn, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "api/trades/profile")
        {
            Content = JsonContent.Create(new { leaderboardOptIn })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AccountApiException(await ReadErrorAsync(response, cancellationToken), (int)response.StatusCode);
    }

    public async Task TrackLaunchAsync(string platform, string version, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("api/usage/launch", new { platform, version }, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AccountApiException(await ReadErrorAsync(response, cancellationToken), (int)response.StatusCode);
    }

    private async Task<T> SendAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(path, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AccountApiException(await ReadErrorAsync(response, cancellationToken), (int)response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
               ?? throw new AccountApiException("The account server returned an empty response.");
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            foreach (var name in new[] { "detail", "error", "title" })
                if (root.TryGetProperty(name, out var value) && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!;
        }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(text) ? $"The account server returned HTTP {(int)response.StatusCode}." : text;
    }
}

public sealed class AccountApiException(string message, int statusCode = 0) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class RegistrationResult
{
    public bool VerificationRequired { get; set; }
    public string Message { get; set; } = "";
}

public sealed class AuthSession
{
    public string AccessToken { get; set; } = "";
    public DateTime AccessTokenExpiresAtUtc { get; set; }
    public string RefreshToken { get; set; } = "";
    public DateTime RefreshTokenExpiresAtUtc { get; set; }
    public AccountIdentity Account { get; set; } = new();
}

public sealed class AccountIdentity
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class PairingRegistration
{
    public Guid SessionId { get; set; }
    public string Code { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public string ReceiverToken { get; set; } = "";
    public string LinkToken { get; set; } = "";
}

public sealed class CommandPoll
{
    public bool Paired { get; set; }
    public string DeviceName { get; set; } = "";
    public List<RemoteCommand> Commands { get; set; } = [];
}

public sealed class RemoteCommand
{
    public long Id { get; set; }
    public string Label { get; set; } = "";
    public string Shortcut { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed record MobilePlanSnapshot(long Revision, DateTime UpdatedAtUtc, string Title, string Summary,
    int CurrentStopIndex, bool CurrentStopCompleted,
    IReadOnlyList<MobilePlanStop> Stops,
    IReadOnlyDictionary<string, long>? CheckedAtUnixMs = null);

public sealed record MobilePlanStop(int Number, string Location, int TravelMinutesFromPrevious,
    IReadOnlyList<string> Deliveries, IReadOnlyList<string> Pickups, IReadOnlyList<string> Packing);

public sealed class CommunityTradeUpload
{
    public Guid TradeId { get; set; }
    public int CommodityId { get; set; }
    public string CommodityName { get; set; } = "";
    public int OriginId { get; set; }
    public string OriginName { get; set; } = "";
    public int DestinationId { get; set; }
    public string DestinationName { get; set; } = "";
    public string Ship { get; set; } = "";
    public int Scu { get; set; }
    public double PriceBuy { get; set; }
    public double PriceSell { get; set; }
    public double OperatingCosts { get; set; }
    public DateTime BoughtUtc { get; set; }
    public DateTime SoldUtc { get; set; }
    public bool LeaderboardOptIn { get; set; }
}

