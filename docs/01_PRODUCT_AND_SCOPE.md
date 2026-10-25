# OrderFleet — product specification and scope

## North star

**OrderFleet — Production-Oriented Restaurant Ordering & Courier Dispatch Backend.** An API-first system that models the complete order lifecycle from authenticated customer order through trusted catalog pricing, restaurant decision, payment, dispatch, courier pickup and delivery. Demonstrate **Domain Modeling, Transactions, Concurrency, Distributed Messaging, Reliability, Security and Testing**. Avoid CRUD-for-CRUD's-sake. A single deployable modular monolith is intentional.

## Primary story

1. **Customer creates an order:** authenticated customer selects a restaurant and catalog products/quantities; **server** reads authoritative restaurant/product/price/availability data, validates order, freezes monetary snapshots, and persists `Created` Order.
2. **Restaurant accepts or rejects:** only authorized restaurant staff can accept/reject relevant order; state machine and timeout rules apply.
3. **Payment workflow:** explicit mock provider with deterministic success/failure; orchestration defines when authorization/capture/refund happens. Never claim real gateway integration. Sequencing is fixed by ADR-008 (authorize on create, capture on accept, void/refund on cancellation) and does not alter the Domain state machine.
4. **Dispatch assigns courier:** dispatch selects eligible nearby available couriers; database atomically claims capacity and updates assignment; `AssignmentId` fences obsolete workers.
5. **Courier picks up/delivers:** only the currently assigned, authorized courier with matching active assignment can transition; location updates use ordered timestamps and sensible validation.
6. **Delivered:** durable final status, persisted event data, audit trail and authorized realtime notifications.

## Actors and trust boundaries

| Actor | Allowed intents | Denied by default |
|---|---|---|
| Customer | browse catalog; create own order; view own order; request permitted cancellation; subscribe to own updates | specify trusted price/status/courier; read another customer's order |
| Restaurant manager/staff | edit own restaurant catalog; accept/reject/view assigned restaurant orders | edit another restaurant; override trusted customer identity |
| Courier | manage own availability/location; view offered/assigned work; pickup/deliver assigned order | pick up unrelated order; steal stale assignment |
| Admin/operator | explicitly scoped support/administrative actions, audited | bypass rules silently; unrestricted impersonation |
| Background worker | process limited application commands with service authority | replace domain checks with direct table edits |

## Domain rules — established versus proposed

**Existing: must preserve unless owner approves change (ADR-013 approves the listed additions: domain events, timestamps, `DomainException`).** `Order` is aggregate root with private items collection; `OrderItem` is part of order aggregate, stored as EF owned collection in its own table; `OrderPricingLimits` is independent policy/configuration; stored `TotalAmount` and item `TotalPrice` are computed snapshots; `Currency = SAR`. Existing statuses: `Created → Accepted → Assigned → PickedUp → Delivered`, plus allowed cancellation transitions. Existing cancellation reasons: CustomerRequested, RestaurantRejected, AcceptanceTimedOut, RestaurantCancelled, AdminCancelled. Existing `AssignmentId` fence prevents stale changes; existing `ReleaseAssignment` returns assigned order to Accepted under permitted conditions. Existing `Order.Create` rejects null/empty items, duplicates by `ProductId`, excessive quantities/count/prices/totals and invalid money precision. Preserve existing semantics verified by tests.

**Proposed to specify before coding.** Payment ordering vs restaurant acceptance; whether restaurant catalog supports delivery zone/open hours; courier acceptance vs system assignment; customer's delivery address and privacy rules; exact retry policies and order acceptance TTL; what happens when payment fails after restaurant accepts; compensation for refund after cancellation. Make explicit scoped decisions as ADRs; do not guess or force inappropriate states into the established order model. **Already decided (2026-10-25):** payment sequence (ADR-008) and auto-assignment (ADR-009); the other items in this list still need ADRs before their phase.

## Feature requirements by module

### Identity & Authorization

- User accounts with unique normalized identifier, safely hashed passwords via established ASP.NET Core Identity or vetted provider; no handcrafted cryptography.
- JWT bearer validation: expected issuer, audience, signature, expiry, clock skew; secure dev key storage; external IdP an optional future concern.
- Explicit roles/permissions and **resource-level ownership checks** for restaurant/order/courier.
- 401 unauthenticated versus 403 forbidden; error bodies avoid leaking resource existence or personal data.
- Auditable administrative interventions; refresh-token feature only if needed and properly specified.

