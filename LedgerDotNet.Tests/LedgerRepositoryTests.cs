using LedgerDotNet.Core;
using Npgsql;
using Xunit;

namespace LedgerDotNet.Tests;

public class LedgerRepositoryTests : IAsyncLifetime
{
    private const string ConnectionString = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=devpass";
    private readonly LedgerRepository _repository;

    public LedgerRepositoryTests()
    {
        _repository = new LedgerRepository(ConnectionString);
    }

    public async Task InitializeAsync()
    {
        // Clean tables before each test
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("TRUNCATE ledger_entries, account_balances, operations", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Transfer_Is_Atomic()
    {
        // Arrange
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        var entries = new[]
        {
            new LedgerEntryInput(accountA, -1000, "USD", "transfer.debit"),
            new LedgerEntryInput(accountB, 1000, "USD", "transfer.credit")
        };

        // Act
        await _repository.AppendOperationAsync(operationId, entries, "op-1", CancellationToken.None);

        // Assert
        var balanceA = await _repository.GetBalanceAsync(accountA, CancellationToken.None);
        var balanceB = await _repository.GetBalanceAsync(accountB, CancellationToken.None);

        Assert.Equal(-1000, balanceA);
        Assert.Equal(1000, balanceB);
    }

    [Fact]
    public async Task Duplicate_Idempotency_Key_Is_Rejected()
    {
        // Arrange
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();

        var entries = new[]
        {
            new LedgerEntryInput(accountA, -500, "USD", "transfer.debit"),
            new LedgerEntryInput(accountB, 500, "USD", "transfer.credit")
        };

        // Act
        await _repository.AppendOperationAsync(Guid.NewGuid(), entries, "dup-key", CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<PostgresException>(() =>
            _repository.AppendOperationAsync(Guid.NewGuid(), entries, "dup-key", CancellationToken.None)
        );
    }

    [Fact]
    public async Task Concurrent_Transfers_Preserve_Order_And_Balance()
    {
        // Arrange
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();

        // Act - 20 concurrent transfers
        var tasks = Enumerable.Range(0, 20)
            .Select(i =>
                _repository.AppendOperationAsync(
                    Guid.NewGuid(),
                    new[]
                    {
                        new LedgerEntryInput(accountA, -100, "USD", "transfer.debit"),
                        new LedgerEntryInput(accountB, 100, "USD", "transfer.credit")
                    },
                    $"op-{i}",
                    CancellationToken.None
                )
            );

        await Task.WhenAll(tasks);

        // Assert
        var balanceA = await _repository.GetBalanceAsync(accountA, CancellationToken.None);
        var balanceB = await _repository.GetBalanceAsync(accountB, CancellationToken.None);

        Assert.Equal(-2000, balanceA);
        Assert.Equal(2000, balanceB);

        // Verify sequence numbers are correct
        var entriesA = await _repository.GetLedgerEntriesAsync(accountA, 100, 0, CancellationToken.None);
        var sequenceNumbers = entriesA.Select(e => (int)e.SequenceNumber).ToList();

        Assert.Equal(Enumerable.Range(1, 20).ToList(), sequenceNumbers);
    }

    [Fact]
    public async Task Balance_Cache_Can_Be_Rebuilt_From_Ledger()
    {
        // Arrange
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();

        await _repository.AppendOperationAsync(
            Guid.NewGuid(),
            new[]
            {
                new LedgerEntryInput(accountA, -1000, "USD", "transfer.debit"),
                new LedgerEntryInput(accountB, 1000, "USD", "transfer.credit")
            },
            "op-1",
            CancellationToken.None
        );

        var originalBalanceA = await _repository.GetBalanceAsync(accountA, CancellationToken.None);

        // Act - Delete cache
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DELETE FROM account_balances", conn);
        await cmd.ExecuteNonQueryAsync();

        // Rebuild from ledger
        await using var rebuildCmd = new NpgsqlCommand(
            """
            INSERT INTO account_balances (account_id, balance_cents, updated_at)
            SELECT account_id, SUM(amount_cents), now()
            FROM ledger_entries
            GROUP BY account_id
            """,
            conn
        );
        await rebuildCmd.ExecuteNonQueryAsync();

        // Assert
        var rebuiltBalanceA = await _repository.GetBalanceAsync(accountA, CancellationToken.None);
        Assert.Equal(originalBalanceA, rebuiltBalanceA);
    }
}
