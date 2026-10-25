# OrderFleet — target architecture and technical design

## Style

**Modular Monolith + Clean Architecture + DDD tactical modeling where beneficial.** One deployable backend and separately hosted worker only if operationally warranted; logically separated modules. Avoid forced microservices and excess C# projects. Preserve existing solution layout.

```text
HTTP / WebSockets / background triggers
                │
       OrderFleet.Api (composition, endpoints, auth, SignalR)
                │
       OrderFleet.Application (use cases, input ports, policies)
                │
       OrderFleet.Domain (aggregates, entities, state invariants)
                ▲
 OrderFleet.Infrastructure (adapters: EF/Npgsql, PostGIS, broker, workers)
                │
      PostgreSQL / RabbitMQ / time / fake payments
```

This depicts responsibility, **not** a compile-time arrow from Domain to Infrastructure. Domain references no external layers. Application may reference Domain. Infrastructure references Application/Domain; Api references Application and infrastructure for DI composition. Existing domain-focused tests reference Domain; integration tests reference infrastructure/API as appropriate.

## Projects and responsibilities

- `OrderFleet.Domain`: `Orders/` aggregate, immutable policies, domain errors/events, restaurant/courier/payment domain models when justified; **zero** EF, ASP.NET Core, RabbitMQ, DI framework or HTTP status code dependencies.
- `OrderFleet.Application`: commands/use cases and results, interfaces needed for external services, per-actor authorization policies, transaction orchestration, trusted price reads, dispatch coordination, event recording coordination. Separate application DTOs from HTTP request models where useful; avoid over-layering.
- `OrderFleet.Infrastructure`: `Persistence/` EF Core DbContext and mappings/migrations, PostGIS, outbox/inbox repository implementations, RabbitMQ publisher/consumer, fake payment provider, clock and worker services. No controller/business-policy duplication.
- `OrderFleet.Api`: thin Minimal API/endpoint group handlers, authentication/authorization middleware, ProblemDetails, OpenAPI, SignalR Hubs, options/DI composition. APIs should not directly set Domain properties.
- `tests/OrderFleet.Domain.Tests`: fast deterministic domain tests.
- `tests/OrderFleet.IntegrationTests`: PostgreSQL/RabbitMQ/HTTP tests against real disposable dependencies; independent isolated schemas/databases to avoid collision.

## Module logical layout (example, flexible)

```text
Domain/Orders, Domain/Restaurants, Domain/Payments, Domain/Couriers, Domain/Dispatch
Application/Orders/CreateOrder, AcceptOrder, CancelOrder
Application/Restaurants, /Payments, /Couriers, /Dispatch
Infrastructure/Persistence/Configurations, Migrations, /Messaging, /Geo, /Payments, /Workers
Api/Endpoints/Orders, Restaurants, Couriers, Payments; /Hubs; /Configuration
```

**Do not reorganize existing healthy files solely for visual uniformity.** Modular boundaries matter more than matching a speculative folder tree.

## Identity and data ownership

- `Identity`: user/account/role identity records and grants.
- `Restaurants/Catalog`: product availability and **current authoritative** prices, not order financial snapshots.
- `Orders`: immutable order-item price/name snapshots, status, cancellation reasons, assignment fences, order total.
- `Payments`: payment intent/transaction identifiers, authorization/capture/refund and idempotency status.
- `Couriers`: latest position, assignment availability/capacity; dispatch owns assignment workflow.
- `Dispatch`: selection attempts and lifecycle/retries; transactionally coordinates courier reservation and order assignment.
- `Messaging`: Outbox/Inbox as technical tables, not the authority for domain state.

Cross-module coordination goes through explicit Application calls/events and documented DB transaction boundaries. A modular monolith shares a PostgreSQL database but must not equate DB access with permission to bypass another module's business rules.

## Order aggregate and persistence

- `Order` is **one business consistency boundary** containing `OrderItem` values/owned children. This **does not mean one physical table**: `Orders` and `OrderItems` remain separate tables with a FK.
- `Order` guards state transitions; only the root exposes relevant operations; items are privately backed and read-only to consumers.
- `OrderItem` enforces intrinsic constraints. `OrderPricingLimits` and Order enforce policy caps and aggregate invariants. Never duplicate the same validation blindly.
- Money: C# `decimal` for SAR, consistent precision and explicit rounding strategy; DB `numeric(18,2)` or another intentionally selected compatible schema bound. **Guard persistence overflow/precision mismatch** even if in-memory decimal fits; validate configuration values against column limits.
- On rehydration, EF can use private constructor and backing field; real database tests must verify state and owned items.
- `Order.TotalAmount` and order-item totals are persisted snapshots, never recalculated using current catalog prices upon reload.

## Concurrency and transaction design

