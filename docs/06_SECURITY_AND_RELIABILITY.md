# Security and distributed reliability requirements

## Threat model and boundary checklist

**Attackers:** unauthenticated clients, authorized customers trying foreign orders, rogue restaurant/courier role access, replaying clients, compromised stale background worker, malformed broker message and inadvertent operator mistakes. Protect order values, actor authority, courier locations, auth secrets and operational integrity.

- Every command derives actor IDs from verified claims/authorization context, not from trusted request body values. Customer may propose product IDs/quantities, never final catalog price, payment status, accepted restaurant/courier IDs or bypass statuses.
- Match ownership at both route/API and use-case level where needed; resource membership changes may invalidate access. Don't rely on SignalR group name as authorization.
- Validate all inputs (length, quantity, currency, decimal scale, latitude/longitude, timestamps, pagination, idempotency key limits); normalize where appropriate; avoid exception-driven validation for expected user errors.
- Use parameterized SQL or safe EF queries; audit any raw SQL and transaction semantics. Bound listing/search results. Rate-limit login, sensitive commands and location updates pragmatically.
- JWT: validate signing algorithm/key, issuer, audience, lifetime/clock skew; strictly scope permissions. Use documented .NET security primitives. For dev use User Secrets/environment; for deployments use env/secret manager. No secrets committed.
- Prefer generic client error messages without leaking which foreign resource exists. Log minimal necessary identifiers/correlation IDs; minimize location/PII retention, never log access tokens/passwords/card data.

## API idempotency contract

For retryable mutating operations (e.g., order creation, mock payment capture/refund), require a scoped idempotency key where practical:

- Key scope = authenticated actor + operation/route + supplied idempotency key; max length/TTL defined.
- Store request fingerprint and final response/result **atomically** with business effect or in appropriately coordinated transaction.
- Same key and same canonical request → replay same business outcome; same key with materially different request → conflict, not a second effect.
- Concurrent first requests for same key use unique DB constraint and controlled winner/loser behavior; verify in integration test.
- Define behavior for in-flight or failed transient attempts; no permanent lock on half-failed pre-transaction attempt.
- Do not claim all POST routes require one key if it is not applied; document exactly supported endpoints.

## Concurrency and assignment integrity

**Protection stack:** Domain transitions guard local legality, DB version guards stale Order updates, atomic courier claim and constraints prevent simultaneous claims, `AssignmentId` fences delayed workers. All four layers have different jobs.

- DB schema prevents multiple conflicting active assignments per courier (partial unique index/lock/conditional update as appropriate).
- Dispatch transaction jointly validates order current status, courier availability, current assignment, payment readiness if required, and writes claim + order + event.
- Retry transactional serialization/deadlock only when safe; reload current state and rerun domain checks; bounded backoff. Do not automatically reexecute a payment without its idempotency key.
- Stale worker cannot cancel/release/pickup/deliver a later assignment after reassignment. Durable check is necessary.
- Define ordering of locks/atomic operations to reduce deadlocks; observe and measure conflict outcomes.

## Outbox, publisher and consumer contract

**DB commit does not atomically publish to RabbitMQ.** Use transactional Outbox, publish after commit, and assume at-least-once with duplicates.

1. Within same PostgreSQL transaction, update domain records and insert versioned outbox event.
2. After commit, worker leases/claims pending outbox rows, publishes persistent message to durable intended route, and waits for publisher confirm. Verify routability (e.g. mandatory-return behavior/topology) as appropriate.
3. Mark published only after confirmed broker acceptance. If process crashes between publish and mark, it may publish again. Consumers dedupe.
4. Consumer validates payload/event version and message identity. In one durable transaction, insert processed-message marker and perform side effect; ack after commit.
5. Failure before consumer commit → nack/requeue or retry; failure after commit before ack → duplicate delivery, marker prevents duplicate effect.
6. Poison message policy: bounded retries with delay/backoff, dead-letter visibility, sensitive logs redacted. No infinite hot requeue loops.
7. Multiple outbox worker processes cannot claim/mark each other's leases accidentally. Recover expired leases after crash.
8. Event ordering: don't assume global order. Use aggregate version/sequence if a subscriber requires order; idempotent stale-event handling.
9. Broker publisher confirms and consumer acknowledgements are independent; neither means “exactly once end to end.”

## Recovery and schedule correctness

- Persist deadlines and attempts in UTC; monotonic observation where appropriate. Keep test clock injectable and bounded timeouts.
- Workers may run concurrently and restart; claim jobs atomically and with leases/fences. Idempotent timeout handler checks latest state before cancel/release.
- Dependencies down: API should report appropriate partial unavailability for operations that need them; outbox should backlog under documented policy, not block local business transaction on network roundtrip.
- Define retention/cleanup for outbox/inbox, locations, audit and idempotency records without sacrificing active operation correctness.

## Failure injection suite — minimum acceptance scenarios

| Injection | Expected protected result |
|---|---|
| API repeats order create with same key | One Order, same resulting response/outcome |
| Two processes accept/cancel same order | Legal single state, conflict recognized; no lost update |
| Two dispatch workers claim one courier | At most one incompatible active claim |
| Worker publishes and dies before marking Outbox | Event may replay; consumer business effect once |
| Consumer commits, dies before ACK | Redelivery safely no-op at business side effect |
| RabbitMQ outage after Order commit | Order + Outbox intact; event published on recovery |
| Expired acceptance timeout races restaurant acceptance | One legal durable state; compensation as agreed |
| Old AssignmentId submits Pickup after reassign | Rejected; latest assignment unchanged |
| User guesses another order ID / SignalR group | Unauthorized, no payload leakage |
| Courier sends stale GPS timestamp | Latest valid location not overwritten |

## Explicit safety boundaries

- Mock payments do not process real money or PCI data.
- Demo JWT setup not a substitute for production identity governance.
- Dev secrets are local; tracked `compose.yaml` should be sanitized for publicly released portfolio, with `.env.example` placeholders and setup instructions.
- Operational maturity must be described as **production-oriented**; don't imply audited enterprise production-readiness.

## Observed hardening controls (2026-10-26)

Do not equate enabled worker configuration with health: monotonic progress/lifecycle and independently verified idle connections now feed readiness. Busy processing suppresses idle heartbeat, persistent failures keep their original grace origin, and dead/backlog alerts remain independent of readiness to avoid poison restart loops. Public health is generic; live Admin runtime diagnostics are bounded and omit URI/payload/exception details.

Financial archive+Inbox is atomic before ACK. Schema/type/state/identity/money/currency checks precede archival; conflicting message identity is rejected, historical order/state sequence is not reapplied. Retained DLQ facts require controlled confirmed replay, not a new payment effect. Broker ready-message metrics exclude unacked deliveries and include sample timestamps in runtime diagnostics.

SafeLogSink normalizes unknown Warning/Error incidents rather than suppressing all exceptions or exporting unrestricted data. SafeExceptionListener/ActivityProcessor still protect immutable trace events and SQL/baggage. Real HTTP/EF/Rabbit tests assert diagnostic visibility, correlation and private marker exclusion.
