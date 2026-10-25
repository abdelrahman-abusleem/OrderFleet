# Proposed external/API/data contracts — implement deliberately

## Implemented contracts through Phase 4 (2026-10-25)

Authentication: register/login/me and strict JSON DTOs, as described in README. Access token role is insufficient without resource ownership. Foreign and nonexistent order/restaurant resources use the same 404; unsupported roles use 403, no token uses 401.

Catalog: anonymous bounded `GET /api/v1/restaurants` and `GET /api/v1/restaurants/{id}/products`; page defaults 1, pageSize 20 (max 50). RestaurantOwner can `POST /api/v1/restaurants` with name/latitude/longitude; actor receives the restaurant membership in the same transaction. Members with RestaurantOwner/RestaurantStaff can create products and `PUT /api/v1/restaurants/{id}/products/{productId}` with name/price/isAvailable; default product availability is true. `PUT /api/v1/restaurants/{id}/availability` requires isActive. No body owner identity, currency override, opening-hours or zone engine. Price cap comes from configured Ordering policy.

Orders: Customer `POST /api/v1/orders` requires one `Idempotency-Key` header (1..100 characters). Body:

```json
{"restaurantId":"00000000-0000-0000-0000-000000000001","items":[{"productId":"00000000-0000-0000-0000-000000000101","quantity":2}],"latitude":0,"longitude":0}
```

Coordinates are mandatory, range-validated and private; zero is a valid coordinate. Unknown JSON fields (including supplied unitPrice/customerId/status) are rejected. Item quantities/count, duplicates and trusted catalog availability/prices are validated. Success is 201 with `{ "id": "..." }` and Location. Same authenticated customer + key + canonical request replays that result; changing the request with that key returns 409. Different customers have separate key scope. Dedupe records are currently retained without expiry.

`GET /api/v1/orders/{id}` returns authorized item/name/price snapshots and status, never destination coordinates. `GET /api/v1/orders` is the customer's own paged list; restaurant users supply `restaurantId` and must be a live member. `GET /api/v1/orders/{id}/delivery` returns coordinates/deadline only after the same participant authorization. Precise destination does not enter logs, traces or public events.

Restaurant members `POST /api/v1/orders/{id}/accept` before the persisted deadline, or `/reject` while Domain permits. Accept at/after the deadline returns 409 regardless of whether a worker ran. `/cancel` derives its reason from authenticated actor: customer request, restaurant cancellation or admin cancellation. Clients cannot choose a more privileged cancellation reason. Repeated/invalid transitions return 409; Domain state machine is unchanged. Audit actor/action rows share the successful state transaction. Financial authorization/capture/void/refund coupling is Phase 5 work, not yet implied by these current endpoints.

OpenAPI `/openapi/v1.json` is exposed in Development/Testing only, with bearer authentication and required create-order idempotency header. Public catalog/auth operations override the default security requirement; protected endpoints document 401/403 and writes document 400/409. No real credentials or customer coordinates appear in examples.

> **NOT an override of existing Domain semantics.** These contracts provide detailed guidance and illustrative names. Verify actual repository patterns and record approved changes via ADR. No schema/client contract is guaranteed until implemented/tested.

## HTTP conventions

- Prefix: `/api/v1` unless current API already has an accepted convention.
- JSON camelCase; UTC ISO 8601 timestamps; GUIDs for internal identifiers; SAR monetary fields serialized explicitly as decimal amounts with currency.
- Prefer domain-specific DTOs over EF tracked entities as request/response payloads.
- Responses: success `200/201/204`, validation `400`, authentication `401`, authorization `403`, privacy-aware missing resource `404`, business/concurrency/replay conflict `409`, infrastructure `503`. Return RFC 7807/9457-style ProblemDetails consistently. Avoid revealing stack trace or credentials.
- For retried create/financial operations, `Idempotency-Key` required where supported, unique to actor + operation; document TTL/fingerprint/replay behavior.
- Pagination on lists; bounded `pageSize`, stable sorting; cancellation tokens.

## Proposed endpoints (names may be refined; preserve behavior)

