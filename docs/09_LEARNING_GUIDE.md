# Learning guide and technical-review syllabus

## Implemented: trusted catalog and transactional orders (Phase 4)

`Domain/Restaurants` owns intrinsic names/prices/activity; `Domain/Geo/Coordinates` validates finite decimal latitude/longitude ranges. `ManageCatalog` checks actor role and restaurant grant before mutation; its price cap comes from the same configured pricing policy used by orders. Catalog DTOs expose public merchant information only.

Read the creation flow: `OrderWriteEndpoints` binds product IDs/quantities/destination and a header → `Application/Orders/CreateOrder` derives customer from Actor, validates bounds and canonical fingerprint → reads authoritative catalog → calls existing `Order.Create` → `EfOrderingStore.CommitNewAsync` revalidates prices/activity/ownership under DB share locks → commits Order/items/private delivery/deadline/request/audit atomically. A changed catalog returns conflict without orphan rows. This avoids a price-change gap while retaining the original order snapshot after catalog edits.

Idempotency uses a unique actor/key in a dedicated create-operation table. Two requests may pass the first read; only one transaction can commit the key. The loser rolls back and reads the winner, comparing fingerprints before replaying. Different request with same key conflicts. No process-local lock or cache is treated as durable dedupe. SHA256 stores no raw destination in the fingerprint. Records currently do not expire; changing retention later requires explicit replay semantics.

`OrderDeliveryInformation` stores private coordinates and the frozen deadline. A separate authorized delivery endpoint exposes it only to participants; regular order/public catalog responses do not. `OrderCommands.AcceptAsync` checks the persisted deadline before calling Domain.Accept; a delayed worker cannot make an expired acceptance legal. `OrderAuditEntries` records actor/action in the same save transaction, so a lost xmin race creates no false audit success. The reliable timeout/compensation worker is still Phase 9 work.

`CatalogOrderingApiTests` tests actual HTTP + PostgreSQL transactions, parallel requests and host restart. `CatalogChangesBetweenPricingAndCommit_RejectStaleSnapshotWithoutOrphans` directly exercises the real adapter with a stale trusted snapshot, not a mock of business behavior. OpenAPI transformer tests verify bearer/header contracts. Full phase gate: 245 Domain + 70 Integration pass. New mutations still need the Phase 5 financial coupling and Phase 7 Outbox before the complete journey is ready.

Questions to demonstrate: Why must the price be checked again before commit? What does FOR SHARE protect? Why does a unique idempotency key need a fingerprint? Why must audit roll back with a conflict? Why is a persisted deadline stronger than a timer? Why does changing timeout configuration not extend an existing order? How is private destination access different from public restaurant location?

## Implemented: Identity and resource authorization (Phase 3)

`Application/Identity/IdentityContracts.cs` defines the credential port and actor/roles. Infrastructure's `IdentityAccounts` adapts ASP.NET Core Identity: no custom password cryptography; account + Customer grant commit together. Identity table constraints arbitrate concurrent registration. The public API never accepts a requested privileged role, and registration does not reveal duplicate accounts. Login error titles are uniform. Lockout and per-IP throttling address different attack paths.

`Api/Identity` issues short-lived JWTs and validates signature, algorithm, issuer, audience, lifetime and Guid subject. Signing material comes from configuration and is startup-validated; tests generate their own keys. No refresh/revocation infrastructure is implied. Membership is rechecked in the database on each resource read, while an already-issued role claim persists until token expiry.

`Application/Orders/GetOrder.cs` owns resource-access decisions. `EfOrderReadStore` reads real data; `OrderReadEndpoints` derives Actor from authenticated claims and emits only the authorized response. A valid role alone is insufficient. Customer must match order.CustomerId, restaurant users need a grant, courier must match the current assignment, and admin access is explicitly audited. Foreign and nonexistent resources both return 404 to prevent resource enumeration.

`IdentityApiTests` exercises real JWT middleware and PostgreSQL Identity stores. It verifies hashing/login, duplicate account races, privilege injection, invalid claims/times/keys, ownership, revoked membership, lockout and throttling. `ApiFactory` uses per-run test keys and the fixture connection only. Run `dotnet test tests/OrderFleet.IntegrationTests -c Release --filter FullyQualifiedName~IdentityApiTests` (17 passing at phase gate). Full suite: 234 Domain + 51 Integration. No fake authentication handler substitutes for signature validation.

Review prompts: How does role differ from ownership? Why does a unique DB index matter after validation? Which updates share the registration transaction? Why can an old JWT retain a role after revocation? Why is membership checked again? What response avoids revealing foreign order existence? How do hash, lockout and throttling protect different failure modes?

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

### Phase 5 — payment result versus order state

