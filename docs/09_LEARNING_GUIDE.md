# Learning guide and technical-review syllabus

## Implemented: ADR-013 domain additions (2026-10-25)

### Verified PostgreSQL behavior (Phase 2A)

### Verified DB conflict protection (ADR-011)

`Api/Errors/OrderFleetExceptionHandler.cs` translates persisted concurrency conflicts to HTTP 409 without replaying the stale command. DomainException also signals 409; ArgumentException is only a generic 400 safety net. Application validation will supply safe detailed request errors. HttpErrorMappingTests runs the production handler in real HTTP middleware, asserting status, ProblemDetails and absence of private exception messages. Unexpected exceptions remain 500. A mapping test is distinct from the PostgreSQL conflict tests and from future authorized order command endpoint tests.

OrderConfiguration maps a uint `xmin` shadow property as IsRowVersion. PostgreSQL supplies a new transaction version on update, and EF includes the previously loaded version in the UPDATE predicate. A zero-row update becomes DbUpdateConcurrencyException. Domain guards still protect only the aggregate in memory; OrderConcurrencyTests demonstrates why a stale aggregate with the old AssignmentId can pass its local guard but must fail at SaveChanges. The test uses separate contexts/DB sessions, including actual parallel SaveChangesAsync calls, rather than a process-local lock. The winner's persisted status, fence, cancellation reason, times and totals are checked in a third context. No stale mutation is blindly retried.

The generated xmin migration contains a rowVersion xid operation; the Npgsql generator treats this as mapping a PostgreSQL system column. It is not a new Domain member. Eleven real DB cases pass; API 409 mapping remains separate work.

`OrderPersistenceTests` uses `PostgresFixture` and Testcontainers.PostgreSql 4.16.0 to start a disposable postgres:17 with random credentials/host port. MigrateAsync applies actual migrations. Each test creates unique aggregates; reads and transitions use fresh contexts so the change tracker cannot fake rehydration. InitialCreate is preserved; timestamp and money corrections are additive migrations. The migration script independently verifies the CLI path against its own disposable container.

Money illustrates why a passing unit suite is insufficient: numeric(18,2) rejected valid Domain magnitudes; numeric(31,2) exposed a provider failure at decimal.MaxValue; numeric with checks preserves value and original scale. Database check tests deliberately attempt invalid writes only against their disposable database and assert rejection. UTC converters allow a caller's +03:00 instant to persist as offset 0 without changing time. PostgreSQL stores instants, not original offset labels, with microsecond timestamp precision.

EF uses the private `_items` field and owned rows with composite key/owner FK. Rehydration does not generate new Domain events. Events currently remain transient until Phase 7 adds transactional Outbox mapping. No persistence test or passing CLI command proves messaging, DB concurrency or HTTP security; those have separate gates. `dotnet test tests/OrderFleet.IntegrationTests -c Release` verified 18 real cases plus one historical placeholder at this checkpoint.

`src/OrderFleet.Domain/Orders/Order.cs` now takes `DateTimeOffset now` at creation and each transition. The caller owns the clock; tests can supply fixed instants without sleeping. Timestamp arguments are required so a caller cannot accidentally fall back to the machine clock. No new chronological validation is introduced, preserving the approved state machine. `AssignedAt` describes the most recent assignment; release preserves that historical timestamp and `AcceptedAt`, while clearing active identifiers.

`OrderDomainEvents.cs` contains immutable records with an event identity, order identity, resulting status, total and occurrence time. Assignment events also contain courier/fence identifiers; release/cancel retain the previous identifiers before the aggregate clears them. These facts allow later outbox consumers to release capacity or compensate payments without reconstructing lost state. Events contain no names, customer data or location. `DomainEvents` is read-only and `ClearDomainEvents` clears only pending facts. EF ignores this transient collection; no outbox delivery is implemented yet.

`DomainException.cs` distinguishes a legal-argument business conflict from a programming/argument error. Existing argument validation remains unchanged. `OrderDomainAdditionsTests` verifies every event/timestamp path; the existing exhaustive transition/cancellation/fence tests now compare pending events and timestamps after rejection. Run `dotnet test tests/OrderFleet.Domain.Tests -c Release` (234 passing at this checkpoint).

Review prompts: Why must release capture the old fence? Why does an in-memory event not prove reliable delivery? Who provides the clock? Why does a rejected command emit no event? Why is DB concurrency still needed after these domain guards? Each answer is demonstrated by the domain tests or remains an explicit later-phase obligation.

The owner intends to learn **why the implementation is designed this way** after the AI executes it. This is a **living explanation of actual code**, not a fictional tutorial. Update it every phase.

