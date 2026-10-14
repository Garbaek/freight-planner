using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreightCopilot;

public sealed class UexTerminal
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Nickname { get; set; }
    public string? Displayname { get; set; }
    public string? StarSystemName { get; set; }
    public string? PlanetName { get; set; }
    public int? IsAvailableLive { get; set; }

    // "HUR-L1 Green Glade Station" rather than "HUR-L1", but "TDD Area 18" rather than "Area18".
    [JsonIgnore] public string Label => Displayname is { Length: > 0 } display && Nickname is { Length: > 0 } nick
        ? display.StartsWith(nick, StringComparison.OrdinalIgnoreCase) ? display : nick
        : Nickname ?? Displayname ?? Name ?? $"Terminal {Id}";
    // UEX's own name where it says more than the label: which office the terminal is in and where in the city,
    // as in "TDD - Trade and Development Division - Commons - New Babbage". Empty when it adds nothing.
    [JsonIgnore] public string FullName => Name is { Length: > 0 } name && !name.Equals(Label, StringComparison.OrdinalIgnoreCase) ? name : "";
    [JsonIgnore] public string Region => string.Join(", ", new[] { PlanetName, StarSystemName }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public override string ToString() => Region.Length > 0 ? $"{Label} — {Region}" : Label;
}

public sealed class UexRoute
{
    public int IdCommodity { get; set; }
    public int IdTerminalDestination { get; set; }
    public string? CommodityName { get; set; }
    public double? PriceOrigin { get; set; }
    public double? PriceDestination { get; set; }
    public double? ScuOrigin { get; set; }
    public double? ScuDestination { get; set; }
    public double? Distance { get; set; }
    public string? ContainerSizesOrigin { get; set; }
    public string? ContainerSizesDestination { get; set; }
    public string? GameVersionOrigin { get; set; }
    public long? DateAdded { get; set; }
    public string? OriginStarSystemName { get; set; }
    public string? DestinationStarSystemName { get; set; }
    public string? DestinationPlanetName { get; set; }
    public string? DestinationTerminalName { get; set; }
}

public sealed class UexCommodity
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public int? IsIllegal { get; set; }
    public int? IsAvailableLive { get; set; }
    public override string ToString() => Name ?? $"Commodity {Id}";
}

// One commodity at one terminal as UEX last had it reported. DateModified is the time of that report.
public sealed class UexPrice
{
    public int IdCommodity { get; set; }
    public int IdTerminal { get; set; }
    public double? PriceBuy { get; set; }
    public double? PriceSell { get; set; }
    public double? ScuBuy { get; set; }
    public double? ScuSellStock { get; set; }
    public double? ScuSell { get; set; }
    public int? StatusBuy { get; set; }
    public int? StatusSell { get; set; }
    public string? ContainerSizes { get; set; }
    public long? DateModified { get; set; }
    public string? CommodityName { get; set; }

    [JsonIgnore] public DateTime? ReportedUtc => DateModified is > 0 ? DateTimeOffset.FromUnixTimeSeconds(DateModified.Value).UtcDateTime : null;

    // The buy or the sell side as a price with its source and age, or null when UEX has no price for that side.
    public PriceQuote? Quote(bool buy)
    {
        var value = buy ? PriceBuy : PriceSell;
        return value is > 0 ? new PriceQuote(value.Value, PriceSource.Uex, ReportedUtc, buy ? ScuBuy : ScuSell, ContainerSizes) : null;
    }
}

public sealed class UexUser
{
    public string? Username { get; set; }
    public int? IsDatarunner { get; set; }
    public int? IsDatarunnerBanned { get; set; }
}

// The app token comes from https://uexcorp.space/api/apps, the secret key from the user's UEX profile.
public sealed record UexCredentials(string AppToken, string SecretKey);

public sealed class UexPriceRow
{
    public int IdCommodity { get; set; }
    public double? PriceBuy { get; set; }
    public double? PriceSell { get; set; }
    public int? ScuBuy { get; set; }
    public int? ScuSell { get; set; }
    public int? StatusBuy { get; set; }
    public int? StatusSell { get; set; }
}

