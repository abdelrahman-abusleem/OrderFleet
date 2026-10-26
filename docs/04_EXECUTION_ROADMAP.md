# Detailed execution roadmap — OrderFleet

This is a **delivery plan**, not a claim of implemented functionality. Work in dependency order. Mark each phase **NOT_STARTED / IN_PROGRESS / BLOCKED / VERIFIED_COMPLETE** in `07_PROGRESS_AND_HANDOFF.md`. Each numbered slice must get explicit acceptance tests and review. Run real tests. Do not rewrite existing verified Domain phase. Before an ambiguous new business assumption, follow decision workflow in `08_DECISION_LOG.md`.

**Autonomous continuation rule:** after each phase gate passes, commit on the phase branch, write the phase report, and immediately proceed to the next phase without waiting for an explicit "go". Do not proceed past an unverified/failed gate as though it passed; record blockers and continue other independent safe work when possible.
**Always-presentable rule:** every phase ends with a green Release build and an up-to-date README status section, so the repository can be shown at any moment.
**Mandatory full scope:** Every phase from 0 through 12, including Phase 10 (SignalR and ETA) and the full OpenTelemetry tracing/metrics work in Phase 11, must be implemented and verified for final completion. Do not remove, skip or indefinitely defer a feature to save time. Scheduling within the roadmap may prioritize dependencies, but does not reduce the delivery scope. Any scope reduction requires a new explicit owner decision.

## Phase 0 — baseline audit (mandatory preflight)

**Tasks:** enumerate solution/projects and dependency graph, Git status and untracked work; audit packages/security issues, Docker/compose, existing migrations and actual schema, Domain contracts, existing integration tests. Run Release build/unit tests. Record test counts and failures, verified snapshot of implementation, migration state, blockers, risk register. **Gate:** factual current baseline + owner work preserved; clearly distinguish handoff claims from observed tests.

## Phase 1 — Domain (expected done; verify, do not reinvent)

**Established:** `Order` aggregate with Created/Accepted/Assigned/PickedUp/Delivered/Cancelled states, `OrderItem`, `OrderPricingLimits`, cancellation reason matrix, `AssignmentId` fence and guarded release/transition. Snapshot pricing; intrinsic item validations separated from configurable order caps.

**Tasks:** inspect implementations and tests; rerun existing test suite; record precise counts; preserve private backing field, owned items and pricing overflow fix. Only change if a demonstrated defect appears; add regression test first. Exception: the additions approved in ADR-013 (Phase 1.5). **Gate:** all existing passing business tests still pass; no state-machine regressions and no public mutable state.

## Phase 1.5 — Approved Order domain additions (ADR-013)

Owner-approved on 2026-10-25. These are the **only** permitted changes to the established Order domain. Existing transitions, cancellation matrix, `AssignmentId` fence, pricing limits and duplicate/limit validations stay exactly as they are.

1. **Tests first:** write failing tests for each addition below, then implement.
2. **`DomainException`:** introduce `DomainException` (initially `Orders/`; moved without behavior changes to shared `Common/` by the2026-10-26 hardening pass, ADR-027). State-machine and fence violations (`Accept`, `Assign`, `MarkPickedUp`, `Deliver`, `ReleaseAssignment`, `Cancel`, `EnsureCurrentAssignment`) throw it instead of `InvalidOperationException`. Argument-contract violations stay `ArgumentException`/`ArgumentOutOfRangeException`. Update only the existing test expectations that change because of this; never weaken or delete tests.
3. **Domain events:** the aggregate raises immutable events (`OrderCreated`, `OrderAccepted`, `CourierAssigned`, `AssignmentReleased`, `OrderPickedUp`, `OrderDelivered`, `OrderCancelled`), exposed as a read-only collection plus `ClearDomainEvents()`. `AssignmentReleased` and `OrderCancelled` carry the **previous `CourierId` and `AssignmentId`**, because the transition nulls them. Events carry identifiers, status, `TotalAmount` and timestamps only; no PII. EF does not persist them; the Application layer maps them to Outbox rows in Phase 7.
4. **Timestamps:** `CreatedAt`, `AcceptedAt`, `AssignedAt`, `PickedUpAt`, `DeliveredAt`, `CancelledAt` (`DateTimeOffset`, nullable except `CreatedAt`). `Create` and the transition methods receive `DateTimeOffset now` from the caller (Application uses a clock port); Domain never reads the system clock.
5. **Not in Domain:** the concurrency version. It is an EF shadow property (`xmin`, ADR-011) added in Phase 2B.
6. **Optional one-line change:** `Guid.CreateVersion7()` for new order ids and assignment ids.
7. Run the full Domain test suite and record counts.

