# Architecture decisions

These decisions describe current implementation. Historical rollout notes have been consolidated; executable tests and [release verification](16_RELEASE_VERIFICATION.md) provide evidence.

| ADR | Decision and consequence |
|---|---|
| 001 | Modular monolith with four Clean Architecture projects; shared deployment/schema, explicit feature boundaries |
| 002 | Order aggregate owns immutable item snapshots; no independent item mutation repository |
| 003 | PostgreSQL/EF Core/Npgsql and real PostGIS geography; no EF InMemory substitute for relational proof |
| 004 | Trusted SAR decimal snapshots, overflow/precision limits, never client prices or floating-point money |
| 005 | DB concurrency, atomic claims and fences complement local Domain guards |
| 006 | Transactional Outbox/Inbox, at-least-once delivery and idempotent effects |
| 007 | Durable mock payments and approximate ETA without external payment/map dependencies |
| 008 | Authorize on create, capture before acceptance, void/refund on cancellation after provider reconciliation |
| 009 | Direct courier auto-assignment; no offer marketplace or courier acceptance stage |
| 010 | ASP.NET Core Identity and short-lived JWTs, no refresh tokens; privileged claims now intersect live grants per031 |
| 011 | PostgreSQL xmin shadow concurrency token; no persistence version in Domain |
| 012 | RabbitMQ.Client directly, async confirms/manual ACK, durable topology and minimal versioned envelopes |
| 013 | Explicit Domain events/timestamps and DomainException; existing state/reason/fence/pricing rules preserved |
| 014 | PostgreSQL numeric with positivity/range/scale checks preserves the full supported decimal range; original migration unchanged |
| 015 | Private required destination coordinates, simple catalog availability and persisted UTC acceptance deadline |
| 016 | Atomic trusted order creation, locked snapshot revalidation and durable actor/key/fingerprint replay |
| 017 | Stable durable provider operations distinguish permanent decline, confirmed transient failure and uncertain capture; late success cannot accept an expired order |
| 018 | PostGIS metres/freshness, courier identity-bound observation ordering and intentional aggregate/spatial projection |
| 019 | Leased confirmed publication and confirmed retry-copy-before-ACK; transient Outbox exhaustion superseded by028 |
| 020 | Order -> Courier locks, unique active capacity, assignment fencing and one transaction for dispatch/history/audit/Inbox/Outbox |
| 021 | Persisted deadlines/retries and isolated recovery failures; no new order states for scheduling exhaustion |
| 022 | Private live-authorized SignalR snapshots and approximate ETA; one realtime API instance |
| 023 | Patched pinned .NET10 SDK/runtime baseline and verified dependency maintenance |
| 024 | Private telemetry, bounded operational diagnostics, audited retries and isolated recovery failures |
| 025 | Explicit repeat-safe Development bootstrap and owned disposable migration/demo verification |
| 026 | Immutable financial archive/Inbox, measured worker health and allowlisted safe incidents; historical facts do not mutate payment authority |
| [027](adr/ADR-027-hardening.md) | GPS checkpoint, conservative retention, focused shared-transaction collaborators and retained clock/exception contracts |
| 028 | Broker outages never consume a permanent Outbox budget; capture learned during cancellation stages captured fact/attempt before refund |
| 029 | Measured partial Created/Accepted recovery indexes; bounded per-actor HTTP/hub admission, no speculative dispatch rewrite |
| 030 | Explicit mock acknowledgement outside local/test, exact hosts/HTTPS issuer/HSTS and coherent initialized-store credential rotation |
| 031 | Restricted live-Admin non-Admin grants, scoped current-Owner memberships, ordered authorization locks, exact browser origins and active courier listing |

## Financial consistency

Provider and local commits are intentionally independent. Stable operation keys and provider status reconciliation prevent duplicate charges after lost responses. A cancellation fact remains a durable compensation obligation. If settlement first discovers capture, its capture snapshot and succeeded attempt precede the refund snapshot in the same local save/Inbox transaction. Payment history is immutable and deduplicated; broker ordering is not assumed.

Permanent capture decline replays409. Confirmed transient failure returns503 and remains retryable before the original deadline. Unknown outcome reconciles; it is not exposed as permanent failure. Late capture persists financial evidence for compensation but cannot accept/dispatch an expired order.

## Concurrency and retry

Order transitions retain original xmin. OrderTransitionEffects shares its context/transaction and preserves courier -> job -> history sequencing. Candidate selection retains the proven geography/locking SQL; locked-nearest and after-snapshot availability tests disprove the proposed under-return risk. Claims and completions require assignment/lease fences and DB constraints.

Transient publication failures use capped2..300s jitter indefinitely; invalid envelopes dead-letter. Confirmed publish with failed DB completion leaves the lease. Existing live-Admin paged diagnostics and audited per-message replay support historical dead rows. An unfiltered bulk revival was rejected because eligibility/repair must remain explicit and no measured operational need justifies a new endpoint.

## Identity, browser and private data

Only current Admin manages Owner/Staff/Courier of other users; no HTTP Admin/Customer administration or self-promotion. Current Owner plus restaurant membership may manage eligible members only in that restaurant. Ownership follows the existing global-role+membership model; a new per-membership role was not invented. Sorted actor/target locks then restaurant lock serialize authority changes with audit. Stale membership removal remains possible after its user's system role is revoked.

Privileged JWT grants are only reduced against live state, never expanded. Membership/assignment checks remain resource-scoped. CORS is exact-origin and HTTPS outside local/test; WebSocket Origin is independently checked. Customer destinations never appear in public order/events/ETA. Courier coordinates are authorized current snapshots, with replay/sequence checks and assignment-aware throttling.

## Operations and capacity

Liveness is process-only. Readiness verifies dependencies, migrations and actual worker progress; dead backlog remains an alert input, not a restart loop. Metrics observations have timestamps and do not invent zeros after failure. Ready-message counts exclude unacked broker deliveries.

Created/Accepted partial indexes follow actual100k/300k history plans. Other exact counts/cancellation-history scans remain measured demo-scale limits; no production SLA or guessed index is claimed. Request/Inbox identities remain indefinite; cleanup only eligible published Outbox after7 days, resolved recovery failures after30 and finished-order dispatch attempts after30, with concurrent eligibility and consumption guards.

Keep IClock as TimeProvider wrapper and ArgumentException order-limit contracts. Retain one API realtime instance, durable mock payments and heuristic ETA. Real money, SignalR replicas, production-scale capacity and an approved finite replay horizon need separate engineering and deployment validation.