// The body of POST data_submit as documented at https://uexcorp.space/api/documentation/id/post_data_submit/.
public sealed class UexSubmission
{
    public int IdTerminal { get; set; }
    public string Type { get; set; } = "commodity";
    public int IsProduction { get; set; }
    public List<UexPriceRow> Prices { get; set; } = [];
    public string? ContainerSizes { get; set; }
    public string? Details { get; set; }
    public string? Screenshot { get; set; }
    public long? DateAdded { get; set; }
}

public sealed record UexSubmitResult(bool Ok, string Status, string Message, string? ReportIds = null, string? Username = null);

// One side of a run in the user's personal UEX trade history. UEX represents a haul as a buy row and a sell row.
public sealed class UexTradeSubmission
{
    public int IdTerminal { get; set; }
    public int IdCommodity { get; set; }
    public string Operation { get; set; } = "";
    public int Scu { get; set; }
    public double Price { get; set; }
    public int IsProduction { get; set; } = 1;
}

public sealed record UexTradeResult(bool Ok, string Status, string Message, long? TradeId = null);

public sealed record UexData<T>(List<T> Rows, DateTime FetchedUtc);

public sealed class UexException(string message) : Exception(message);

// Talks to the UEX API (https://uexcorp.space/api/documentation). Reading is anonymous and cached in uex-cache/
// next to the app, and the last answer is reused when UEX cannot be reached. Credentials are only ever attached
// to requests for BaseUrl, and only for the credential check, an approved price submission, or an enabled
// Bought/Sold personal trade-history checkpoint.
public sealed class UexClient(string appDirectory, HttpMessageHandler? handler = null)
{
    private const string BaseUrl = "https://api.uexcorp.uk/2.0/";
    // UEX allows 120 requests a minute; this keeps the app under 100.
    private static readonly TimeSpan MinimumSpacing = TimeSpan.FromMilliseconds(600);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Shared = CreateHttp(null);
    private static DateTime _lastRequestUtc;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly HttpClient _http = handler is null ? Shared : CreateHttp(handler);
    private readonly string _cacheDirectory = Path.Combine(appDirectory, "uex-cache");

    public Task<UexData<UexTerminal>> CommodityTerminalsAsync(CancellationToken cancellation) =>
        GetAsync<UexTerminal>("terminals?type=commodity", "terminals", TimeSpan.FromHours(24), cancellation);

    public Task<UexData<UexCommodity>> CommoditiesAsync(CancellationToken cancellation) =>
        GetAsync<UexCommodity>("commodities", "commodities", TimeSpan.FromHours(24), cancellation);

    public Task<UexData<UexRoute>> RoutesFromAsync(int terminalId, CancellationToken cancellation) =>
        GetAsync<UexRoute>($"commodities_routes?id_terminal_origin={terminalId}", $"routes-{terminalId}", TimeSpan.FromMinutes(30), cancellation);

    // Every reported price at every terminal in one request, so the station the player is at is not sent.
    public Task<UexData<UexPrice>> PricesAsync(CancellationToken cancellation) =>
        GetAsync<UexPrice>("commodities_prices_all", "prices-all", TimeSpan.FromMinutes(30), cancellation);

