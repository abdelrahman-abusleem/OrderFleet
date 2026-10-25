# Architecture decision records (ADRs)

**Process:** Add one ADR per meaningful design choice or risky change. Status = `ACCEPTED`, `PROPOSED`, `SUPERSEDED`. Document context/problem, decision, alternatives, cost/tradeoffs, correctness/failure risks, references, verification. Owner must approve changes that alter established business contracts or architecture.

## ADR-001 — ACCEPTED: modular monolith with Clean Architecture

**Problem:** Portfolio needs credible reliability patterns without ops overhead of microservices. **Decision:** one modular monolith with four current projects, feature boundaries inside layers, optional separate worker host if justified. **Alternatives:** microservices (too much operational overhead), flat N-layer CRUD (weak domain boundaries). **Tradeoff:** share deploy and database, discipline needed to prevent module coupling. **Verification:** domain reference graph and code review.

## ADR-002 — ACCEPTED: Order is aggregate root with owned items

**Decision:** business ownership in `Order`; `OrderItem` is owned via `OwnsMany` and separate relational table. Snapshot names/prices/totals; no independent item lifecycle. **Alternatives:** public mutable item repository/table entity (risks bypassing invariants). **Verification:** real save/reload plus Domain invariants. Existing implementation is binding unless demonstrated defect.

## ADR-003 — ACCEPTED: PostgreSQL + EF Core/Npgsql, PostGIS for Geo

**Decision:** PostgreSQL is durable source of truth; EF Core/Npgsql for persistence; PostGIS for proximity and spatial index. **Alternatives:** EF InMemory (not relational proof), custom geo math only (limited indexed search), separate search engine (extra operations). **Tradeoff:** migrations and provider behavior must be verified. **Special:** base `postgres:17` container not assumed PostGIS-capable; migrate deliberately without data loss.

## ADR-004 — ACCEPTED: trustworthy money snapshots

**Decision:** SAR decimals, server-side catalog prices, fixed order snapshots, limits in business policy; DB precision compatible with allowed limits. **Alternatives:** float/double, client-supplied price, recompute from catalog (rejected). **Risks:** `numeric(18,2)` narrower than C# decimal and policy may allow larger totals; cap and validate carefully. **Verification:** precision/overflow/boundary DB tests.

## ADR-005 — ACCEPTED: DB concurrency plus atomic claims/fencing

**Decision:** combine Order optimistic version conflict detection with atomic courier claim and assignment fencing. Domain guards alone insufficient. **Alternatives:** C# `lock` (one process only), domain checks only (race). **Unresolved implementation detail:** provider-supported `xmin` vs application version column; choose after verifying package/provider and migrations, document final outcome.

## ADR-006 — ACCEPTED: Outbox + idempotent Inbox over RabbitMQ

**Decision:** domain mutation and Outbox same DB transaction; relay publishes after commit; RabbitMQ delivery at least once; consumer dedupe, ack after durable commit. **Alternatives:** direct publish after SaveChanges (lost event window), distributed transaction/“exactly once” (unjustified). **Risks:** duplicate delivery, ordering, broker routing and backpressure. **Verification:** crash/failure tests.

## ADR-007 — ACCEPTED: no external map/payment dependencies for demo

**Decision:** mock provider with explicit financial state and idempotency; basic ETA heuristic with stated limitations; no real charges or paid map service dependency. **Risks:** cannot claim actual payment/navigation accuracy. **Verification:** deterministic mock/ETA tests.

## ADR-008 — ACCEPTED (owner, 2026-10-25): mock payment sequence and cancellation coupling

**Decision:** Option A. Payment is a separate aggregate in the Payments module; **no new `OrderStatus` and no new `CancellationReason`**.
- **Create:** the use case authorizes the mock payment (scoped idempotency key). If authorization is **declined, the order is not persisted** and the API returns a payment-declined ProblemDetails; a transient provider failure returns a retryable 503. On success, order and `Payment(Authorized)` are persisted together with their Outbox rows.
- **Accept:** capture is attempted in the same use case **before** the Accept transition commits. If capture fails the order stays `Created`, the payment is marked failed, and the acceptance timeout later cancels it with `AcceptanceTimedOut` and voids the authorization. A crash after capture and before commit is safe: retrying with the same idempotency key returns the same capture result.
- **Cancel/Reject while `Created`** (`CustomerRequested`, `RestaurantRejected`, `AcceptanceTimedOut`, `AdminCancelled`): void the authorization.
- **Cancel after acceptance** (`RestaurantCancelled`, `AdminCancelled`): refund the captured payment.
- Void and refund run as idempotent consumers of the `order.cancelled.v1` event (Outbox/Inbox), not inside the cancel transaction.

**Alternatives:** (B) authorize and capture only after acceptance (no pre-accept hold; customer can be charged late or fail after acceptance). **Tradeoff:** a pre-accept hold exists for orders the restaurant may never accept. **Verification:** declined, replayed callback, concurrent capture, duplicate refund, cancel-vs-accept race, crash after capture.

## ADR-009 — ACCEPTED (owner, 2026-10-25): auto-assignment

**Decision:** dispatch assigns a courier directly; `Order.Assign` creates the `AssignmentId`. No offer/acceptance market and no offer expiry. Courier availability semantics are offline / available / reserved / busy and are documented in the Couriers module.

## ADR-010 — ACCEPTED (owner, 2026-10-25): identity and token issuing

**Decision:** ASP.NET Core Identity for users, password hashing and roles only (no UI, no external IdP); JWT access tokens validated for signature, issuer, audience and lifetime; **no refresh tokens**. Signing key from User Secrets / environment, never tracked. Seeded demo users in Development only. Resource ownership is checked in the use cases; login endpoint rate-limited. **Tradeoff:** access-token revocation relies on short lifetimes; documented as a limitation.