### Restaurants & Catalog

- Restaurant identity, owner/staff relationship, availability status and catalog with product price/currency/isAvailable.
- Product changes are reflected in future orders; existing order item snapshots stay unchanged.
- Product belongs to selected restaurant, cannot reuse foreign product IDs; validate requested quantities and active restaurant.
- Reliable decimal arithmetic and server-side pricing; optional catalog version/price change handling resolved in command transaction.

### Orders

- Create, get/list own, restaurant accept/reject, permitted cancel; later assigned/pickup/delivery APIs.
- Preserve currently defined Domain state machine and cancellation reason matrix.
- Persist status transitions and assignment fencing in real DB; conflicts return meaningful status (normally 409) without leaking sensitive facts.
- Idempotency for retried create/payment commands; no double charge/order for a repeated request with same key and scope.

### Payments (mock)

- Separate Payment intent/state from Order aggregate where lifecycle merits it, with scoped amount/currency/order association and immutable references.
- Controlled mock outcomes: authorized/captured/failed/refunded; prevent duplicate charges/capture/refund via idempotency and unique constraints.
- Explicit order/payment timing, compensating actions and failure rules in ADR; test rejection, timeout, crash and repeated callback simulations.

### Couriers & Geo

- Courier account/profile, operational state (offline/available/reserved/busy), latest location/time and assignment consistency.
- PostGIS coordinates with documented SRID/unit semantics; spatial index; bounded-radius query and region filtering.
- Reject invalid lat/lon; handle missing, stale, duplicated and out-of-order updates.
- Geo exposure follows least privilege; no broadcasting exact courier coordinates to arbitrary clients.

### Dispatch

- Candidate policy: eligibility, distance, freshness, status; deterministic tie-break for reproducible tests.
- Atomically claim courier and transition order; ensure no two active incompatible orders claim same courier; one active assignment per order.
- Fence every stale delivery/pickup/release attempt via `AssignmentId`; handle reassign/cancel/timeout races.
- Retry with bounds/backoff and safety for concurrent workers; record reason when no courier available.

### Messaging / Background

- Transactional Outbox coupled to business transaction and idempotent consumer processing.
- RabbitMQ events represent facts; documented versioned contracts, message IDs, correlation IDs and causality.
- Publisher confirms, manual consumer acknowledgement where appropriate, bounded retries, dead-letter handling and poison-message metrics.
- Recovery for unfinished claims, outbox publishing, acceptance/dispatch timeouts across process restarts.

### Realtime / ETA

- Authorized SignalR subscriptions scoped to customer/order/restaurant/courier; no unauthorized group joins.
- Order status and courier location notifications reflect **persisted events**, not optimistic unsaved state.
- ETA is a labeled approximation (distance/speed + bounded adjustments); not Google-Maps-level turn-by-turn accuracy or real road routing.

### Operations & Delivery

- Structured redacted logs, activity tracing/metrics, basic liveness and readiness health checks.
- Reliable local Compose for API/worker/Postgres with PostGIS support/RabbitMQ and optional observability stack.
- Clean checkout setup, deterministic migrations, seed/sample dataset, documented curl/http scenarios and CI.

## Explicit non-goals (to avoid scope creep)

- No production payment credentials or real-money transactions.
- No mobile/frontend application, no full merchant back-office UI.
- No microservices extraction, event sourcing or distributed transactions across DB and RabbitMQ.
- No automated cloud deployment or mandatory paid mapping services.
- No claim of production SLA/compliance certification without evidence.
- No speculative inventory reservation, promotions, tax engine or multi-currency support unless separately approved.

## Whole-system acceptance demonstration

One tested path: authorized customer creates order from real catalog prices → DB snapshot and Outbox → mock payment as defined → restaurant accepts → dispatch atomically reserves eligible courier → assigned courier picks up/delivers → DB confirms terminal status → authorized SignalR and RabbitMQ events are observed; replays, duplicate calls, stale workers and restart do not corrupt state. Include negative cross-tenant authorization, concurrent courier claims and a crash/recovery case.