    // Asks UEX who the credentials belong to. Nothing is submitted.
    public async Task<UexUser> UserAsync(UexCredentials credentials, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "user");
        var (code, body) = await SendAsync(request, credentials, cancellation);
        var (status, message, data) = ReadEnvelope(body);
        if (status != "ok" || data.ValueKind != JsonValueKind.Object) throw new UexException(Explain(status, message, code));
        return data.Deserialize<UexUser>(Json) ?? new UexUser();
    }

    public async Task<UexSubmitResult> SubmitAsync(UexSubmission submission, UexCredentials credentials, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "data_submit")
        {
            Content = new StringContent(Serialize(submission), Encoding.UTF8, "application/json")
        };
        var (code, body) = await SendAsync(request, credentials, cancellation);
        var (status, message, data) = ReadEnvelope(body);
        if (status != "ok" || code is < 200 or > 299) return new UexSubmitResult(false, status, Explain(status, message, code));
        return new UexSubmitResult(true, status, "", Field(data, "ids_reports"), Field(data, "username"));
    }

    public async Task<UexTradeResult> AddTradeAsync(UexTradeSubmission trade, UexCredentials credentials, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "user_trades_add")
        {
            Content = new StringContent(JsonSerializer.Serialize(trade, Json), Encoding.UTF8, "application/json")
        };
        var (code, body) = await SendAsync(request, credentials, cancellation);
        var (status, message, data) = ReadEnvelope(body);
        if (status != "ok" || code is < 200 or > 299)
            return new UexTradeResult(false, status, ExplainTrade(status, message, code));
        return new UexTradeResult(true, status, "", long.TryParse(Field(data, "id_user_trade"), out var id) ? id : null);
    }

    public static string Serialize(UexSubmission submission) => JsonSerializer.Serialize(submission, Json);

    // What a UEX status means for the player and what to do about it.
    public static string Explain(string status, string message, int httpCode) => status switch
    {
        "not_allowed" or "no_api_found" => "UEX did not accept the app token. Check it under Settings.",
        "missing_secret_key" or "invalid_secret_key" or "missing_secret_key_or_username" => "UEX did not accept the secret key. Check it under Settings.",
        "user_not_found" => "UEX knows no user for this secret key.",
        // UEX documents this as "applied restriction (temporary ban, user not a datarunner, etc)".
        "user_not_allowed" => "UEX does not take reports from this account yet. Its usual reason is that the account is not a data runner: " +
                              "sign in at uexcorp.space and become one there (the Data section), then send again. Check with UEX on the Settings tab shows the account's state.",
        "user_disabled" => "UEX has banned or blocked this account from reporting.",
        "requests_limit_reached" => "UEX's request limit is reached. Wait a minute and try again.",
        "too_many_reports" => "UEX takes at most 1,000 reports in 30 minutes. Try again later.",
        "duplicated_report" => "UEX already got this commodity at this terminal in the last five minutes.",
        "screenshot_required" => "UEX wants a screenshot with reports from new data runners. Report from a screenshot and keep \"Attach the screenshot\" on.",
        "screenshot_length_exceeds_limit" => "The screenshot is over UEX's 10 MB limit.",
        "terminal_not_found" or "not_allowed_player_terminal" => "UEX does not take reports for this terminal.",
        "invalid_date" => "The observation is too old: UEX takes reports from the last 30 days.",
        "service_unavailable" or "database_error" => "UEX is temporarily unavailable. Try again later.",
        _ when httpCode == 429 => "UEX's request limit is reached. Wait a minute and try again.",
        _ => $"UEX answered \"{status}\"" + (message.Length > 0 ? $": {message}" : httpCode is < 200 or > 299 ? $" (HTTP {httpCode})." : ".")
    };

    private static string ExplainTrade(string status, string message, int httpCode) => status switch
    {
        "not_allowed" or "no_api_found" => "UEX did not accept the app token. Check it under Settings.",
        "missing_secret_key" or "invalid_secret_key" => "UEX did not accept the secret key. Check it under Settings.",
        "user_not_found" => "UEX knows no user for this secret key.",
        "user_not_verified" => "UEX requires this account to be verified on the RSI website before it can keep trade history.",
        "user_not_allowed" or "user_disabled" => "UEX does not allow this account to add trade history.",
        "terminal_not_found" => "UEX no longer recognizes this terminal.",
        "commodity_not_found" => "UEX no longer recognizes this commodity.",
        "vehicle_not_found" => "UEX no longer recognizes the selected fleet vehicle.",
        "requests_limit_reached" => "UEX's request limit is reached. Try the checkpoint again in a minute.",
        _ when httpCode == 429 => "UEX's request limit is reached. Try the checkpoint again in a minute.",
        _ => $"UEX answered \"{status}\"" + (message.Length > 0 ? $": {message}" : httpCode is < 200 or > 299 ? $" (HTTP {httpCode})." : ".")
    };

    private async Task<(int Code, string Body)> SendAsync(HttpRequestMessage request, UexCredentials credentials, CancellationToken cancellation)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credentials.AppToken.Trim());
        request.Headers.TryAddWithoutValidation("secret-key", credentials.SecretKey.Trim());
        try
        {
            await ThrottleAsync(cancellation);
            using var response = await _http.SendAsync(request, cancellation);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellation));
        }
        catch (HttpRequestException ex)
        {
            throw new UexException($"UEX could not be reached: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new UexException("UEX did not answer in time.");
        }
    }

    private static (string Status, string Message, JsonElement Data) ReadEnvelope(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return (Field(root, "status") ?? "nothing", Field(root, "message") ?? "",
                root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data.Clone() : default);
        }
        catch (JsonException)
        {
            return ("unreadable_answer", "", default);
        }
    }

    private static string? Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString() : null;

    private async Task<UexData<T>> GetAsync<T>(string resource, string cacheName, TimeSpan maxAge, CancellationToken cancellation)
    {
        var path = Path.Combine(_cacheDirectory, cacheName + ".json");
        var cached = ReadCache<T>(path);
        if (cached is not null && DateTime.UtcNow - cached.FetchedUtc < maxAge) return cached;
        try
        {
            await ThrottleAsync(cancellation);
            var json = await _http.GetStringAsync(BaseUrl + resource, cancellation);
            var rows = Parse<T>(json);
            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                File.WriteAllText(path, json);
            }
            catch (IOException) { }
            return new UexData<T>(rows, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UexException ||
                                   (ex is TaskCanceledException && !cancellation.IsCancellationRequested))
        {
            if (cached is not null) return cached;
            throw new UexException(ex switch
            {
                TaskCanceledException => "UEX did not answer in time.",
                JsonException => "UEX sent an answer that could not be read.",
                UexException => ex.Message,
                _ => $"UEX could not be reached: {ex.Message}"
            });
        }
    }

    private static async Task ThrottleAsync(CancellationToken cancellation)
    {
        await Gate.WaitAsync(cancellation);
        try
        {
            var wait = _lastRequestUtc + MinimumSpacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellation);
            _lastRequestUtc = DateTime.UtcNow;
        }
        finally { Gate.Release(); }
    }

    private static UexData<T>? ReadCache<T>(string path)
    {
        try
        {
            return File.Exists(path) ? new UexData<T>(Parse<T>(File.ReadAllText(path)), File.GetLastWriteTimeUtc(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UexException) { return null; }
    }

    private static List<T> Parse<T>(string json)
    {
        var envelope = JsonSerializer.Deserialize<Envelope<T>>(json, Json);
        if (envelope?.Status != "ok" || envelope.Data is null)
            throw new UexException(envelope?.Status == "requests_limit_reached"
                ? Explain("requests_limit_reached", "", 200)
                : $"UEX answered \"{envelope?.Status ?? "nothing"}\".");
        return envelope.Data;
    }

    private static HttpClient CreateHttp(HttpMessageHandler? handler)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"FreightPlanner/{typeof(UexClient).Assembly.GetName().Version?.ToString(3)}");
        return http;
    }

    private sealed class Envelope<T>
    {
        public string? Status { get; set; }
        public List<T>? Data { get; set; }
    }
}