Follow CreateOrder → DurableMockPaymentProvider → EfOrderingStore: an external authorization can outlive a failed local transaction. CaptureOrderPayment records confirmed failure separately from an uncertain result; provider operation identity survives retries. OrderCommands checks the original deadline again after capture, since a slow successful payment cannot extend restaurant acceptance. SettleCancelledPayment uses the provider's actual state to refund a capture even if the local process crashed before recording it. Provider financial effects and local Inbox/Outbox commits are separate failure boundaries, so retries are expected.

PaymentWorkflowTests uses real PostgreSQL and a new host with the same signing key to prove durable replay. The timing decorator advances the test clock after the real provider commits; it does not emulate financial behavior. Ask: why can an Outbox unique index report a concurrency conflict before xmin? EF may insert dependent facts before issuing the root update; both belong to one rollback transaction.

### Phase 6 — time and distance have storage semantics

Courier owns state and ordered coordinates without knowing NTS or EF. ManageCourier checks the observation window and normalizes microsecond precision; EfCourierStore commits Domain state with a PostGIS projection in one transaction. A stale context cannot update either one after the root xmin changes. NTS Point X means longitude. Geography and server-side distance supply meters; a client-side Point.Distance would instead operate on degrees. CourierSpatialTests proves this across longitude ±180 and checks GiST availability using EXPLAIN. Reserved/Busy guard capacity in Domain; concurrent dispatch still needs the Phase 8 transaction and DB constraints.

### Phase 7 — two independent acknowledgements

Trace EfOrderingStore → OutboxMessage → PostgresOutboxStore → OutboxRelay → RabbitEventPublisher. Publisher confirms mean the broker accepted a routed durable message; they do not commit the Outbox marker. Then trace RabbitConsumer → RabbitDeliveryProcessor → CancelledPaymentEventHandler → SettleCancelledPayment: the Inbox marker and local financial state commit before BasicAck. RabbitDeliveryTests deliberately omits each acknowledgement in turn, closes the actual channel, and proves one financial effect despite replay.

A lease token is separate from an assignment token. Lease expiry permits another relay to publish; the old relay cannot mark that claim complete. Duplication is safe only because consumers dedupe. Quorum retry queues preserve failed work while their dead-letter target is unavailable; a bounded dead queue still needs an operator to diagnose and replay safely. Tests demonstrate single-node durability and recovery, not cluster availability or unlimited retention.

### Phase 8 — capacity is a transaction, fencing is a different guarantee

Follow DispatchEventHandler → DispatchOrder → EfDispatchStore. The DB locks Order first, checks captured money, then selects and locks one PostGIS candidate without waiting on another worker's courier. Both Domain aggregates change before one commit with Outbox/Inbox. The partial Orders.CourierId index protects active capacity even if a different writer makes a mistake. AssignmentId protects subsequent commands from an old worker; a unique capacity index cannot prove that caller holds the current assignment.

CourierOrderCommands derives actor authority; EfDispatchStore reloads the actual current order under lock. EfOrderingStore synchronizes release capacity from immutable transition facts and keeps original xmin so stale restaurant/cancel writes still conflict. DispatchConcurrencyTests asserts both order and courier state after races; testing only the HTTP status would miss a leaked reservation.

### Phase 9 — a timer is a wakeup, a deadline is durable state

RecoverOrderFleet scans persisted timestamps; EfRecoveryStore locks and rechecks the current order/fence before invoking Domain. Repeated timers can observe the same due work; only one can mutate its current state. DispatchJob carries next attempt and bounded counters. CourierAssignment freezes each pickup deadline so a configuration change after restart cannot extend an old reservation. A PickedUp order retains busy capacity even after the earlier pickup deadline; a timer cannot invent a cancellation transition.

ReconcilePaymentCapture reads the provider ledger without creating a charge. An uncertain result stays uncertain until an actual operation result exists. Orphan cleanup is a different recovery path: it waits a grace period and locks the authorization while checking that no local Payment attached. RecoveryTests advances a test clock and creates a fresh host; no sleep is used as the business deadline.

### Phase 10 — groups route messages; permissions authorize data

OrdersHub delegates to OrderRealtimeNotifier, which re-reads live roles and the same OrderAccessPolicy used by HTTP. A group is not proof that a restaurant membership or courier fence is still valid. RealtimeEventHandler consumes a persisted fact but sends latest committed state/sequence, so delayed events cannot regress the client. Reconnect calls SubscribeOrder again and receives a new snapshot; it does not rely on remembered server groups or durable client ACK.

Courier GPS facts contain no coordinates. The authorized notifier reads current fenced position for a specific order and throttles sample delivery. ETA uses database geography distances, explicit speed/factor/buffer assumptions and a low-confidence fallback when GPS is stale. RealtimeApiTests uses actual SignalR transports; RabbitDeliveryTests additionally proves the real broker-to-hub path. Framework package audit uncovered a real security advisory, so SDK/runtime patch level is part of verification evidence rather than assuming build success implies safety.

