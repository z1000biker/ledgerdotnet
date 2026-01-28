namespace LedgerDotNet.Core;

public sealed record LedgerEntryInput(
    Guid AccountId,
    long AmountCents,
    string Currency,
    string EventType
);