public static class UexSubmissionBuilder
{
    // UEX takes one container-size list per submission, so rows that differ in it go out separately,
    // as do rows read from different screenshots. Each batch keeps the observations it was built from.
    public static List<(UexSubmission Submission, List<MarketObservation> Rows)> Build(IEnumerable<MarketObservation> observations,
        bool production, bool attachScreenshot, DateTime nowUtc)
    {
        var batches = new List<(UexSubmission, List<MarketObservation>)>();
        foreach (var group in observations.GroupBy(x => (x.TerminalId, Sizes: ObservationValidator.NormalizeSizes(x.ContainerSizes) ?? "", x.ScreenshotPath)))
        {
            var rows = group.ToList();
            var oldest = rows.Min(x => x.ObservedUtc);
            batches.Add((new UexSubmission
            {
                IdTerminal = group.Key.TerminalId,
                IsProduction = production ? 1 : 0,
                Prices = rows.Select(Row).ToList(),
                ContainerSizes = group.Key.Sizes.Length > 0 ? group.Key.Sizes : null,
                Screenshot = attachScreenshot ? ReadScreenshot(group.Key.ScreenshotPath) : null,
                // UEX stamps a report with the time it arrives unless told when the price was actually seen.
                DateAdded = nowUtc - oldest > TimeSpan.FromMinutes(5) ? new DateTimeOffset(oldest.ToUniversalTime()).ToUnixTimeSeconds() : null
            }, rows));
        }
        return batches;
    }