### Phase11: evidence without private data

Follow an incoming W3C trace through order.create, PostgreSQL, persisted Outbox, confirmed Rabbit publication and consumer handling. SafeActivityProcessor and SafeExceptionListener protect different boundaries: mutable span tags can be filtered after completion, immutable exception event data must be redacted before capture. TelemetryTests checks actual exporters rather than logger mocks. Default root sampling10% is an operational cost choice; tests explicitly use100%.

Liveness proves process response; readiness additionally checks dependency/schema availability with time limits. Operations endpoints require live administrator authority and persist idempotency plus audit. Retrying a technical dead letter does not force an Order transition. RecoveryFailureIsolationTests demonstrates why a bounded per-item failure ledger matters: one bad item must not prevent another timeout or compensation from progressing.

## Executable local walkthrough

Read [13_DELIVERY_GUIDE.md](13_DELIVERY_GUIDE.md) after the contracts. It includes actual seed/login/create/assignment calls, architecture/sequence/data diagrams, failure mapping to named tests, operator replay, private telemetry, retention and patching boundaries. Bootstrap and Invoke-Demo are executable counterparts. ArchitectureTests replaces the historical empty integration placeholder. EndToEndJourneyTests follows capture/dispatch through a full host restart and broker-to-SignalR delivery with durable DB assertions.

## Studying the independent-review fixes

Start with docs/14_HARDENING_REVIEW.md. Follow ArchiveFinancialEvent → EfFinancialArchive → FinancialAuditRecords/Inbox, then RabbitConsumer manual ACK and retry/DLQ behavior. FinancialArchiveTests deliberately omits ACK after commit and verifies redelivery does not duplicate audit. Read WorkerProgress with its fake monotonic clock tests: empty queues differ from stuck handlers, and a new failure attempt must not renew failure grace. WorkerRuntimeTests uses actual broker stop/restart and idle AMQP probes.

SafeLogSink separates diagnostic metadata from private event content before any exporter; unknown incidents retain controlled categories/type/event-code/trace. SafeIncidentTests and TelemetryTests prove unexpected errors remain visible while raw messages, SQL values, tokens and private markers stay out. DispatchContentionTests queries pg_stat_activity to witness two real blocked sessions before releasing the test-only lock; Task.WhenAll alone cannot prove overlap.

## Reading the focused refactoring

EfOrderingStore.SaveAsync remains the transaction coordinator. OrderTransitionEffects.SynchronizeAsync reads each pending fact in order and delegates capacity release, dispatch scheduling and assignment history to private methods sharing its context. Moving the three calls outside the event loop, running them in parallel, or saving inside a helper would change the contract. OrderingTransactionTests demonstrates the caller-owned transaction: changes are observable inside it, pending events remain, and rollback restores order, courier, job, history, audit and Outbox together. IdentityRegistration names JWT and rate-limit blocks; TimeProvider registration belongs to Program. Recovery keeps each lock/recheck/commit visible rather than hiding differing invariants in a generic transaction helper.

## Central versions and independent verification

Directory.Packages.props pins the same24 direct NuGet versions previously stored in project files. Compare resolved project.assets.json libraries before and after centralization; identical direct version strings alone would not prove the transitive graph stayed identical. Docker's restore layer must copy the central file too. Directory.Build.props makes existing nullable/implicit-usings/C#14/deterministic defaults explicit. Formatter cleanup removes unused imports only, with historical migration namespaces/encoding preserved. A compiler/formatter pass cannot replace real integration verification: the new hardening Phase0 remains blocked while Docker returns HTTP500, even though all257 Domain tests pass.

Docker recovery evidence matters: CLI success saying Desktop is starting does not prove the engine or persistent services recovered. Gate0 resumed only after engine connectivity and all three original service health checks succeeded; exited Testcontainers fixtures were identified by labels and left untouched. The full432-test rerun then verified application behavior against real infrastructure. Historical HTTP500 failures remain in the ledger.

Phase1's payment race tests pause immediately before provider capture or after the durable provider commit, then cancel and settle while the accept response remains held. A durable cancellation Outbox fact is the compensation obligation while local payment state catches up. Archive typed financial facts and replay Inbox processing before asserting one final effect. API cancellation has two boundaries: ASP.NET already recognizes RequestAborted and returns499, but calling the custom handler directly still exposed a500/error path. Characterize both so a defensive handler fix cannot turn unrelated cancellation into silent success. Role seed identity and hub log tests pass on old code; valid current values are not proof the positional seed design is safe under future changes.

## Reliability hardening: evidence and protected history

A confirmed Rabbit publication followed by a failed DB acknowledgement belongs to the crash gap, not publication failure: preserve the lease and let at-least-once redelivery meet Inbox deduplication. GPS emission uses a durable last-emitted point/time/assignment fence; every accepted observation still updates both profile and spatial projection in one transaction. Replay of an identical normalized observation does not write.