**Gate:** every pre-existing business rule still covered and passing; new tests cover each event, timestamp and exception path; no EF/HTTP/RabbitMQ dependency added to Domain.

## Phase 2A — PostgreSQL persistence (FIRST INFRASTRUCTURE WORK)

1. Confirm `OrderFleetDbContext`, `OrderConfiguration`, EF Core 10/Npgsql compatible versions, `InitialCreate` migration and API DI. Never edit or regenerate `InitialCreate`; the Phase 1.5 columns arrive in a **new** migration.
2. Inspect generated SQL/constraints: PK/FK for `Orders`+`OrderItems`; owned collection with backing field; enum strings; nullable courier/assignment/cancellation data; string max length; decimal precision. Check `numeric(18,2)` range is consistent with configurable pricing limits and total; fix with explicit decisions/migration if mismatched.
3. Add genuine PostgreSQL Testcontainers fixture using actual migrations; test DB per test or fixture isolation, no dependence on local developer DB.
4. Tests: create order of multiple items, save in context A; load in B with items and exact totals/currency/state; accept, save, reload in C; assign, persist fence; release, stale fence, cancel; verify applicable combinations and nulls. Test DB constraints/duplicate cases as relevant.
5. Validate startup DI connection handling; secrets are not tracked. Test `dotnet ef database update` against disposable DB; never apply to unknown DB.
6. Review EF mapping for accidental insert/update setters, cascade deletes, direct-owned-child saves and query tracking patterns.
7. **CI and README skeleton (moved up from Phase 12):** add a minimal CI workflow (restore, Release build, Domain tests, and an integration-test job with Docker/Testcontainers) and a README skeleton (what the project is, how to run it, current phase status). Keep both updated at every phase end.

**Gate:** real migrations on disposable PostgreSQL; complete aggregate save/reload/transition proven by integration tests; no warning/precision surprises; Release build and full applicable tests pass. `Build succeeded` alone is not this gate. The CI workflow runs green.

## Phase 2B — optimistic concurrency

1. Implement ADR-011: `xmin` shadow row-version in `OrderConfiguration` (no Domain property). Verify against the installed Npgsql 10.x API and document migration impact.
2. Protect order transition updates; domain invariant rechecked after reload. Map expected `DbUpdateConcurrencyException` to application conflict (HTTP 409 for user-facing unresolvable conflict), not generic 500.
3. Add two-context test: both read same order, A updates, B attempts incompatible write and **must not silently overwrite** A.
4. Test accepting/canceling/assignment/release races, stale fencing after reassignment, and retry policy (no blind replay).
5. Check bulk `ExecuteUpdate`/raw SQL paths preserve concurrency constraints; explicitly handle if used.

**Gate:** deterministic DB-backed conflict tests, correct API mapping and no lost updates.

## Phase 3 — Identity, credentials, authorization

1. Model users/roles (Customer, RestaurantStaff/Owner, Courier, Admin) and membership/ownership links with unique indexes; choose ASP.NET Core Identity or vetted equivalent, avoid homemade password hashing.
2. Implement registration/login only as required for demo, JWT issuer/audience/signature/expiration and secure configuration. Decide refresh-token scope; default not required to demo if unneeded.
3. Build per-module authorization policies + resource ownership queries; guard endpoints and SignalR later. Define trusted principal ID extraction.
4. Add tests for no token, invalid signature/issuer/audience/expiry, wrong role, cross-restaurant and cross-customer; admin path audited.
5. Prevent enumeration and sensitive errors, rate-limit credential endpoints where appropriate.

**Gate:** actual API tests demonstrate 401/403 and ownership isolation; secrets are not in repo/logs.

## Phase 4 — restaurant/catalog + ordering HTTP use cases

