using Dapper;
using Npgsql;

namespace LedgerDotNet.Core;

public sealed class LedgerRepository
{
    private readonly string _connectionString;

    public LedgerRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task AppendOperationAsync(
        Guid operationId,
        IReadOnlyList<LedgerEntryInput> entries,
        string idempotencyKey,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            // Insert operation record for idempotency (will fail if duplicate)
            await conn.ExecuteAsync(
                """
                INSERT INTO operations (operation_id, idempotency_key, created_at)
                VALUES (@OperationId, @IdempotencyKey, now())
                """,
                new { OperationId = operationId, IdempotencyKey = idempotencyKey },
                tx
            );

            // Acquire advisory locks in deterministic order to prevent deadlocks
            var accountIds = entries.Select(e => e.AccountId).Distinct().OrderBy(id => id).ToList();
            foreach (var accountId in accountIds)
            {
                await conn.ExecuteAsync(
                    "SELECT pg_advisory_xact_lock(hashtext(@AccountId::text))",
                    new { AccountId = accountId },
                    tx
                );
            }

            foreach (var entry in entries)
            {
                // Determine next sequence number (now safe due to lock)
                var nextSeq = await conn.ExecuteScalarAsync<long?>(
                    """
                    SELECT MAX(sequence_number)
                    FROM ledger_entries
                    WHERE account_id = @AccountId
                    """,
                    new { entry.AccountId },
                    tx
                ) ?? 0;

                nextSeq++;

                // Insert ledger entry
                await conn.ExecuteAsync(
                    """
                    INSERT INTO ledger_entries (
                        entry_id,
                        operation_id,
                        account_id,
                        sequence_number,
                        amount_cents,
                        currency,
                        event_type
                    )
                    VALUES (
                        @EntryId,
                        @OperationId,
                        @AccountId,
                        @SequenceNumber,
                        @AmountCents,
                        @Currency,
                        @EventType
                    )
                    """,
                    new
                    {
                        EntryId = Guid.NewGuid(),
                        OperationId = operationId,
                        entry.AccountId,
                        SequenceNumber = nextSeq,
                        entry.AmountCents,
                        entry.Currency,
                        entry.EventType
                    },
                    tx
                );

                // Update balance cache atomically
                await conn.ExecuteAsync(
                    """
                    INSERT INTO account_balances (account_id, balance_cents, updated_at)
                    VALUES (@AccountId, @Delta, now())
                    ON CONFLICT (account_id)
                    DO UPDATE
                    SET balance_cents = account_balances.balance_cents + @Delta,
                        updated_at = now()
                    """,
                    new
                    {
                        AccountId = entry.AccountId,
                        Delta = entry.AmountCents
                    },
                    tx
                );
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<long> GetBalanceAsync(Guid accountId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var balance = await conn.ExecuteScalarAsync<long?>(
            "SELECT balance_cents FROM account_balances WHERE account_id = @AccountId",
            new { AccountId = accountId }
        );

        return balance ?? 0;
    }

    public async Task<IReadOnlyList<LedgerEntry>> GetLedgerEntriesAsync(
        Guid accountId,
        int limit,
        int offset,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var entries = await conn.QueryAsync<LedgerEntry>(
            """
            SELECT 
                entry_id AS EntryId,
                operation_id AS OperationId,
                account_id AS AccountId,
                sequence_number AS SequenceNumber,
                amount_cents AS AmountCents,
                currency AS Currency,
                event_type AS EventType,
                occurred_at AS OccurredAt
            FROM ledger_entries
            WHERE account_id = @AccountId
            ORDER BY sequence_number
            LIMIT @Limit OFFSET @Offset
            """,
            new { AccountId = accountId, Limit = limit, Offset = offset }
        );

        return entries.ToList();
    }
}

public sealed record LedgerEntry(
    Guid EntryId,
    Guid OperationId,
    Guid AccountId,
    long SequenceNumber,
    long AmountCents,
    string Currency,
    string EventType,
    DateTime OccurredAt
);