## For every feature, document

1. **What problem are we solving?** Real customer/business scenario and its invariant.
2. **Why this design?** Chosen option, 2+ considered alternatives where meaningful, tradeoffs and failure modes.
3. **Where is it in code?** Concrete file paths, class names, methods and public entry points.
4. **Read the flow:** HTTP request → validation/auth → Application use case → Domain behavior → transaction/DB → outbox/consumer → notification.
5. **What does each important abstraction do?** Its responsibility, collaborating objects and why it belongs in that layer.
6. **What could go wrong?** invalid state, stale DB reads, two workers, duplicate message, broker down, bad token, mis-sized decimal, etc.
7. **How do tests prove it?** Exact test class/name and behavior asserted; command to run it.
8. **What would change if requirements evolved?** e.g., new payment provider, more couriers, multi-region deployments, fresh pricing policy.
9. **What interview questions should owner answer?** 5–8 meaningful questions, with concise explanations and runnable demonstration prompts.

## Course outline (review in this order)

### Lesson 1: architecture and assembly references

- Why Modular Monolith versus Microservices?
- What is Clean Architecture and the real compile-time dependency graph?
- Difference between entity, aggregate, value object, owned EF relationship and physical DB table.
- Which project contains a business rule versus an HTTP policy versus an adapter?

### Lesson 2: Order Domain invariants

- Walk `Order.Create`, `OrderItem`, `OrderPricingLimits`, decimal multiplication boundary and persisted monetary snapshots.
- Trace accepted/assigned/picked-up/delivered/cancelled transitions; why `AssignmentId` is a fence.
- Understand why two tables `Orders` and `OrderItems` are normal with one aggregate.

### Lesson 3: EF Core and real PostgreSQL

- Mapping `OwnsMany`, backing field, shadow keys, FK and precision.
- Migration vs schema; tracking vs no-tracking; `SaveChanges`, transaction and explicit owned loading.
- Why EF InMemory cannot verify PostgreSQL behavior. Watch actual integration tests.

### Lesson 4: concurrency and idempotency

- Demonstrate two contexts and `DbUpdateConcurrencyException` or chosen equivalent.
- Show difference between optimistic update conflict and atomic courier claim.
- Demonstrate stale fence, idempotent API keys, DB uniqueness and conflict response.

### Lesson 5: identity and multi-actor authorization

- Authentication vs authorization, role vs ownership, JWT validation, 401 vs 403.
- Inspect how trusted actor identity flows and why no `CustomerId` or trusted `Price` from body.

### Lesson 6: event-driven reliability

- Show why `SaveChanges + Publish` can lose messages.
- Trace Outbox record → relay → RabbitMQ → Inbox → ACK and exactly where duplicate deliveries arise.
- Run crash/replay tests; understand at-least-once vs exactly-once business effect.

### Lesson 7: PostGIS and dispatch

- Geography units, SRID and `ST_DWithin`, candidate filtering.
- Active courier claim transaction, fairness/tie-breaking, two-dispatch-worker race.
- State recovery and timeout safety.

### Lesson 8: realtime, observability and deployment

- SignalR groups vs security, private order updates, ETA limitations.
- Logs, traces/metrics and health; what happens when RabbitMQ is unavailable.
- Compose and CI test bootstrapping; migration safety and secrets.

## Teaching artifact template

```markdown
### Feature / class / method
- Business purpose:
- Path in repo:
- Inputs (trusted vs untrusted):
- Preconditions/invariants:
- Step-by-step control flow:
- DB tables and transaction boundary:
- Concurrent/replay failure mode:
- Why design X, not alternative Y:
- Tests and proof command:
- Interview Q&A:
- Potential refactoring or limitations:
```

## Interview challenge prompts (sample; add real examples later)

1. Two HTTP requests create the same order with the same idempotency key simultaneously: what DB constraint protects the race?
2. Why is OrderItem owned even though it has a separate table?
3. What if EF Core rehydrates an aggregate with private setters? How prove it works?
4. Why is reading `Courier.IsAvailable` then saving assignment not safe under concurrency?
5. How does an `AssignmentId` prevent a stale worker from delivering a newly reassigned order?
6. What happens if RabbitMQ confirms a publish but Outbox mark-published fails?
7. Why does a consumer ACK after durable commit?
8. Does JWT role validation alone permit access to any order ID? Why not?
9. What does `ST_DWithin` interpret as meters vs projection units?
10. How can a successful Release build still hide a production bug?

**After implementation:** replace generic examples with the real classes, real ADRs, test commands and actual screenshots/console results if available. Do not invent test proof.
