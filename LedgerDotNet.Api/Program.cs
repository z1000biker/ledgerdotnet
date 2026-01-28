using LedgerDotNet.Core;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=devpass";

builder.Services.AddSingleton(new LedgerRepository(connectionString));

var app = builder.Build();

// POST /operations/transfer
app.MapPost("/operations/transfer", async (
    TransferRequest req,
    LedgerRepository ledger,
    CancellationToken ct) =>
{
    var operationId = Guid.NewGuid();

    var entries = new[]
    {
        new LedgerEntryInput(
            req.FromAccountId,
            -req.AmountCents,
            req.Currency,
            "transfer.debit"
        ),
        new LedgerEntryInput(
            req.ToAccountId,
            req.AmountCents,
            req.Currency,
            "transfer.credit"
        )
    };

    try
    {
        await ledger.AppendOperationAsync(
            operationId,
            entries,
            req.IdempotencyKey,
            ct
        );

        return Results.Ok(new { operationId });
    }
    catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505") // Unique violation
    {
        return Results.Conflict(new { error = "Duplicate operation" });
    }
});

// GET /accounts/{id}/balance
app.MapGet("/accounts/{id}/balance", async (
    Guid id,
    LedgerRepository ledger,
    CancellationToken ct) =>
{
    var balance = await ledger.GetBalanceAsync(id, ct);
    return Results.Ok(new { accountId = id, balanceCents = balance });
});

// GET /accounts/{id}/ledger
app.MapGet("/accounts/{id}/ledger", async (
    Guid id,
    int limit,
    int offset,
    LedgerRepository ledger,
    CancellationToken ct) =>
{
    var entries = await ledger.GetLedgerEntriesAsync(id, limit, offset, ct);
    return Results.Ok(entries);
});

app.Run();

// Request DTOs
public sealed record TransferRequest(
    Guid FromAccountId,
    Guid ToAccountId,
    long AmountCents,
    string Currency,
    string IdempotencyKey
);
