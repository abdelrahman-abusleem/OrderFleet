# HTTP and event contracts

Base prefix: /api/v1. JSON is camelCase; IDs are GUIDs; timestamps UTC. Authenticate with Authorization:Bearer using POST /auth/login. GET /auth/me returns own ID/roles. Public POST /auth/register accepts email/password and grants Customer only; duplicate registration has the same202 public outcome. Unknown JSON fields are rejected. No refresh tokens or real gateway callbacks exist.

## Endpoints

| Method/path | Authority and result |
|---|---|
| GET /restaurants | Public active restaurant summaries; page/pageSize |
| GET /restaurants/{id}/products | Public available products; page/pageSize |
| POST /restaurants | RestaurantOwner creates restaurant and own membership |
| PUT /restaurants/{id}/availability | Current scoped Owner/Staff; required isActive |
| POST /restaurants/{id}/products | Current scoped Owner/Staff; name,price,isAvailable |
| PUT /restaurants/{id}/products/{productId} | Current scoped Owner/Staff; snapshot history unchanged |
| POST /orders | Customer, trusted catalog, required Idempotency-Key;201{id} |
| GET /orders | Own customer list, or restaurantId with current Owner/Staff membership |
| GET /orders/{id} | Authorized participant snapshot; no destination coordinates |
| GET /orders/{id}/delivery | Authorized participant destination and persisted acceptance deadline |
| POST /orders/{id}/accept | Current restaurant member, capture before acceptance, original deadline |
| POST /orders/{id}/reject | Current restaurant member and allowed reason/state |
| POST /orders/{id}/cancel | Server derives customer/restaurant/Admin reason; domain matrix applies |
| PUT /couriers/me | Courier registers own offline profile idempotently |
| GET /couriers/me | Own status/last observation, no coordinates |
| GET /couriers/me/orders | Own Assigned/PickedUp orders/fences, paged; no private coordinates |
| PUT /couriers/me/availability | Courier; required isAvailable; cannot override active capacity |
| PUT /couriers/me/location | Courier; required latitude,longitude,observedAt |
| GET /restaurants/{id}/nearby-couriers | Current restaurant member or Admin; bounded radius/freshness, no coordinates |
| POST /orders/{id}/pickup | Current courier and assignmentId; persisted pickup deadline |
| POST /orders/{id}/deliver | Current courier and assignmentId; valid state |
| POST /orders/{id}/release-assignment | Current courier or Admin, same fence, unpicked only |
| GET /orders/{id}/eta | Existing participant policy; approximate result, no destination geometry |
| PUT/DELETE /identity/users/{userId}/roles/{role} | Live Admin manages other users' RestaurantOwner/RestaurantStaff/Courier only |
| PUT/DELETE /restaurants/{restaurantId}/memberships/{userId} | Current scoped Owner, eligible Owner/Staff target for grant |

No HTTP Admin-role administration, unrestricted self-promotion, public directory or assignment-selection endpoint exists. A target user shares their own ID from /auth/me for onboarding. Role grants require a fresh login before new token claims appear; revoked privileged roles are removed from existing requests. Membership changes never grant system roles. Repeated PUT/DELETE is naturally idempotent and produces audit only for an actual change.

Lists accept page1..100000 and pageSize1..50. Stable identifier ties are used; order lists sort by creation time, courier assignments by assignment time. Deep OFFSET cost remains a documented limit. Existing courier capacity permits one active assignment.

## Create example and replay

```json
{"restaurantId":"00000000-0000-0000-0000-000000000001","items":[{"productId":"00000000-0000-0000-0000-000000000101","quantity":2}],"latitude":24.72,"longitude":46.68}
```

Coordinates are required, latitude[-90,90], longitude[-180,180]; zero is valid. Prices, customerId, status and assignment input are rejected. Catalog price/name/availability is rechecked under locks. Same customer/key/canonical request replays201 with the same ID; changed request under that key conflicts409. Keys are1..100 non-control characters and currently retained indefinitely.

Creation authorization decline returns402 without an order. Accept permanent capture decline returns409 and replays the declined provider result. Confirmed transient failure returns503 and remains retryable before the original deadline. Unknown outcome returns503 and reconciles the stable operation; it is not a confirmed permanent failure. Late capture never permits expired acceptance. Compensation is asynchronous and durable.

## Errors and transport policy

Success:200/201/204. Invalid input400; missing/invalid authentication401; forbidden role403; private foreign/missing resource404; state/concurrency/replay/fence conflict409; throttling429; dependency failure503. Unexpected errors are generic500. Request-abort handling does not emit a false500/body. ProblemDetails excludes internal exceptions and private values.

Read/write/create/hub limits and explicit browser origins are described in [security](06_SECURITY_AND_RELIABILITY.md). OpenAPI /openapi/v1.json is available only in Development/Testing. Payment responses are explicitly mock; there is no real-money contract.

## Realtime

Authenticated /hubs/orders supports SubscribeOrder(orderId) and UnsubscribeOrder(orderId), not arbitrary group joins. Subscription returns an order snapshot and optional authorized current courier location. OrderUpdated/CourierLocationUpdated are sequence/observation checked. Destination coordinates never appear. Refresh/resubscribe on reconnect; notifications are best effort. Live roles/membership/assignment and token expiry are rechecked for subscribe/send.

## Operations and events

Anonymous /health/live and /health/ready expose only Healthy/Unavailable. Live-Admin /operations/summary, /runtime, /outbox and /recovery-failures return bounded payload-free diagnostics. POST /operations/outbox/{id}/retry, /orders/{id}/retry-dispatch and /recovery/{operation}/{id}/retry require Idempotency-Key and are audited. Retry never bypasses payment/state/fence rules. Historical dead Outbox rows are paged and retried individually; unfiltered bulk revival is not implemented.

Integration envelopes contain MessageId, Type, Version, OccurredAtUtc, AggregateId, optional aggregate sequence/correlation/traceparent and minimal typed payload. Order types include order.created.v1, order.accepted.v1, order.courier-assigned.v1, order.assignment-released.v1, order.picked-up.v1, order.delivered.v1 and order.cancelled.v1; consult IntegrationEventTypes for exact names. Financial types archive authorized/captured/failed/uncertain/voided/refunded snapshots. Broker delivery is at least once; consumers deduplicate and never infer global arrival order. Precise destination/courier coordinates are absent from unrestricted envelopes.