    // The rows that may not go to UEX, each with its reasons, checked before the player is asked to approve
    // anything. The rules are UEX's own: its API's limits, and its Data Runner Quick Guide (v1.1), which says its
    // expectations hold for third-party apps too.
    public static Dictionary<MarketObservation, List<string>> Held(IReadOnlyCollection<MarketObservation> rows, Func<int, int, DateTime?> lastSubmission, DateTime nowUtc)
    {
        var held = new Dictionary<MarketObservation, List<string>>();
        foreach (var row in rows)
        {
            if (!row.Verified) Hold(row, "it is not ticked as checked");
            if (row.Source == PriceSource.Uex) Hold(row, "it came from UEX itself");
            foreach (var error in ObservationValidator.Errors(row, nowUtc)) Hold(row, error);
            // Guide: "Enter all required values for each commodity: inventory status, SCU amount, price per SCU."
            // A price read off the player's own goods or off a confirmation has neither of the other two.
            if (row.StockScu is null || row.Status is null) Hold(row, "UEX wants the shop quantity and the inventory status with every price, and this row lacks one (a price from your own cargo always does: report it from the LOCAL MARKET VALUE tab)");
            // Guide: "No screenshot means no valid report."
            if (!File.Exists(row.ScreenshotPath)) Hold(row, "it has no screenshot on this PC, and UEX takes no report without one");
            else if (new FileInfo(row.ScreenshotPath).Length > 10_000_000) Hold(row, "its screenshot is over UEX's 10 MB limit");
            if (nowUtc - row.ObservedUtc > TimeSpan.FromDays(30)) Hold(row, "it is older than the 30 days UEX accepts");
            if (lastSubmission(row.TerminalId, row.CommodityId) is { } last && nowUtc - last < TimeSpan.FromMinutes(5)) Hold(row, "it went to UEX less than five minutes ago");
        }
        foreach (var repeat in rows.GroupBy(x => (x.TerminalId, x.CommodityId, x.IsBuy)).Where(x => x.Count() > 1))
        foreach (var row in repeat)
            Hold(row, $"it is listed {repeat.Count()} times");
        return held;

        void Hold(MarketObservation row, string reason)
        {
            if (!held.TryGetValue(row, out var reasons)) held[row] = reasons = [];
            reasons.Add(reason);
        }
    }

    // The same as one line per row, for showing to the player.
    public static List<string> Blockers(IReadOnlyCollection<MarketObservation> rows, Func<int, int, DateTime?> lastSubmission, DateTime nowUtc) =>
        Held(rows, lastSubmission, nowUtc).Select(x => $"{x.Key.CommodityName} at {x.Key.TerminalName}: {string.Join("; ", x.Value)}.").ToList();

    private static UexPriceRow Row(MarketObservation row) => row.IsBuy
        ? new UexPriceRow { IdCommodity = row.CommodityId, PriceBuy = row.PriceBuy, ScuBuy = row.StockScu, StatusBuy = row.Status }
        : new UexPriceRow { IdCommodity = row.CommodityId, PriceSell = row.PriceSell, ScuSell = row.StockScu, StatusSell = row.Status };

    private static string? ReadScreenshot(string path)
    {
        try
        {
            // UEX refuses screenshots over 10 MB.
            return path.Length > 0 && File.Exists(path) && new FileInfo(path).Length <= 10_000_000 ? Convert.ToBase64String(File.ReadAllBytes(path)) : null;
        }
        catch (IOException) { return null; }
    }
}
