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

### ADR-016 — ACCEPTED: atomic order creation and scoped replay

- Date: 2026-10-25; implementation of approved ADR-015 and existing reliability requirements.
- Create takes restaurantId, productId/quantity rows and required private destination coordinates. CustomerId comes from verified claims; no supplied price/name/status/assignment is accepted. Domain creates snapshots from authoritative catalog data.
- Read catalog in one bounded query, then revalidate restaurant activity, product ownership/availability/name/price under PostgreSQL FOR SHARE locks in stable restaurant/product order before committing. A catalog change between pricing and commit produces a conflict and no orphan order/delivery/request/audit rows. Existing catalog/order writes use xmin; no in-process lock is treated as a DB guarantee.
- Store Order, owned items, private linked delivery information, UTC acceptance deadline, request fingerprint and audit in one transaction. The deadline is frozen at creation (CreatedAt + configured timeout, default 5 minutes); changes to future configuration do not extend existing orders. Application rejects acceptance at/after that persisted deadline even without a timeout worker. Reliable cancellation/compensation worker remains Phase 9 work.
- A dedicated order-create request table gives operation scope implicitly; key = CustomerId + Idempotency-Key (1..100 characters, no controls). Fingerprint is SHA256 over restaurant, normalized numeric coordinates and sorted product IDs/quantities. Same key/request returns the same order ID and 201; changed request returns 409. DB uniqueness arbitrates concurrent first requests and rollback prevents extra orders. No raw destination is stored in the fingerprint or emitted publicly.
- Request records currently have no expiry (conservative dedupe for the demo); operational retention must preserve documented replay semantics. No cleanup/deletion is implied by this phase. Payment keys/records have their own operation scope in Phase 5.
- Authorized delivery details use a separate resource endpoint and live ownership checks. Public catalog includes only merchant pickup locations; no customer destination. Mutating order actor/action audit commits with the transition. New DB constraints reinforce coordinates, product ownership, membership FK and unique ProductId per order.
- Alternatives: trust client price (rejected); check catalog only before commit window (race); process-local dedupe/lock (not durable); allow timeout configuration changes to extend old orders (breaks persisted deadline contract).
- OpenAPI uses installed Microsoft.OpenApi 2.12.0/.NET 10 transformers: bearer scheme, anonymous-operation overrides, 400/401/403/409 descriptions and required Idempotency-Key header. Source verified: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/customize-openapi?view=aspnetcore-10.0.
- Known phase boundary: current create/accept financial coupling is added in Phase 5 per ADR-008; dispatch is not present and must require successful capture. Domain events are not claimed durably transported until Phase 7.

### ADR-015 — ACCEPTED (owner, 2026-10-25): delivery information, availability and persisted deadlines

- Customer supplies required delivery latitude/longitude on create. Validate finite range (latitude -90..90, longitude -180..180), persist linked private order delivery information. No precise destination in public responses, logs/traces or unrestricted events. Authorized participant access only. This does not add unapproved members to the established Order aggregate; delivery information has a separate persistence model linked by OrderId.
- Restaurant has an active flag and product has availability; only active restaurants/available products can create new orders. No opening-hours/zone engine in demo. PostGIS discovery remains mandatory.
- Acceptance timeout defaults to 5 minutes and is configurable. Deadline derives from Order.CreatedAt in UTC and is persisted for restart recovery. Acceptance must reject elapsed deadlines even if the worker has not run; timeout/acceptance race uses DB concurrency and established AcceptanceTimedOut cancellation, idempotent processing and ADR-008 compensation.
- Dispatch is permitted only after successful capture; capture still occurs during acceptance as ADR-008 requires. Duplicate callbacks/captures/concurrent dispatch must remain safe. No new OrderStatus/reason.
- Tradeoffs: approximate customer-supplied destination, simple availability and explicit privacy rather than a zone/address engine. Deadline and payment readiness belong in Application orchestration/persistence; Domain state machine stays intact. Tests will cover missing/invalid coordinates, cross-user privacy, expired accept, races and restart recovery.
- Approval: explicit owner reply to the policy question, including mandatory implementation details. These clarify scope without removing any roadmap feature.

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

### ADR-017 — ACCEPTED (owner, 2026-10-25): durable mock payment and capture retry contract

