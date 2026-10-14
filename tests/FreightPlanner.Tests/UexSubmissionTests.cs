using System.Net;
using System.Text.Json;

namespace FreightCopilot.Tests;

public sealed class UexSubmissionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 30, 0, DateTimeKind.Utc);
    private readonly string _directory = Directory.CreateTempSubdirectory("freight-planner-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static MarketObservation Row(int commodity, string name, double? buy, double? sell, string sizes, int minutesAgo = 1) => new()
    {
        StarSystem = "Nyx", TerminalId = 778, TerminalName = "Levski", CommodityId = commodity, CommodityName = name, PriceBuy = buy, PriceSell = sell,
        StockScu = buy is null ? null : 84, Status = buy is null ? null : 3, ContainerSizes = sizes,
        ObservedUtc = Now.AddMinutes(-minutesAgo), Source = PriceSource.Screenshot, Verified = true
    };

    [Fact]
    public void Rows_with_different_container_sizes_go_in_separate_submissions()
    {
        var batches = UexSubmissionBuilder.Build(
            [Row(1, "Agricium", 8_379, null, "1,2,4,8,16"), Row(91, "Construction Materials", 8_640, null, "1,2,4,8"), Row(33, "Beryl", 15_390, null, "16,8,4,2,1")],
            production: false, attachScreenshot: false, Now);

        Assert.Equal(2, batches.Count);
        var wide = batches.Single(x => x.Submission.ContainerSizes == "1,2,4,8,16");
        Assert.Equal([1, 33], wide.Submission.Prices.Select(x => x.IdCommodity));
        Assert.Equal(2, wide.Rows.Count);
        Assert.Equal(91, Assert.Single(batches.Single(x => x.Submission.ContainerSizes == "1,2,4,8").Submission.Prices).IdCommodity);
    }

    [Fact]
    public void A_buy_row_and_a_sell_row_use_their_own_fields()
    {
        var sell = Row(18, "Compboard", null, 34_650, "");
        sell.StockScu = 63;
        sell.Status = 5;
        sell.DemandScu = 88;
        var submission = Assert.Single(UexSubmissionBuilder.Build([Row(1, "Agricium", 8_379, null, ""), sell], false, false, Now)).Submission;

        using var json = JsonDocument.Parse(UexClient.Serialize(submission));
        var root = json.RootElement;
        Assert.Equal(778, root.GetProperty("id_terminal").GetInt32());
        Assert.Equal("commodity", root.GetProperty("type").GetString());
        Assert.Equal(0, root.GetProperty("is_production").GetInt32());
        Assert.False(root.TryGetProperty("container_sizes", out _));
        Assert.False(root.TryGetProperty("screenshot", out _));
        Assert.False(root.TryGetProperty("date_added", out _));

        var bought = root.GetProperty("prices")[0];
        Assert.Equal(["id_commodity", "price_buy", "scu_buy", "status_buy"], bought.EnumerateObject().Select(x => x.Name));
        Assert.Equal(8_379, bought.GetProperty("price_buy").GetDouble());
        Assert.Equal(84, bought.GetProperty("scu_buy").GetInt32());
        Assert.Equal(3, bought.GetProperty("status_buy").GetInt32());

        // Demand is the player's own note; UEX gets the shop quantity only.
        var sold = root.GetProperty("prices")[1];
        Assert.Equal(["id_commodity", "price_sell", "scu_sell", "status_sell"], sold.EnumerateObject().Select(x => x.Name));
        Assert.Equal(63, sold.GetProperty("scu_sell").GetInt32());
    }

    [Fact]
    public void Production_is_only_set_when_asked_for()
    {
        Assert.Equal(0, UexSubmissionBuilder.Build([Row(1, "Agricium", 8_379, null, "")], production: false, false, Now)[0].Submission.IsProduction);
        Assert.Equal(1, UexSubmissionBuilder.Build([Row(1, "Agricium", 8_379, null, "")], production: true, false, Now)[0].Submission.IsProduction);
    }

    [Fact]
    public void An_older_observation_carries_the_time_it_was_seen()
    {
        var old = Row(1, "Agricium", 8_379, null, "", minutesAgo: 90);
        var submission = UexSubmissionBuilder.Build([old], false, false, Now)[0].Submission;
        Assert.Equal(new DateTimeOffset(old.ObservedUtc).ToUnixTimeSeconds(), submission.DateAdded);
        Assert.Null(UexSubmissionBuilder.Build([Row(1, "Agricium", 8_379, null, "", minutesAgo: 2)], false, false, Now)[0].Submission.DateAdded);
    }

    [Fact]
    public void The_screenshot_is_attached_only_when_asked_for()
    {
        var file = Path.Combine(_directory, "shot.jpg");
        File.WriteAllBytes(file, [1, 2, 3]);
        var row = Row(1, "Agricium", 8_379, null, "");
        row.ScreenshotPath = file;

        Assert.Equal("AQID", UexSubmissionBuilder.Build([row], false, attachScreenshot: true, Now)[0].Submission.Screenshot);
        Assert.Null(UexSubmissionBuilder.Build([row], false, attachScreenshot: false, Now)[0].Submission.Screenshot);
    }

    // A row as UEX wants it: checked, with price, shop quantity and status, and its screenshot on disk.
    private MarketObservation Proven(int commodity, string name, double? buy, double? sell, int minutesAgo = 1)
    {
        var file = Path.Combine(_directory, "terminal.jpg");
        if (!File.Exists(file)) File.WriteAllBytes(file, [1, 2, 3]);
        var row = Row(commodity, name, buy, sell, "1,2,4,8,16", minutesAgo);
        (row.StockScu, row.Status, row.ScreenshotPath) = (84, 3, file);
        return row;
    }

    [Fact]
    public void A_checked_complete_row_with_its_screenshot_may_go()
    {
        Assert.Empty(UexSubmissionBuilder.Blockers([Proven(1, "Agricium", 8_379, null), Proven(18, "Compboard", null, 34_650)], (_, _) => Now.AddMinutes(-6), Now));
    }

    [Fact]
    public void Unchecked_repeated_recent_and_old_rows_are_held_back()
    {
        var open = Proven(1, "Agricium", 8_379, null);
        open.Verified = false;
        Assert.Contains(UexSubmissionBuilder.Blockers([open], (_, _) => null, Now), x => x.Contains("not ticked as checked"));

        Assert.Contains(UexSubmissionBuilder.Blockers([Proven(1, "Agricium", 8_379, null), Proven(1, "Agricium", 8_380, null)], (_, _) => null, Now), x => x.Contains("2 times"));
        Assert.Contains(UexSubmissionBuilder.Blockers([Proven(1, "Agricium", 8_379, null)], (_, _) => Now.AddMinutes(-3), Now), x => x.Contains("five minutes"));
        Assert.Contains(UexSubmissionBuilder.Blockers([Proven(1, "Agricium", 8_379, null, minutesAgo: 31 * 24 * 60)], (_, _) => null, Now), x => x.Contains("30 days"));

        var fromUex = Proven(1, "Agricium", 8_379, null);
        fromUex.Source = PriceSource.Uex;
        Assert.Contains(UexSubmissionBuilder.Blockers([fromUex], (_, _) => null, Now), x => x.Contains("came from UEX"));
    }

    // UEX's Data Runner Quick Guide: "Enter all required values for each commodity: inventory status, SCU amount,
    // price per SCU" and "Never report incomplete data".
    [Fact]
    public void A_price_without_shop_quantity_or_status_stays_home()
    {
        // What the terminal pays for the player's own goods: a price and nothing else.
        var ownGoods = Proven(18, "Compboard", null, 34_650);
        (ownGoods.StockScu, ownGoods.Status) = (null, null);
        Assert.Contains("shop quantity", Assert.Single(UexSubmissionBuilder.Blockers([ownGoods], (_, _) => null, Now)));

        var noStatus = Proven(1, "Agricium", 8_379, null);
        noStatus.Status = null;
        Assert.Contains("inventory status", Assert.Single(UexSubmissionBuilder.Blockers([noStatus], (_, _) => null, Now)));

        // An empty shelf is a figure too.
        var soldOut = Proven(1, "Agricium", 8_379, null);
        (soldOut.StockScu, soldOut.Status) = (0, 1);
        Assert.Empty(UexSubmissionBuilder.Blockers([soldOut], (_, _) => null, Now));
    }

    // The guide again: "No screenshot means no valid report."
    [Fact]
    public void A_row_without_its_screenshot_stays_home()
    {
        var typedIn = Proven(1, "Agricium", 8_379, null);
        typedIn.ScreenshotPath = "";
        Assert.Contains("no screenshot", Assert.Single(UexSubmissionBuilder.Blockers([typedIn], (_, _) => null, Now)));

        var deleted = Proven(1, "Agricium", 8_379, null);
        deleted.ScreenshotPath = Path.Combine(_directory, "gone.jpg");
        Assert.Contains("no screenshot", Assert.Single(UexSubmissionBuilder.Blockers([deleted], (_, _) => null, Now)));
    }

    [Fact]
    public void Only_the_rows_with_a_problem_are_held_back()
    {
        var good = Proven(1, "Agricium", 8_379, null);
        var ownGoods = Proven(18, "Compboard", null, 34_650);
        (ownGoods.StockScu, ownGoods.Status) = (null, null);

        var held = UexSubmissionBuilder.Held([good, ownGoods], (_, _) => null, Now);

        Assert.Same(ownGoods, Assert.Single(held).Key);
    }

    private sealed class FakeUex(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Sent { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null) Sent = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        }
    }

    private static readonly UexCredentials Credentials = new(" app-token-for-tests ", "secret-key-for-tests\n");

    [Fact]
    public async Task A_submission_is_posted_to_uex_with_both_credentials()
    {
        var uex = new FakeUex(HttpStatusCode.OK, """{"status":"ok","http_code":200,"data":{"ids_reports":"101,102","date_added":1791635400,"username":"garbaek"}}""");
        var submission = UexSubmissionBuilder.Build([Row(1, "Agricium", 8_379, null, "1,2,4,8,16")], false, false, Now)[0].Submission;

        var result = await new UexClient(_directory, uex).SubmitAsync(submission, Credentials, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("101,102", result.ReportIds);
        Assert.Equal("garbaek", result.Username);
        Assert.Equal(HttpMethod.Post, uex.Request!.Method);
        Assert.Equal("https://api.uexcorp.uk/2.0/data_submit", uex.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", uex.Request.Headers.Authorization!.Scheme);
        Assert.Equal("app-token-for-tests", uex.Request.Headers.Authorization.Parameter);
        Assert.Equal("secret-key-for-tests", Assert.Single(uex.Request.Headers.GetValues("secret-key")));
        Assert.Equal("application/json", uex.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("\"container_sizes\":\"1,2,4,8,16\"", uex.Sent);
        Assert.DoesNotContain("token-for-tests", uex.Sent);
        Assert.DoesNotContain("secret-key-for-tests", uex.Sent);
    }

    [Fact]
    public async Task A_personal_trade_is_added_to_uex_and_its_remote_id_is_returned()
    {
        var uex = new FakeUex(HttpStatusCode.OK, """{"status":"ok","data":{"id_user_trade":7123}}""");

        var result = await new UexClient(_directory, uex).AddTradeAsync(new UexTradeSubmission
        {
            IdTerminal = 843, IdCommodity = 18, Operation = "buy", Scu = 24, Price = 28_114, IsProduction = 1
        }, Credentials, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(7123, result.TradeId);
        Assert.Equal(HttpMethod.Post, uex.Request!.Method);
        Assert.Equal("https://api.uexcorp.uk/2.0/user_trades_add", uex.Request.RequestUri!.ToString());
        Assert.Contains("\"operation\":\"buy\"", uex.Sent);
        Assert.Contains("\"id_terminal\":843", uex.Sent);
        Assert.Contains("\"id_commodity\":18", uex.Sent);
        Assert.Contains("\"scu\":24", uex.Sent);
        Assert.Contains("\"price\":28114", uex.Sent);
        Assert.Contains("\"is_production\":1", uex.Sent);
        Assert.DoesNotContain("for-tests", uex.Sent);
    }

    [Fact]
    public async Task A_refused_personal_trade_has_a_safe_explanation()
    {
        var uex = new FakeUex(HttpStatusCode.OK, """{"status":"user_not_verified","data":null}""");

        var result = await new UexClient(_directory, uex).AddTradeAsync(new UexTradeSubmission
        {
            IdTerminal = 778, IdCommodity = 18, Operation = "sell", Scu = 24, Price = 34_650
        }, Credentials, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("verified", result.Message);
        Assert.DoesNotContain("for-tests", result.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"status":"not_allowed","http_code":403,"data":null,"message":"Sorry. Authentication error."}""", "app token")]
    [InlineData(HttpStatusCode.OK, """{"status":"invalid_secret_key","data":null}""", "secret key")]
    [InlineData(HttpStatusCode.OK, """{"status":"user_not_allowed","data":null}""", "not a data runner")]
    [InlineData(HttpStatusCode.OK, """{"status":"user_disabled","data":null}""", "banned or blocked")]
    [InlineData(HttpStatusCode.OK, """{"status":"duplicated_report","data":null}""", "five minutes")]
    [InlineData(HttpStatusCode.OK, """{"status":"too_many_reports","data":null}""", "30 minutes")]
    [InlineData(HttpStatusCode.OK, """{"status":"requests_limit_reached","data":null}""", "request limit")]
    [InlineData(HttpStatusCode.TooManyRequests, "slow down", "request limit")]
    [InlineData(HttpStatusCode.OK, """{"status":"screenshot_required","data":null}""", "screenshot")]
    [InlineData(HttpStatusCode.OK, """{"status":"invalid_status_buy","data":null,"message":"status_buy out of range"}""", "invalid_status_buy")]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>", "HTTP 502")]
    public async Task A_refusal_is_explained_without_revealing_the_credentials(HttpStatusCode code, string body, string expected)
    {
        var submission = UexSubmissionBuilder.Build([Row(1, "Agricium", 8_379, null, "")], false, false, Now)[0].Submission;

        var result = await new UexClient(_directory, new FakeUex(code, body)).SubmitAsync(submission, Credentials, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(expected, result.Message);
        Assert.DoesNotContain("for-tests", result.Message);
        Assert.Null(result.ReportIds);
    }

    [Fact]
    public async Task The_credential_check_reads_the_user_and_submits_nothing()
    {
        var uex = new FakeUex(HttpStatusCode.OK, """{"status":"ok","data":{"username":"garbaek","is_datarunner":1,"is_datarunner_banned":0}}""");

        var user = await new UexClient(_directory, uex).UserAsync(Credentials, CancellationToken.None);

        Assert.Equal("garbaek", user.Username);
        Assert.Equal(1, user.IsDatarunner);
        Assert.Equal(HttpMethod.Get, uex.Request!.Method);
        Assert.Equal("https://api.uexcorp.uk/2.0/user", uex.Request.RequestUri!.ToString());
        Assert.Null(uex.Request.Content);
    }

    [Fact]
    public async Task A_wrong_secret_key_fails_the_credential_check()
    {
        var uex = new FakeUex(HttpStatusCode.OK, """{"status":"invalid_secret_key","data":null}""");
        var error = await Assert.ThrowsAsync<UexException>(() => new UexClient(_directory, uex).UserAsync(Credentials, CancellationToken.None));
        Assert.Contains("secret key", error.Message);
    }

    [Fact]
    public async Task Prices_are_read_and_then_served_from_the_cache()
    {
        var uex = new FakeUex(HttpStatusCode.OK,
            """{"status":"ok","data":[{"id_commodity":18,"id_terminal":778,"price_buy":0,"price_sell":34000,"scu_sell_stock":63,"scu_sell":88,"status_sell":5,"container_sizes":"1,2,4,8,16,24,32","date_modified":1791437400,"commodity_name":"Compboard"}]}""");

        var first = await new UexClient(_directory, uex).PricesAsync(CancellationToken.None);
        var price = Assert.Single(first.Rows);
        Assert.Equal(34_000, price.PriceSell);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791437400).UtcDateTime, price.ReportedUtc);

        var offline = new FakeUex(HttpStatusCode.InternalServerError, "down");
        var second = await new UexClient(_directory, offline).PricesAsync(CancellationToken.None);
        Assert.Equal(34_000, Assert.Single(second.Rows).PriceSell);
        Assert.Null(offline.Request);
    }

    [Fact]
    public async Task No_prices_are_made_up_when_uex_is_down_and_nothing_is_cached()
    {
        var error = await Assert.ThrowsAsync<UexException>(() =>
            new UexClient(_directory, new FakeUex(HttpStatusCode.InternalServerError, "down")).PricesAsync(CancellationToken.None));
        Assert.Contains("UEX could not be reached", error.Message);
    }
}