Cleanup cannot shorten an indefinite replay contract. Request and Inbox identities remain unbounded under D2. Published cancellation facts remain recovery sources until compensation settles; financial history is never a deletion candidate. Bounded DELETE must retain eligibility outside its limited-key subquery so PostgreSQL can reject deletion after a lock-holder reopens a resolved failure. DataCleanupTests proves this actual lock interleaving. RecoveryQueryPlanTests captures real EF SELECTs and bound parameters over100000 orders/jobs: only the large pending-capture sequential scan justified the partial covering index. Synthetic plan timing is evidence of access path, not a production SLA. Idle polling backs off to10s by default while persisted deadlines and API guards remain authoritative. Gate2's499 passing tests supersede the earlier intentionally-red driver milestone; full migration/compose verification remains Gate5.

## Named side effects and focused application ports

OrderTransitionEffects makes the Order event side effects discoverable while sharing EfOrderingStore.SaveAsync's context and transaction. Preserve per-fact courier→job→history sequencing, then Outbox staging and the single SaveChanges. OrderTransitionQueryOrderTests observes this actual SELECT order; existing caller-transaction tests prove multi-fact rollback. OrderLocking has distinct load/xmin and lock-only entry points so consolidation does not reload a tracked aggregate or alter its original concurrency token. CourierCandidateQuery preserves geography selection, distance/Id ties and SKIP LOCKED exactly.

Creation/replay, transitions and listing use separate ports backed by one scoped EF store, not separate units of work. Fixed vocabularies use constants where string contracts already exist; PostgreSQL round-trip tests and EF drift checks protect stored values. DomainException belongs to Common because several aggregates use it; its exact behavior remains unchanged. Product initial availability is validated once. Role seed identity comes from explicit names, not enumeration position. IClock remains the owner-approved TimeProvider wrapper and composition owns registration. CourierPosition is an intentional spatial read projection with transactional equality/replay/rollback coverage. A long method is a review signal: acceptance's explicit late-capture compensation and recovery's differing locked transactions were retained because speculative wrappers would obscure their guarantees.

## Reproducible infrastructure verification

Collection scheduling and business concurrency are separate concerns. xUnit defaults to CPU-count collection threads; a20-core host can start many independent PostGIS/RabbitMQ fixtures against one smaller Docker VM. The final review observed broker-budget failures even after the image build stopped. Bounding Integration collections at two made the unchanged full suite pass while tests still created competing database sessions, barriers and workers internally. The checked-in runner configuration also fails skips. Keep the existing timeout/assertion contracts and physical lock-contention witness; do not substitute isolated successes for a full-suite gate. Record failed runs and the subsequent configuration proof explicitly.

Fresh migration and Compose smoke verification used new owned databases/ports/images, never a developer database or unrelated service. Verify actual migration history, durable broker topology and financial facts as well as HTTP success. Stop only the owned smoke services and retain data; engine connectivity alone does not prove preserved services are healthy. Local verification does not establish remote CI, multi-instance realtime or real-provider readiness.

## Final release baseline — 2026-11-05
Clean47c658a; no remote or owner edits. Restore0; strict Release0 warnings/errors;260 Domain+260 real Integration passed,0 failed/skipped (Integration5m14s). Original running MeetWise services healthy; stopped OrderFleet resources preserved.771 historical blobs scanned without matching current local secret values or high-confidence key/token signatures. Archive exposure and original audit availability remain separate publication gates. Current findings and evidence:16_RELEASE_VERIFICATION.md. Latest owner request authorizes a private GitHub repository/push only after local/security gates.

## Confirmed defects — verified
F-01/F-02 regression run failed2 and passed1 before production edits. Fix: broker unavailability retains capped durable retries indefinitely; invalid envelopes retain the permanent budget. Lease/deadline CAS is unchanged; attempt counter saturates safely. Reconciled capture stages Captured fact and Succeeded attempt before refund in the same existing SaveChanges/Inbox transaction. Both race interleavings and duplicate archive/cancellation processing retain one provider effect and correct fact order. Strict Release0 warnings/errors;44 focused payment/archive/Outbox/operator tests passed,0 failed/skipped. Existing permanent-dead test now uses InvalidEnvelope; historical operator replay explicitly seeds the old BrokerUnavailable dead state and retains all assertions. Diff reviewed. No migration or Domain edit.
Existing live-Admin paged diagnostics (max50) plus per-message audited/idempotent retry already provide bounded recovery for historical dead messages. A new unfiltered bulk revive would risk replaying invalid envelopes and is not justified by a measured operational need; no bulk endpoint added. Pending/dead counts, oldest lag and retry reason metrics already provide alert inputs. New outages recover automatically after more than the old budget without operator replay.
