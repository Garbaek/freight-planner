namespace FreightCopilot.Tests;

public sealed class ActiveSessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"freight-session-{Guid.NewGuid():N}");

    public ActiveSessionStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void An_incomplete_trade_run_is_saved_atomically_and_restored_with_the_session()
    {
        var store = new ActiveSessionStore(_directory);
        var origin = new UexTerminal { Id = 843, Name = "PSS Lambda", Nickname = "PSS Lambda", StarSystemName = "Nyx" };
        var run = new TradeRun("Nomad", origin, 778, "Levski", "Nyx", 18, "Compboard", 24,
            28_113.75, 34_650, "12 × 2 SCU", 62, "Mine", "High", DateTime.UtcNow.AddMinutes(-5));
        run.RecordPurchase(20, 28_200, DateTime.UtcNow.AddMinutes(-4));
        run.RecordSale(778, "Levski", 8, 34_000, DateTime.UtcNow.AddMinutes(-1));

        store.Save([], null, run);
        var snapshot = store.Load();
        var restored = TradeRun.Restore(Assert.IsType<TradeRunState>(snapshot.ActiveTradeRun));

        Assert.Equal(2, snapshot.SchemaVersion);
        Assert.Equal((20, 8, 12), (restored.BoughtScu, restored.SoldScu, restored.RemainingScu));
        Assert.True(File.Exists(store.PathOnDisk));
        Assert.False(File.Exists(store.PathOnDisk + ".tmp"));
    }

    [Fact]
    public void A_completed_run_is_not_resurrected_as_active()
    {
        var store = new ActiveSessionStore(_directory);
        var origin = new UexTerminal { Id = 843, Name = "PSS Lambda", Nickname = "PSS Lambda" };
        var run = new TradeRun("Nomad", origin, 778, "Levski", "Nyx", 18, "Compboard", 24,
            28_113.75, 34_650, "12 × 2 SCU", 62, "Mine", "High", DateTime.UtcNow.AddMinutes(-5));
        run.RecordPurchase(24, 28_200, DateTime.UtcNow.AddMinutes(-4));
        run.RecordSale(778, "Levski", 24, 34_000, DateTime.UtcNow.AddMinutes(-1));

        store.Save([], null, run);

        Assert.Null(store.Load().ActiveTradeRun);
    }

    [Fact]
    public void A_newer_valid_temporary_snapshot_recovers_an_interrupted_atomic_replace()
    {
        var store = new ActiveSessionStore(_directory);
        store.Save([], null);
        var older = File.GetLastWriteTimeUtc(store.PathOnDisk);
        var temporary = store.PathOnDisk + ".tmp";
        File.WriteAllText(temporary, "{\"SchemaVersion\":2,\"UpdatedAtUtc\":\"2099-01-01T00:00:00Z\",\"Contracts\":[]}");

        var restored = store.Load();

        Assert.Equal(new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), restored.UpdatedAtUtc);
        Assert.True(File.GetLastWriteTimeUtc(store.PathOnDisk) >= older);
    }

    [Fact]
    public void A_missing_or_corrupt_session_is_marked_as_legacy_for_journal_recovery()
    {
        var store = new ActiveSessionStore(_directory);
        Assert.Equal(0, store.Load().SchemaVersion);
        File.WriteAllText(store.PathOnDisk, "not json");
        Assert.Equal(0, store.Load().SchemaVersion);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
