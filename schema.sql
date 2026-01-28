-- Ledger Entries: Immutable source of truth
CREATE TABLE IF NOT EXISTS ledger_entries (
    entry_id           UUID PRIMARY KEY,
    operation_id       UUID NOT NULL,
    account_id         UUID NOT NULL,
    sequence_number    BIGINT NOT NULL,
    amount_cents       BIGINT NOT NULL,
    currency           CHAR(3) NOT NULL,
    event_type         TEXT NOT NULL,
    occurred_at        TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT uq_account_sequence UNIQUE (account_id, sequence_number),
    CONSTRAINT chk_amount_non_zero CHECK (amount_cents <> 0),
    CONSTRAINT chk_currency_iso CHECK (currency ~ '^[A-Z]{3}$')
);

CREATE INDEX IF NOT EXISTS idx_ledger_operation ON ledger_entries(operation_id);
CREATE INDEX IF NOT EXISTS idx_ledger_account ON ledger_entries(account_id);

-- Operations: Track idempotency at operation level
CREATE TABLE IF NOT EXISTS operations (
    operation_id UUID PRIMARY KEY,
    idempotency_key TEXT NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Account Balances: Rebuildable cache for performance
CREATE TABLE IF NOT EXISTS account_balances (
    account_id UUID PRIMARY KEY,
    balance_cents BIGINT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);