- ADR-008 sequence remains authorize at create, capture before acceptance, void authorized / refund captured on cancellation. Payment has its own state; established Order states and reasons are unchanged.
- The mock provider uses separate PostgreSQL transactions and durable operation records to model an external financial system. No real charges or gateway credentials. Authorization scope is actor + request key, verified against request fingerprint, amount and SAR; capture/settlement have stable server-derived operation keys. Row locks and unique operation constraints prevent duplicate effects across processes and host restarts.
- Authorization decline returns 402 and no Order. Capture permanent decline returns 409 and replays the persisted declined operation even after mock configuration changes. Confirmed transient capture failure returns 503, records Failed + retryable attempt, and may retry the same capture operation before the original deadline. Transient failures do not create a final provider operation result.
- Unknown capture outcome returns 503 and records PendingReconciliation, never a confirmed failure. Mock Unknown commits capture then loses the response. Retry queries the same provider operation and obtains its actual result, avoiding a second charge. PaymentCaptureAttempts persist classification, retryability, reconciliation requirement and stable capture key.
- Acceptance checks the persisted deadline before and after provider capture. A late success persists the financial fact but leaves Order Created, for established timeout cancellation and refund. No dispatch until captured and accepted. A concurrent cancellation/acceptance conflicts on xmin/event sequence; financial compensation reads the provider's actual state, including a capture committed before a local crash.
- Order, payment, delivery, request dedupe, audit and versioned Outbox facts commit atomically. Order event sequence is incremented under root optimistic concurrency and protected by a filtered unique index. An event insert may detect a stale sequence before EF updates xmin; this is mapped to the same 409 optimistic conflict with rollback.
- Cancellation consumer commits payment settlement, financial Outbox and Inbox together. Provider effects remain idempotent if local commit/ACK is lost. Real RabbitMQ transport is mandatory Phase 7; reliable timeout/orphan-authorization recovery is mandatory Phase 9, not claimed implemented here. Unknown commit failures do not trigger blind void; confirmed catalog conflicts release an unattached authorization under a DB lock.
- Alternatives rejected: process-local financial ledgers, permanently caching transient failures, retrying an ambiguous charge with a new key, accepting after late capture, publishing directly before DB commit. Retention currently conservative/no expiry; no cleanup of financial keys without a documented retention policy.
- Owner explicitly approved permanent/transient/unknown distinctions, concurrency/deadline safeguards and testing. Verification: PaymentTests, PaymentWorkflowTests and existing catalog races on real PostgreSQL; restart replay, same-key retry, lost response, crash gap, deadline boundary and duplicate refund covered. No external callback endpoint is invented for this synchronous mock; delayed/replayed provider results use the durable provider operation contract.

### ADR-018 — ACCEPTED implementation policy: courier observations and PostGIS discovery

- ADR-009 operational states are Offline / Available / Reserved / Busy. Courier profile identity equals the authenticated Identity user; registration cannot choose another identity. Only Available can reserve; active reservations cannot be overwritten by availability. Order and AssignmentId fence reserve/busy/release operations. Actual transactional dispatch is Phase 8.
- Courier location is validated decimal latitude/longitude in Domain, with strictly increasing observed UTC time. Application rejects missing observations, older than configurable 120 seconds, or more than configurable 15 seconds in the future. Incoming observations normalize to database microsecond precision before comparison, preventing a sub-microsecond replay from appearing newer after reload. xmin and atomic profile/spatial-position persistence prevent stale writers overwriting either representation.
- Infrastructure-only NTS Point stores X=longitude, Y=latitude, SRID4326 in geography(Point,4326). GiST index supports ST_DWithin using spheroid and meters. Freshness and Available filters apply before bounded ranking by distance then courier ID. Radius default5000/max20000 meters (config bounded100..50000), results default20/max50. No global courier list or precise location responses.
- Nearby endpoint is restaurant-scoped, live membership checked for owner/staff, or explicit Admin. Couriers can modify/read only their own profile; role alone does not grant restaurant discovery. Future dispatch uses this spatial port internally and independently rechecks eligibility under a DB claim.
- New schema requires a PostGIS-capable image. Tests and migration audit now use postgis/postgis:17-3.5 (tested image digest sha256:01a6a70e41e6c4467c8f55f6063555ed72db2d6662cd0d571040d42eadaeb6f6). Existing postgres:17 developer service/volume remains untouched. Compose adds an opt-in spatial profile, separate PostGIS volume and loopback5434; no automatic upgrade or reuse of legacy data directory. New schema will not migrate on plain PostgreSQL without installed PostGIS.
- Sources: https://www.npgsql.org/efcore/mapping/nts.html, https://postgis.net/docs/ST_DWithin.html, https://github.com/postgis/docker-postgis. Alternatives: planar degrees or client-side NTS distance (wrong units), replacing existing volume (unsafe), timestamps based on arrival only (stale overwrite).
- Verification: real geography dateline (~222.64m), radius inside/outside and inclusive zero-distance boundary, freshness/state/authorization, missing/invalid/stale/future/duplicate observations, sub-microsecond replay, independent-context stale save rollback, SRID/axis assertions, installed extension/index and EXPLAIN index capability. Tiny test table query plan uses enable_seqscan=off only in its test connection to check index eligibility; no production planner forcing or scale/performance claim.
