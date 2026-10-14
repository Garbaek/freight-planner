using FreightCopilot.Api;
using Altcha.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FreightCopilot.Tests;

public sealed class TradeLedgerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"freight-ledger-{Guid.NewGuid():N}");

    [Fact]
    public async Task PublicStats_AreOptInRankedAndIdempotent()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "trades.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:TradesPath"] = path
        }).Build();
        var ledger = new TradeLedger(config, new TestEnvironment(_directory));
        var alpha = Account("Alpha <Pilot>");
        var bravo = Account("Bravo");
        var privatePilot = Account("Private");
        await ledger.SetProfileAsync(alpha, true, CancellationToken.None);
        await ledger.SetProfileAsync(bravo, true, CancellationToken.None);
        await ledger.SetProfileAsync(privatePilot, false, CancellationToken.None);

        var firstId = Guid.NewGuid();
        await ledger.AddAsync(alpha, Trade(firstId, 10, 100, 150, 50), CancellationToken.None);
        await ledger.AddAsync(alpha, Trade(firstId, 12, 100, 150, 50), CancellationToken.None); // Retry updates; it does not duplicate.
        await ledger.AddAsync(alpha, Trade(Guid.NewGuid(), 5, 200, 300, 0), CancellationToken.None);
        await ledger.AddAsync(bravo, Trade(Guid.NewGuid(), 1, 500, 1_000, 0), CancellationToken.None);
        await ledger.AddAsync(privatePilot, Trade(Guid.NewGuid(), 100, 1, 10_000, 0), CancellationToken.None);

        var stats = await ledger.PublicStatsAsync(CancellationToken.None);

        Assert.Equal(2, stats.Traders);
        Assert.Equal(3, stats.Runs);
        Assert.Equal(18, stats.Scu);
        Assert.Equal(4_300, stats.SalesVolume);
        Assert.Equal(1_550, stats.GrossProfit);
        Assert.Equal(["Alpha <Pilot>", "Bravo"], stats.TopTraders.Select(x => x.DisplayName));
        Assert.Equal([3_300d, 1_000d], stats.TopTraders.Select(x => x.SalesVolume));

        var html = LandingPage.Render([], stats, new PublicUsageStats(7, new Dictionary<string, long> { ["windows"] = 7 }));
        Assert.Contains("Alpha &lt;Pilot&gt;", html);
        Assert.DoesNotContain("Alpha <Pilot>", html);
        Assert.Contains("7 app launches", html);

        await ledger.SetProfileAsync(alpha, false, CancellationToken.None);
        stats = await ledger.PublicStatsAsync(CancellationToken.None);
        Assert.Single(stats.TopTraders);
        Assert.Equal("Bravo", stats.TopTraders[0].DisplayName);
    }

    [Fact]
    public async Task UsageLedger_StoresOnlyAggregateDailyCounters()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "usage.db");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:UsagePath"] = path
        }).Build();
        var ledger = new UsageLedger(config, new TestEnvironment(_directory));

        await ledger.CountLaunchAsync("windows", "0.9.0", CancellationToken.None);
        await ledger.CountLaunchAsync("windows", "0.9.0", CancellationToken.None);
        await ledger.CountLaunchAsync("android", "0.9.0", CancellationToken.None);

        var stats = await ledger.PublicStatsAsync(CancellationToken.None);
        Assert.Equal(3, stats.LaunchesLast30Days);
        Assert.Equal(2, stats.ByPlatform["windows"]);
        Assert.Equal(1, stats.ByPlatform["android"]);

        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var columns = db.CreateCommand();
        columns.CommandText = "SELECT name FROM pragma_table_info('launch_counts') ORDER BY cid";
        using var reader = columns.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        Assert.Equal(["day_utc", "platform", "version", "launches"], names);
    }

    [Fact]
    public void AccountPage_UsesAltchaAndEscapesPrefilledEmail()
    {
        var html = AccountPage.Render("test-nonce", "pilot\"@example.test");

        Assert.Contains("altcha@3.3.0", html);
        Assert.Contains("challenge=\"/altcha/challenge\"", html);
        Assert.Contains("nonce=\"test-nonce\"", html);
        Assert.Contains("pilot&quot;@example.test", html);
        Assert.DoesNotContain("value=\"pilot\"@example.test\"", html);

        IAltchaPayloadCarrier request = new RegisterRequest("pilot@example.test", "Pilot", "long-enough-password", null)
        {
            AltchaPayload = "proof"
        };
        Assert.Equal("proof", request.AltchaPayload);
    }

    private static AccountRecord Account(string name) => new()
    {
        Id = Guid.NewGuid(),
        Email = $"{Guid.NewGuid():N}@example.test",
        DisplayName = name,
        PasswordHash = "test",
        CreatedAtUtc = DateTime.UtcNow
    };

    private static CompletedTradeRequest Trade(Guid id, int scu, double buy, double sell, double costs) => new()
    {
        TradeId = id,
        CommodityId = 1,
        CommodityName = "Gold",
        OriginId = 10,
        OriginName = "Origin",
        DestinationId = 20,
        DestinationName = "Destination",
        Ship = "C2",
        Scu = scu,
        PriceBuy = buy,
        PriceSell = sell,
        OperatingCosts = costs,
        BoughtUtc = DateTime.UtcNow.AddHours(-1),
        SoldUtc = DateTime.UtcNow,
        LeaderboardOptIn = true
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "FreightCopilot.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
