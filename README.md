# LedgerDotNet

**LedgerDotNet is an immutable, event-driven ledger engine designed to prioritize correctness, consistency, and explicit failure semantics over feature breadth or extensibility.**

This project intentionally avoids CRUD abstractions and instead models financial state as an append-only log of immutable events, with all critical invariants enforced at the database level.

The goal is not to build a “fintech app”, but to demonstrate how to design and reason about systems where **incorrect state is worse than unavailable state**.

---

## Why This Project Exists

Most backend systems fail not because of missing features, but because of:

- race conditions under concurrency  
- duplicate writes during retries  
- partial failures that corrupt state  
- unclear guarantees about what the system does and does not promise  

LedgerDotNet explores how to design a **small, bounded system** that:

- makes guarantees explicit
- enforces them mechanically
- proves correctness under real concurrency

---

## Intended Audience

This project is aimed at backend engineers, platform engineers, and systems-minded developers working on stateful domains such as billing, payments, inventory, or internal financial tooling.

It is **not** intended as a general-purpose fintech framework or end-user application.

---

## Core Design Principles

- **Immutability over mutation**  
  Ledger entries are never updated or deleted. Corrections are modeled as new facts.

- **Database as the final arbiter of correctness**  
  Invariants are enforced via PostgreSQL constraints and transactional guarantees, not in-memory logic.

- **Operation-level reasoning**  
  A single business operation (e.g. a transfer) may produce multiple ledger entries and must succeed or fail atomically.

- **Explicit trade-offs**  
  Throughput, scope, and extensibility are consciously constrained to preserve correctness and clarity.

---

## System Guarantees

LedgerDotNet provides the following guarantees:

- **Atomic multi-entry operations**  
  Logical operations (e.g. debit + credit) commit entirely or not at all within a single database transaction.

- **Exactly-once semantics per operation**  
  Duplicate requests are rejected via a dedicated `operations` table with a unique `idempotency_key`.

- **Per-account strict ordering**  
  Ledger entries for each account are assigned monotonically increasing sequence numbers, enforced via transactional advisory locks.

- **Single source of truth**  
  The immutable ledger (`ledger_entries`) is authoritative. Cached balances are derived state and can be fully rebuilt.

- **Crash safety and durability**  
  PostgreSQL transactional guarantees and WAL ensure no partial state survives process or database crashes.

---

## Explicit Non-Guarantees (By Design)

The following concerns are intentionally out of scope:

- **Global or cross-account ordering**  
  Each account is an independent unit of consistency.

- **Business-level validation**  
  Rules such as overdraft protection or currency compatibility are deliberately omitted to keep the core engine pure.

- **Distributed transactions**  
  The system assumes a single PostgreSQL instance as the transaction coordinator.

- **Unbounded throughput**  
  Writes are serialized per account to preserve ordering and correctness.

> **Note**  
> The project intentionally avoids additional features (authentication, UI, background jobs, messaging) to preserve absolute clarity of invariants and reasoning.

---

## Design Rationale: Transactional Advisory Locks

LedgerDotNet uses `pg_advisory_xact_lock` to serialize writes per account.

### Why advisory locks?

- **Compared to SERIALIZABLE isolation**  
  Serializable isolation can lead to retry storms under contention. Advisory locks provide explicit, predictable serialization.

- **Compared to `SELECT ... FOR UPDATE`**  
  Aggregates such as `MAX(sequence_number)` cannot be directly locked. Advisory locks allow locking the logical resource before rows exist.

- **Compared to a dedicated sequence table**  
  A sequence table introduces additional coordination complexity and contention. Advisory locks leverage PostgreSQL’s native lock manager.

### Trade-offs

- Tight coupling to PostgreSQL  
- Theoretical hash collision risk when deriving lock keys (statistically negligible at this scope)

---

## Data Model Overview

- **`ledger_entries`**  
  Immutable event log and single source of truth.

- **`operations`**  
  Operation-level audit trail and idempotency enforcement.

- **`account_balances`**  
  High-performance derived cache, rebuilt deterministically from the ledger.

Key constraints include:

- `UNIQUE (account_id, sequence_number)`
- `UNIQUE (idempotency_key)`
- Currency and amount validation via `CHECK` constraints

---

## Failure Semantics (The “3 AM Test”)

| Scenario            | Behavior                                  | Guarantee        |
|---------------------|--------------------------------------------|------------------|
| Duplicate request   | Rejected by unique constraint              | Exactly-once     |
| Concurrent writes   | Serialized per account                     | Strict ordering |
| Process crash       | Transaction rolled back                    | Atomicity        |
| Database crash      | WAL recovery on restart                    | Durability       |
| Cache corruption    | Rebuilt from ledger                        | Recoverability  |

---

## Correctness Verification

All invariants are verified using **integration tests against a real PostgreSQL instance** running in Docker.  
No database interactions are mocked.

Tests explicitly cover:

- Atomic multi-entry operations  
- Operation-level idempotency  
- Concurrent writes under contention  
- Balance cache rebuild from immutable events  

---

## Running the Project

### Prerequisites

- Docker & Docker Compose  
- .NET 8 SDK  

### Steps

```bash
docker-compose up -d
dotnet test
cd LedgerDotNet.Api
dotnet run
 ```
Why This Is Not a Feature Demo

This repository is intentionally small.

Its purpose is to demonstrate:

systems thinking

correctness-first design

explicit trade-off analysis

comfort with database-driven invariants

Additional features would reduce clarity rather than increase confidence.

License

MIT


---