1. Create restaurant and product tables/config with ownership, validity/availability, currency SAR, reasonable max lengths/price precision and indexes. Catalog changes should not alter historic orders.
2. Implement authorized catalog create/update/deactivate/list, restaurant availability handling and input validation; add OpenAPI docs.
3. Implement create order application use case: authenticated identity, restaurant identity, product ownership, active products/restaurant, trusted DB prices, `OrderPricingLimits`, full totals, Domain factory, one transaction + Outbox when messaging is enabled.
4. Implement Create/Get/List/Accept/Reject/Cancel endpoints with thin handlers and consistent ProblemDetails. Use idempotency keys for safe retried order create.
5. Concurrency/transaction tests: price changes at request boundary, missing/unavailable products, duplicate items, invalid cross-restaurant product, unauthorized restaurant user, post-creation catalog price change, duplicate submission.
6. Decide restaurant acceptance TTL and cancellation compensation contracts explicitly.

**Gate:** HTTP-level happy and negative path with real PostgreSQL, current catalog price always trusted and original snapshots preserved.

## Phase 5 — mock payments and policy integration

1. Implement ADR-008 (already decided). Add a new ADR only for details it leaves open (for example capture-failure handling edge cases) and keep payment state distinct from established `OrderStatus`.
2. Model payment intent/attempt and idempotency keys; DB uniqueness on `(Provider, ExternalReference)` and/or `(Actor, Operation, Key)` as applicable.
3. Build deterministic mock provider with configurable authorized/declined/transient outcomes. No production charge, no real secret needed.
4. Safe authorize/capture/refund operations; compute amounts from order snapshots; enforce state transitions exactly once at business-effect level.
5. Tests for declined, delayed/replayed callback, concurrent capture, duplicate refund, cancellation timing, API retry and restart after DB commit.

**Gate:** no double debit/capture/refund; order/payment transitions follow documented matrix; real DB constraints verified.

## Phase 6 — couriers and spatial functionality

1. Model courier identity/availability and latest valid location with accurate observed timestamp; define offline/reserved/busy and immutable audit where useful.
2. Add PostGIS extension via a **compatible tested database image/migration** and indexes. Treat current `postgres:17` image as lacking guaranteed extension.
3. Use `geography(Point,4326)` and `ST_DWithin` radius in meters if selected; indexing and query plan sanity checks. Make SRID and axis order explicit.
4. Implement courier availability and location API; updates must be authorized, ordered and reject invalid coordinates/time skew/stale writes.
5. Nearby-courier query: bounded radius, freshness, availability, filtering and deterministic ranking; reasonable pagination/limits.
6. Tests against real PostGIS including radius boundary, invalid points, stale location and restaurant/delivery distances.

**Gate:** spatial query correctness + index deployed + secured position updates and no stale overwrite.

## Phase 7 — transactional Outbox + RabbitMQ + Inbox

1. Finalize versioned event names, producer/consumer matrix and schema. Record failure contract.
2. Add outbox table with message IDs, payload/version, timestamps and safe claim/lease/attempt handling; write domain state and event in **same DB commit**.
3. Add RabbitMQ exchange/queue topology and durable contracts as needed, publisher confirms, bounded retry/backoff and poison/DLQ handling.
4. Consumer: manual acknowledgement **after durable idempotent processing**, inbox uniqueness and transactionally committed effects.
5. Handle concurrency in polling workers and stranded leases; avoid publication flood/unbounded retries; verify duplicate/out-of-order behavior.
6. Real PostgreSQL + RabbitMQ Testcontainers tests: crash before commit (no event), crash after commit before publish, duplicate publish, crash after consume commit before ACK, consumer restart, poison event.

**Gate:** no fabricated exactly-once guarantee; eventual delivery under documented healthy/retry assumptions and no duplicate business effect.

## Phase 8 — race-safe dispatch

