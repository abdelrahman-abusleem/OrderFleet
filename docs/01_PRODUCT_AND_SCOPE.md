# Product and scope

OrderFleet coordinates a restaurant order from a trusted catalog snapshot through mock payment, restaurant acceptance, automatic courier assignment and delivery. PostgreSQL persists the business state and recovery obligations; RabbitMQ transports committed facts. The system is a modular monolith with four projects.

## Participants

| Participant | Capabilities |
|---|---|
| Customer | Browse, create with private destination, list/read own orders, cancel while Created, receive authorized updates |
| Restaurant Owner/Staff | Manage catalog and orders for current restaurant memberships; accept/reject within policy |
| Restaurant Owner | Manage eligible memberships within restaurants currently owned |
| Courier | Maintain own availability/location, discover active assignments, pickup/deliver/release with the current fence |
| Admin | Audited support/recovery and restricted non-Admin role provisioning, with live role validation |

Public registration grants Customer only. Admin provisioning is non-public. Ownership uses the existing global RestaurantOwner role plus restaurant membership; there is no separate per-membership role field. System-role changes and membership changes are different authorities.

## Order rules

The successful path is Created → Accepted → Assigned → PickedUp → Delivered. Releasing an unpicked assignment returns Assigned → Accepted and clears active identifiers. Reassignment generates a new AssignmentId, even for the same courier.

| Current state | Permitted cancellation reasons |
|---|---|
| Created | CustomerRequested, RestaurantRejected, AcceptanceTimedOut, AdminCancelled |
| Accepted | RestaurantCancelled, AdminCancelled |
| Assigned | AdminCancelled |
| PickedUp/Delivered/Cancelled | None |

Cancellation reasons come from server authorization, not request input. Invalid/repeated transitions and stale courier/fence pairs are rejected without changing the aggregate. Delivered retains its historical assignment identifiers while current courier capacity is released. Cancelled clears assignment information.

Names, prices, quantities and totals are immutable order snapshots. Product IDs must be unique; item collections are copied and read-only. SAR decimals must be positive with at most two decimal places and must fit configured limits without overflow. Persistence preserves the full supported decimal range rather than silently rounding to numeric(18,2).

## Payment and scheduling

Create authorizes the durable mock payment. Acceptance captures before its transition commits and checks the original persisted deadline both before and after capture. Dispatch requires Accepted plus matching captured payment. Default acceptance and pickup deadlines are five minutes. UTC deadlines survive restarts; delayed workers cannot make expired acceptance/pickup legal.

Cancellation voids an uncaptured authorization or refunds a confirmed capture. Compensation reconciles actual provider state. A capture-first cancellation records capture and refund facts together; retries cannot duplicate provider effects. Confirmed transient capture failure is retryable before the deadline; permanent decline replays the declined outcome; uncertain outcomes reconcile the stable operation.

GPS updates persist the aggregate and spatial projection atomically. Events require an active assignment and movement at least25 metres or elapsed time at least10 seconds from the last emitted observation. Identical normalized replay is a no-op; stale/conflicting observations are rejected.

## Implemented boundaries

Included: PostGIS discovery, transactional Outbox/Inbox, durable financial archive, concurrent dispatch/fencing, persisted recovery, restricted identity administration, CORS/SignalR origin policy, approximate ETA, telemetry, operational diagnostics, conservative cleanup and real infrastructure tests.

Deliberate limits: mock payments, straight-line ETA, one active order per courier, one API instance for realtime, simple restaurant active/product available flags, and no delivery-zone/opening-hours engine. No frontend, real payment gateway, road routing, refresh-token service or SignalR backplane is implemented. Retention preserves HTTP/Inbox deduplication indefinitely. Production capacity and operational readiness require separate deployment validation.
