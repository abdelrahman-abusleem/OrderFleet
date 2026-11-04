# Code study guide

Read the production flow alongside its tests. Each step below names the code that owns the decision and the failure scenario that explains it. The [architecture](02_ARCHITECTURE.md), [decisions](08_DECISION_LOG.md) and [HTTP contracts](11_API_DATA_CONTRACTS.md) provide the surrounding context.

## 1. Start with the dependency graph

Open OrderFleet.slnx and the four project files, then ArchitectureTests. Domain references no framework package. Application depends only on Domain. Infrastructure implements application ports. Api binds transport and configures adapters. A shared database does not make every class responsible for transactions: the use case and adapter own explicitly named boundaries.

## 2. Follow the Order aggregate

Read Domain/Orders/OrderItem, OrderPricingLimits and Order before persistence. Item values are immutable. Create copies and validates the collection, detects duplicates/overflow and computes snapshots once. The read-only collection prevents callers mutating aggregate contents after validation. Compare OrderCreationTests and OrderItemTests, including decimal.MaxValue and precision boundaries.

Walk Accept, Assign, MarkPickedUp, Deliver, ReleaseAssignment and Cancel with OrderStateMachineTests and OrderCancellationTests. Legal state and cancellation reason are different checks. A courier identity is insufficient without the current AssignmentId. Release captures the old fence in its event before clearing active fields. Rejection leaves state, timestamps and pending events unchanged.

Domain timestamps come from supplied instants. The domain does not read the machine clock. IClock/SystemClock remains a thin TimeProvider adapter; ClockRegistrationTests demonstrates override and lifetime. DomainException distinguishes business/fence conflicts; argument validation retains exact ArgumentException contracts.

## 3. Rehydrate and protect a stale writer

Open OrderConfiguration and OrderFleetDbContext. Owned item rows map through the private backing field. Numeric checks preserve supported decimal values without forced scale; timestamp converters preserve instants in UTC. PostgreSQL precision is microseconds, not .NET ticks.

The shadow xmin value is persistence metadata. A stale entity can pass its in-memory fence check and still lose at SaveChanges. OrderConcurrencyTests reads with two contexts and verifies the winner in a third. OrderLocking.LockTrackedAsync deliberately does not replace the original xmin by reloading a tracked root. A filtered event-sequence uniqueness conflict can occur before the root update; it maps to the same optimistic409 and rolls back all staged work.

## 4. Create from authoritative catalog data

Read OrderWriteEndpoints → CreateOrder → EfOrderingStore.CommitNewAsync. The client sends product IDs/quantities and private destination, never price, customer identity or status. The use case reads catalog prices and computes a canonical request fingerprint. Persistence share-locks and rechecks the authorization, restaurant and sorted products before committing snapshots, payment, delivery/deadline, replay identity, audit and Outbox.

Two first requests can race. A unique actor/key decides the durable winner; the loser reads and compares the fingerprint. Same request replays, a different request conflicts. CatalogOrderingApiTests proves parallel replay, stale catalog rejection and privacy. An existence check or in-memory lock alone would not close either race.

## 5. Separate provider effects from local commit

Read Payment, DurableMockPaymentProvider, CaptureOrderPayment, OrderCommands.AcceptAsync and SettleCancelledPayment. The provider ledger intentionally commits independently, so a captured payment can survive a failed local accept. Stable provider operation keys prevent a second charge; uncertain outcomes reconcile rather than charge again.

Acceptance checks its persisted deadline before and after capture. A late capture is still a real financial fact, but cannot authorize acceptance. Cancellation has a durable Outbox obligation even while a capture HTTP response is held. CaptureCancellationRaceTests pauses both interleavings and proves void/refund convergence. Settlement records a newly discovered capture fact and succeeded attempt before refund; replay cannot duplicate either history or provider effects.

FinancialAuditRecords and their Inbox are an immutable typed projection, not financial authority. FinancialArchiveTests covers conflicting identity, money extremes, poison handling and commit-before-ACK. Broker reordering does not replay history as new payment mutations.

## 6. Stage side effects without splitting the transaction

Read EfOrderingStore.SaveAsync, OrderTransitionEffects and OutboxEventWriter. Per fact, courier capacity is synchronized before dispatch job and assignment history. The same context/transaction stages Outbox and saves once. The collaborator does not commit. OrderTransitionQueryOrderTests observes actual query ordering; OrderingTransactionTests proves rollback of multi-fact work.

## 7. Prove a courier claim across processes

Read EfDispatchStore and CourierCandidateQuery. The adapter locks Order first, checks captured payment and current state, then uses geography metres/freshness/availability and SKIP LOCKED to select a courier. Domain Assign generates the fence; courier reservation, historical assignment, scheduling, audit, Inbox and Outbox commit together. Partial unique indexes remain the final capacity backstop.