| Actor | Verb/path | Business behavior | Auth + critical safeguards |
|---|---|---|---|
| Customer/public | `GET /api/v1/restaurants` | browse active restaurants | public summary; no sensitive staff data |
| Customer/public | `GET /api/v1/restaurants/{id}/products` | browse active catalog | current price, no historic order mutation |
| Restaurant staff | `POST /api/v1/restaurants/{id}/products` | add controlled product | ownership, decimal/currency/availability validation |
| Restaurant staff | `PATCH /api/v1/restaurants/{id}/products/{productId}` | update price/availability | same restaurant membership, audit changes |
| Customer | `POST /api/v1/orders` | create from selected product IDs/qty | own identity, trusted price, idempotency |
| Customer | `GET /api/v1/orders/{id}` | own order detail | ownership/least privilege |
| Customer | `GET /api/v1/orders` | own paginated orders | principal derived from token |
| Restaurant staff | `POST /api/v1/orders/{id}/accept` | accept restaurant's Created order | ownership, transition + concurrency |
| Restaurant staff | `POST /api/v1/orders/{id}/reject` | reject as permitted reason | ownership, transition + compensation rules |
| Customer/admin/restaurant | `POST /api/v1/orders/{id}/cancel` | policy-appropriate cancel | role/reason matrix, concurrency, compensation |
| Courier | `PUT /api/v1/couriers/me/location` | update own GPS timestamp | principal-bound, staleness and lat/lon validation |
| Courier | `PUT /api/v1/couriers/me/availability` | set eligible status | cannot force busy while assigned |
| Courier | `POST /api/v1/orders/{id}/pickup` | mark pickup | current assignment and `AssignmentId` fence |
| Courier | `POST /api/v1/orders/{id}/deliver` | complete delivery | authorized matching assignment/state |
| Customer | `GET /api/v1/orders/{id}/eta` | current best-effort ETA | own order, fallback clearly described |
| Auth client | `POST /api/v1/auth/register` and `/login` | demo identity | safe password flow + rate limiting |

**Dispatch assignment** should be triggered by correct server-side workflow/event, not exposed as unauthenticated `PUT /orders/{id}/courierId`. If an admin action exists, separate policy and audit.

## Illustrative create-order request

```json
{
  "restaurantId": "00000000-0000-0000-0000-000000000001",
  "items": [
    {"productId": "00000000-0000-0000-0000-000000000101", "quantity": 2},
    {"productId": "00000000-0000-0000-0000-000000000102", "quantity": 1}
  ]
}
```

**Notice absence:** no `CustomerId`, `UnitPrice`, `TotalAmount`, `Status`, `CourierId`, `AssignmentId` input. Real tests must reject/ignore forbidden fields per approved strict DTO semantics. Service loads the real product names/prices and computes immutable order snapshots.

## Data model concept (verify actual schema and naming)

| Table / model | Core fields and correctness constraints |
|---|---|
| `Orders` | Id PK; CustomerId; RestaurantId; Status; Currency; TotalAmount; CourierId?; AssignmentId?; CancelReason?; concurrency version; indexes by customer/restaurant/status |
| `OrderItems` | owned rows with PK/FK to Order; ProductId; Name; UnitPrice; Quantity; TotalPrice; cascade only where domain warranted |
| `Users/Roles/Memberships` | account identity, password/role credential or IdP subject; normalized unique account IDs; restaurant membership FK |
| `Restaurants`, `Products` | product belongs to exactly one restaurant; decimal price, currency SAR, current availability and appropriate indexes |
| `Payments`, `PaymentAttempts` | amount/currency/orderId, mock provider correlation, transition state and uniqueness for idempotency |
| `Couriers`, `CourierLocations` | availability/current reservation + `geography(Point,4326)`, observed timestamp, appropriate GiST index |
| `DispatchAttempts`, `Assignments` | order/courier/assignment fence ID, status, lease/deadline, uniqueness preventing conflicting active claims |
| `OutboxMessages` | unique ID, versioned serialized event, attempt/lease/confirmation timing and correlation |
| `InboxMessages` | consumer identity + message ID unique marker with processed time |
| `IdempotencyRecords` | actor+operation+key unique with request fingerprint, result, expiration |