## ADR-011 — ACCEPTED (owner, 2026-10-25): concurrency token

**Decision:** PostgreSQL `xmin` as an EF **shadow property** mapped in `OrderConfiguration` (for example `builder.Property<uint>("xmin").IsRowVersion()`); the Domain gets no version property. Verify the exact mapping API against the installed Npgsql 10.x and prove it with a two-context test. `DbUpdateConcurrencyException` maps to HTTP 409 for user-facing conflicts; no blind retry of a stale aggregate. **Fallback:** an app-managed integer version only through a new ADR if `xmin` proves unsuitable.

## ADR-012 — ACCEPTED (owner, 2026-10-25): event transport

**Decision:** `RabbitMQ.Client` used directly from Infrastructure (async API, publisher confirms, manual acks); no MassTransit, MediatR or other broker framework. Application defines the event contracts; Infrastructure implements the Outbox relay and consumers. Envelope: `MessageId`, `Type`, `Version`, `OccurredAtUtc`, `CorrelationId`, `AggregateId`, minimal non-PII payload, serialized with `System.Text.Json`.

## ADR-013 — ACCEPTED (owner, 2026-10-25): approved additions to the Order aggregate

**Context:** the Outbox (Phase 7) and timeout worker (Phase 9) need domain events, timestamps and a distinguishable domain exception, none of which the established Order has.
**Decision:** add exactly these, in Phase 1.5, tests first:
1. `DomainException` for state-machine and fence violations (replacing `InvalidOperationException`); argument violations stay `ArgumentException`. The API maps `DomainException` to 409; an `ArgumentException` reaching the API is a safety net mapped to a generic 400 and logged, because Application validation runs first and returns detailed 400s.
2. Aggregate-raised domain events (`OrderCreated`, `OrderAccepted`, `CourierAssigned`, `AssignmentReleased`, `OrderPickedUp`, `OrderDelivered`, `OrderCancelled`); release and cancel events carry the previous `CourierId` and `AssignmentId`; no PII.
3. Timestamps `CreatedAt`, `AcceptedAt`, `AssignedAt`, `PickedUpAt`, `DeliveredAt`, `CancelledAt`, with `DateTimeOffset now` supplied by the caller; Domain never reads the system clock.
**Unchanged:** transitions, cancellation matrix, `AssignmentId` fence, `OrderPricingLimits` semantics, item/duplicate/limit validation. Existing tests change only where the exception type expectation changes.
**Not included:** concurrency version (ADR-011, EF shadow property).
**Verification:** new unit tests per event, timestamp and exception path; full Domain suite rerun.

## ADR template

### ADR-014 — ACCEPTED: preserve the Domain monetary range in persistence

- Date: 2026-10-25; implementation compatibility choice, no business-rule changes.
- Context: the existing tested Domain admits decimal.MaxValue for a single item/order. InitialCreate numeric(18,2) cannot store those valid amounts. Restricting Domain limits would alter the established contract.
- Decision: preserve the full Domain monetary range using PostgreSQL numeric without forced scale and CHECK constraints for positivity, decimal.MaxValue and two-decimal precision. InitialCreate is unchanged. Alternative: restrict pricing configuration at Application/options boundary, which would add a narrower storage policy. No external data/price is trusted by this choice.
- Timestamp migration: add the six ADR-013 columns; CreatedAt is required with no fabricated default. Existing developer Orders count is 0 (read-only query). Upgrading a populated legacy database requires an owner-approved backfill and is deliberately not silently supported. No migration was applied to the developer DB.
- Sources verified 2026-10-25: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/managing and https://dotnet.testcontainers.org/modules/postgres/. Testcontainers.PostgreSql 4.16.0 pinned from NuGet; tests use real postgres:17, random ports/credentials and migrations.
- Verification: initial numeric(31,2) widening was tested and exposed an Npgsql 10.0.3 IndexOutOfRangeException reading decimal.MaxValue with forced scale 2. A subsequent additive migration removes forced scale and adds constraints; the same regression now passes. PostgreSQL numeric preserves supplied scale (https://www.postgresql.org/docs/17/datatype-numeric.html); no floating point or string money storage is used. Npgsql also rejects nonzero DateTimeOffset offsets, so EF converts supplied values to UTC without changing the instant (https://www.npgsql.org/doc/types/datetime.html). All six timestamp properties have this conversion.
- Executed proof: 234 Domain + 18 meaningful PostgreSQL tests + 1 original Integration placeholder pass. Tests verify all money boundaries, invalid direct test-DB writes, UTC offsets, full lifecycle, cancellation matrix, release/fencing and duplicate root insert rollback. Migration CLI applies all three migrations to a separate disposable container. Down migrations narrow/drop columns and must not be run on non-disposable data without explicit review.

### ADR-013 implementation notes — 2026-10-25

Required timestamp parameters were added to Create/transition APIs; existing test call sites supply a fixed instant. AssignedAt retains the last assignment time on release and is replaced on reassignment; AcceptedAt retains original acceptance time. No extra time-order guard was added because it would alter the approved transition rules. Pending events are immutable records exposed via a read-only list, explicitly ignored by EF. Verification: 234 Domain tests and green Release solution build. Persistence migration/outbox mapping are separate upcoming work, not claimed complete.

```markdown
### ADR-XXX — STATUS: [title]
- Date:
- Context and measurable problem:
- Alternatives considered:
- Decision:
- Why:
- Tradeoffs/risks:
- Implementation files:
- Tests proving behavior:
- Owner approval if required:
```