DispatchContentionTests witnesses actual blocked PostgreSQL sessions, skips a locked nearest candidate, and changes eligibility after a query snapshot. These are evidence for retaining the existing algorithm. Stale pickup/release/delivery tests explain why both order and courier fences matter even when the courier identity is unchanged.

## 8. Distinguish observation from notification

Read EfCourierStore, CourierPosition and CourierLocationEventOptions. Every accepted observation persists both location representations. A durable last-emitted position/time/assignment checkpoint controls events, not acceptance. First observation of a new assignment emits; later observations emit at25m movement OR10s elapsed. Identical normalized replay saves nothing. CourierEventThrottleTests and CourierReplayTests prove exact boundaries, consistency and atomic stale-writer rollback.

## 9. Follow an Outbox fact through RabbitMQ

Read PostgresOutboxStore → OutboxRelay → RabbitEventPublisher → RabbitDeliveryProcessor. Claims are DB-side, leases expire, writes are fenced and publication awaits routing/confirm. A broker outage gets delayed retry indefinitely; poison envelopes dead-letter. Confirmed publication followed by Complete failure is a crash gap and leaves the lease, not a publication failure.

Consumers commit their effects/Inbox, then ACK. If the retry copy cannot be confirmed, the original remains unacked for redelivery. OutboxOutageTests exceeds the old retry budget with a stopped real broker and publishes after restart. OutboxRelayReliabilityTests and RabbitDeliveryTests demonstrate duplicate facts with one business effect. There is no global exactly-once or cross-relay publication-order guarantee.

## 10. Recover from durable state

Read EfRecoveryStore, RecoverOrderFleet and RecoveryWorker. Expired creation, pickup reservation, dispatch retry, payment settlement, uncertain capture and orphan authorization are distinct obligations. UTC deadlines and fences survive restart; per-resource failure records isolate poison work with bounded retry/dead state. IClock controls policy time; monotonic WorkerProgress controls health.

DataCleanupTests demonstrates why deletion eligibility is checked again outside a bounded candidate subquery after a concurrent lock holder changes state. Request/Inbox identities and financial/order audit are protected. RecoveryQueryPlanTests uses realistic historical deadlines, idle and active distributions; the narrow Created/Accepted indexes have measured justification. Other history-sensitive exact counts remain an explicit capacity limitation.

## 11. Authenticate, then authorize the current resource

Read IdentityRegistration, JwtTokenIssuer, OrderAccessPolicy and EfRealtimeStore. A signed role is intersected with current privileged grants; membership and assignment are separate live checks. A foreign resource returns404 without private payload. Tokens do not acquire new roles simply because the DB grant changes.

Read ManageIdentityAccess and EfIdentityAdministrationStore. System-role provisioning belongs to live Admin; restaurant membership belongs to a current scoped Owner. Sorted user locks serialize duplicate grants and concurrent revocation. IdentityAdministrationTests holds the actor lock, revokes authority, then verifies the waiting request is rejected. Audit and mutation share a transaction. No HTTP Admin provisioning exists.

## 12. Inspect browser and realtime boundaries

Read CorsRegistration, OrdersHub, OrderConnections and OrderRealtimeNotifier. CORS is an exact allowlist; WebSocket Origin needs an independent check. Group membership is transport routing, not authority. Subscribe/send refresh roles, membership, assignment and expiry. Old sequences/observations are suppressed. Reconnect requires snapshot refresh because notification is best effort.

FrontendBoundaryTests exercises actual preflight/WebSocket handshakes and private courier discovery. RealtimeApiTests and HubLogPrivacyTests prove revocation, token expiry and query-token privacy. One API instance is supported; a backplane alone would not distribute the local subscription authorization registry.

## 13. Diagnose without leaking customer data

Read SafeLogSink/Policy, SafeActivityProcessor and SafeRequestLogging before adding diagnostics. Approved templates and properties remain structured; unknown incidents become safe correlation/type/event metadata. Public health is generic. Live-Admin runtime diagnostics expose bounded worker/broker observations without payloads or connection URIs. TelemetryTests and SafeIncidentTests verify real exporter behavior.

## 14. Run the system and explain its limits

Use Bootstrap, Invoke-Demo and Verify-Migrations with fresh owned resources. Follow the [testing guide](05_QUALITY_GATES.md). Explain why mock payments, heuristic ETA, single-instance realtime, indefinite deduplication identities and synthetic plan timings are honest limits. A green test suite does not demonstrate real-provider processing, production scale, HA or a completed operational security review.
