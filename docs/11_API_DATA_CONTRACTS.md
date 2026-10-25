# Proposed external/API/data contracts — implement deliberately

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