- For Order writes, use the `xmin` shadow row-version mapped in `OrderConfiguration` (ADR-011, no Domain property), verify compatibility with installed Npgsql 10.x; no automatic SQL Server rowversion assumption. Two contexts load same version, one saves, second update conflicts; translate to an appropriate API 409 or controlled retry based on operation semantics.
- Do not accidentally retry a Domain mutation on a stale aggregate without reloading and revalidating.
- Courier reservation is a **DB atomic conditional update/locking + constraints** (where justified), not only a `CanAssign` check. A multi-entity dispatch may require transaction spanning courier claim, order assignment, assignment record and outbox row. Minimize lock durations, define consistent lock ordering and recoverable failures.
- `AssignmentId` fences late workers and courier commands. A fence must be checked **at durable write boundary** where stale context updates may otherwise overwrite newer assignment.
- EF Core `SaveChanges` typically wraps multiple statements in a transaction; explicit transactions when coordinating more than one SaveChanges/query/update or custom atomic operations. Do not hold DB transaction open across remote RabbitMQ/network calls.
- Use PostgreSQL uniqueness/partial indexes/check constraints to reinforce invariants (e.g. at most one active courier assignment, scoped idempotency keys); constraints complement, not replace, Domain validation.

## Domain events, Outbox and Inbox

```text
Application command -> Domain transition + Outbox event (same PostgreSQL transaction)
       -> commit -> Background outbox relay -> RabbitMQ publish + publisher confirm
       -> consumer -> Inbox dedupe + business side effects in durable transaction
       -> consumer ACK after successful commit
```

- Outbox candidate columns: `Id`, `EventType`, `SchemaVersion`, `OccurredAtUtc`, `AggregateId`, `Payload`, `CorrelationId`, `Attempts`, `NextAttemptAtUtc`, `PublishedAtUtc`, `ClaimedUntilUtc` (adjust schema intentionally). Define safe concurrent claiming, no unbounded polling, backoff/jitter and poison handling.
- Dedupe key: event `MessageId` plus consumer name or handler identity. Commit dedupe marker and side effect atomically; duplicate delivery becomes safe no-op.
- Publisher confirm only proves broker-side handling, **not consumer execution**. Outbox relay may publish twice after crash; that is expected and requires idempotency.
- Maintain event compatibility with schema versioning; avoid serializing mutable domain entity graphs into public event contracts.

## Spatial and ETA

- Storage coordinates: prefer PostGIS `geography(Point,4326)` if querying meters over Earth; otherwise document geometry projection/units. Include GiST index and use indexed filtering (`ST_DWithin`) before expensive distance ordering.
- Validate coordinate ranges, order of latitude/longitude, location timestamp freshness, capability/availability, and maximum search radius. Beware telemetry-driven write load: keep latest location and optional bounded history with retention policy.
- ETA is a transparent heuristic, not external navigation. Configurable speed assumptions, last-update staleness, clamp/fallback, unit tests. Define what happens when no location is available.

## API/realtime contracts

- Resource-oriented versioned API (`/api/v1/...` unless repository already settled on a convention).
- Use ProblemDetails consistent error codes and correlation IDs; explicitly map validation (400), auth (401/403), not found (404), conflict (409), idempotency misuse (409), temporary dependency unavailability (503); never reveal stack traces.
- SignalR access: authorize connection + per-method + order/group membership, recheck relevant ownership where needed; keep group names unguessable or treat them as non-security boundaries. Never use a public event to leak location or payment details.
- OpenAPI documents realistic examples, 401/403/409, and auth scheme.

## Operational composition

- Docker Compose locally: database **with PostGIS extension installed and enabled**, RabbitMQ, API, worker (if separate), optional tracing collector. Existing `postgres:17` image does NOT automatically imply PostGIS exists: intentionally migrate to a compatible tested PostGIS-capable image or add extension through a justified build; do not wipe the existing data volume.
- Use development User Secrets/env configuration; strongly validated options, safe defaults, no credentials in tracked files. Migrations run deliberately, not destructive auto-migrate on every production startup.
- Readiness checks verify meaningful dependencies with suitable timeouts; liveness avoids cascading restarts when an external service is briefly unhealthy.

## Implemented runtime navigation (2026-10-26)

The concrete boundaries, complete order sequence, logical data model and failure-mode matrix are in [13_DELIVERY_GUIDE.md](13_DELIVERY_GUIDE.md). Actual source retains the four existing projects; no extra service/project layer was introduced. ArchitectureTests verifies Domain/Application assembly references exclude infrastructure/transport. API composition and Development bootstrap may reference infrastructure; HTTP business handlers delegate to Application. DB technical claims/leases and projections live in Infrastructure; Domain transition methods remain the authority.