1. Define candidate policy (geo availability, freshness, priority, tie-break, order/restaurant delivery constraints).
2. Implement atomic DB claim with `UPDATE...WHERE state = available`/row lock plus appropriate constraints, transactionally coupled with order assignment and `AssignmentId` creation. Explicit collision/retry policies.
3. Ensure active order and courier capacity constraints at DB boundary; record dispatch attempt with idempotent keys and attempts/status.
4. Handle ReleaseAssignment and reassign races; fence stale worker, courier and delayed message actions.
5. Run **parallel integration tests** with multiple workers/contexts: one courier two orders, one order two workers, races with cancel/pickup/release, stale assignment ID.

**Gate:** no duplicate incompatible active courier claims, no lost order state, no stale worker success. Test using actual concurrent database operations (not only locks inside one process).

## Phase 9 — timers, retries and crash recovery

1. Define UTC clock abstraction and persisted deadlines/attempt counters/status; no correctness dependence on `Task.Delay` or in-memory timer alone.
2. Acceptance-timeout cancellation using existing `CancellationReason.AcceptanceTimedOut`, only if order still eligible; coordinate payment compensation based on ADR.
3. Dispatch timeout/reassignment, bounded exponential backoff with jitter, dead-letter/operator visibility and idempotent wakeup.
4. Recover abandoned claims/outbox leases after crash, restart; avoid double release after new assignment.
5. Tests simulate clock advances, duplicate timeout workers, retry threshold, crash/restart and overlapping cancel/assign.

**Gate:** deterministic recovery and timeout integration tests survive restart and conflicting actions.

## Phase 10 — SignalR realtime and ETA (REQUIRED FOR FINAL COMPLETION)

1. Define private groups per authorized actor/order and server-only group membership; use proper JWT configuration.
2. Deliver status/location updates from persisted event processing; address out-of-order event sequence and reconnect/read-model refresh.
3. Implement ETA calculator with clear assumption: distance ÷ configurable average speed (+ buffer), freshness fallback; never market as road-accurate navigation.
4. Protect precise courier location and ordering of updates; coalesce/throttle high-frequency notifications where needed.
5. Tests: authorized party receives event; unrelated user cannot subscribe; expired/revoked role handling by documented reconnect/revalidation policy; missing/outdated location yields fallback.

**Gate:** real integration verification of hub auth and event flow, no cross-user leakage.

## Phase 11 — observability/ops/hardening (Serilog, health checks, options validation, and OpenTelemetry tracing/metrics are ALL required)

1. Structured safe Serilog logs, correlation IDs with `traceparent`, trace spans for HTTP/DB/outbox/dispatch/consumer, basic metrics (outbox lag, retries, dispatch outcome/time, errors).
2. Configure OpenTelemetry exporter with local/dev fallback; do not require paid cloud service.
3. Health checks: liveness, readiness and broker/DB checks with timeout; safe behavior on transient broker outage.
4. Validate critical Options on startup and environment variables; redacted logs; sane SQL/API and message retries.
5. Harden Compose, PostGIS-enabled DB, secrets/examples, resource limits, initialization, structured logs and intentional migrations.

**Gate:** observability actually emits required data locally, safety/privacy review and failure-injection tests pass.

## Phase 12 — delivery, CI, portfolio documentation

1. Reproducible bootstrap from clean checkout: prerequisites, Docker, secrets example, migrations, seed, build/test and first API call.
2. Extend the Phase 2A CI: restore/build/format/analyzers/unit and integration tests with Docker available or clearly separated jobs. Testcontainers feasibility on runner established.
3. API contract docs/OpenAPI with complete calls and response examples, token demo instructions and reset procedures.
4. Architecture diagrams and ADRs, sequence flows, data model, concurrency and failure-mode explanation; expose explicit known limitations.
5. Automated critical E2E demo: create from catalog → accept → mock payment → assign courier → pickup → deliver → event/DB verification, plus cross-user 403, duplicate idempotency, concurrent dispatch and worker restart.
6. Human review and repository cleanup; no test placeholders masquerading as coverage, no secrets, no unsupported claims.

**Gate:** clean-environment build and demo works with recorded commands; review reports, README and handoff accurate.

## Global checkpoints

At **every** slice: acceptance criteria first, safe minimal change, test, self-review, correction, retest, docs and verified evidence. Block phase promotion when P0/P1 defect, material security hole or critical integration test failure exists. If environment prevents execution, document exact blockers and continue only independent safe work. No arbitrary declaration of success.