### Monetary schema caution

`numeric(18,2)` allows up to **16 integer digits**, while C# `decimal` and a policy object could permit larger values. Either bound allowed amounts at application/options level consistently or choose supported schema precision deliberately. Do not perform a destructive change without data migration review. Store decimal snapshots exactly and test round trips.

### Location schema caution

PostGIS extension must be available inside the selected container; `postgres:17` alone is not enough. For `geography` distances are meters; for `geometry`, units depend on SRID. Never silently use degree values as meters.

## Request and event examples that need finalization

- Define stable versioned event names like `order.created.v1`, `order.accepted.v1`, `dispatch.assigned.v1`, `courier.location-updated.v1`, `order.delivered.v1`; **these are proposals, not fixed requirement to publish sensitive full payloads**.
- Event envelope: `MessageId`, `Type`, `Version`, `OccurredAtUtc`, `CorrelationId`, `AggregateId`, minimal non-PII payload. Document consumers and whether global/aggregate order matters.
- Business failure event semantics for payment/dispatch/timeout to be decided. Avoid emitting events from failed transactions.

### Implemented payment behavior (Phase 5)
Create may return 402 on authorization decline or 503 on provider unavailability, without persisting an Order. Accept returns 409 on permanent capture decline or elapsed persisted deadline; 503 distinguishes retryable failure and uncertain outcome via safe ProblemDetails titles. Capture uses a stable server-derived key; retry the same accept endpoint. A permanent decline remains declined. Unknown outcome reconciles the provider operation before proceeding. See ADR-017. No card data, external payment callback or real gateway exists in this mock.

### Implemented courier API (Phase 6)

Courier role: PUT /api/v1/couriers/me registers own offline profile idempotently; GET reads own status/last observation (no coordinates). PUT /me/availability accepts required isAvailable. PUT /me/location accepts required latitude, longitude and observedAt UTC. Invalid/time-window errors400, duplicate/out-of-order/conflicting writes409. Unknown fields such as courierId reject400; identity always comes from claims.

GET /api/v1/restaurants/{id}/nearby-couriers?radiusMeters=5000&limit=20 requires owner/staff live membership or Admin. Returns courier ID, distanceMeters and observation time, without coordinates. Available/fresh positions only, deterministic distance then ID ranking. Config Couriers:LocationFreshnessSeconds default120, FutureSkewSeconds default15, MaxRadiusMeters default20000. See ADR-018 for bounds and storage semantics.

### Implemented dispatch/courier order transitions (Phase 8)

Server dispatch consumes order.accepted.v1 / order.assignment-released.v1. No public assignment/courier selection input. Eligibility independently requires accepted order + captured matching payment + available fresh nearby courier. Attempts/jobs and active-capacity uniqueness persist in PostgreSQL.

Courier POST /api/v1/orders/{id}/pickup and /deliver accept {assignmentId}. Current assigned identity and fence required. POST /release-assignment supports the current Courier or Admin and same fence, only before pickup as Domain allows. Empty fence400; wrong courier404; stale/invalid current-owner operation409. Pickup makes courier Busy; delivery/release clears current capacity atomically. Delivered Order keeps historical assignment identifiers. Release returns Order to Accepted, then dispatch produces a new fence even for the same courier. Admin cancellation of Assigned orders also releases capacity atomically. See ADR-020.

### Implemented recovery policy (Phase 9)

No public timer-bypass endpoint. Recovery defaults enabled with poll1000ms/batch20. Acceptance retains original5-minute configured deadline and established reason. Dispatch:PickupTimeoutMinutes defaults5, frozen per AssignmentId; pickup at/after deadline409 even before worker release. Expired current unpicked reservation releases to Accepted and may reassign with a new fence. PickedUp never automatically releases. Dispatch:MaxAssignments defaults3 and MaxAttempts10; exhausted scheduling is visible DB state awaiting authorized support, not a new order cancellation reason. Recovery:OrphanGraceMinutes defaults5. Options and exact restart/reconciliation semantics are ADR-021; diagnostics/replay endpoints are Phase11.
