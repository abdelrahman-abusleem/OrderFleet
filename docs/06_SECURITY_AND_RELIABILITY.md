# Security and reliability

## Identity and resource access

JWT validation requires HS256, signature, issuer, audience, expiry and a nonempty GUID subject. Signing material must contain at least32 bytes; token lifetime is1..60 minutes (default15) with30 seconds skew. No refresh-token service is implemented. Public registration grants Customer only; login uses ASP.NET Core Identity password hashing, five-failure lockout and a10/IP/minute credential limit.

Every privileged token role is intersected with its authoritative current DB grants. Revoked Admin/Owner/Staff/Courier claims cannot authorize subsequent requests; adding a grant never expands an old token. Sign in again to acquire new claims. Requests already authorized can be in flight; identity-administration transactions recheck authority after ordered actor/target locks to close the mutation race.

Only current Admin manages other users' Owner/Staff/Courier roles. No HTTP Admin or Customer role administration exists. Current Owner plus restaurant membership manages eligible restaurant members in that restaurant. An Owner system role is global; membership scopes it. Owners cannot change system roles or their own membership. Mutations and identifier-only audits share a transaction; repeated requests do not duplicate audit effects. Non-public provisioning/revocation must use the same user-lock discipline.

Order reads check customer identity, courier assignment or live restaurant membership; Admin access is scoped and audited. Foreign private resources generally return404. Commands derive cancellation reasons and assignment identity from server policy. A role alone never authorizes a foreign restaurant/order.

## Browser, network and deployment

CORS defaults to no cross-origin grants. Configure at most20 exact approved origins through Cors:AllowedOrigins; HTTPS is required outside Development/Testing. No wildcards, credentials in URLs, paths or queries are accepted. SignalR separately rejects a supplied foreign Origin on negotiate/WebSocket requests; same-origin and authenticated nonbrowser clients remain supported.

Default AllowedHosts is localhost/127.0.0.1. Non-local environments require explicit demo acknowledgement, valid non-wildcard hosts and HTTPS JWT issuer. Payments remain a mock, labeled by X-OrderFleet-Payments:mock. HSTS and HTTPS redirection apply outside Development/Testing; deploy behind correctly configured TLS termination. This repository has no real payment adapter.

Forwarded headers are disabled by default. Enabled configuration requires explicit proxy IPs/CIDRs with no /0; one hop is processed. Trusted forwarding precedes logging/auth/limits. Enforce network isolation and correct proxy headers; do not enable an independent trust-all override.

RequestLimits defaults per actor/minute: create10, write60, read120 and hub invocations60. Route casing does not bypass create limits. Hub invocation budget spans connections; existing connection/subscription/message-size caps remain. Anonymous partitions use the trusted socket/forwarded client IP. These are single-instance limits; production edge/DDoS controls require separate validation.

## Privacy and secrets

Private destination coordinates are persisted separately and exposed only through authorized delivery details. Public order/event/ETA responses exclude them. Precise courier notification coordinates require a current authorized active assignment and live subscription checks. Identical GPS replay does not create another event.

SafeLogSink allows approved templates/properties and reduces unknown warnings/errors to safe incident metadata. Trace/request processing excludes query credentials, bodies, SQL text, baggage and private coordinates. Keep .env, key files, local settings and build artifacts untracked and out of Docker contexts. Review history as well as HEAD before publication.

Changing .env does not rotate initialized database/broker credentials. Rotate the stored credentials, demo hashes and JWT configuration coherently; verify new access and old rejection while preserving records and volumes. Never print resolved Compose configuration, full container environments, tokens or password-bearing responses. The identified local archive exposure was remediated as recorded in [release verification](16_RELEASE_VERIFICATION.md); unknown external credential reuse is not certified.

## Transactions and delivery

Business changes, audit and Outbox commit together. DB xmin/unique constraints complement Domain guards; order→courier lock order and AssignmentId fences protect concurrent assignments. Broker publication happens after commit with mandatory routing and confirms. Completion/failure writes require the same live lease token/deadline.

Broker unavailability keeps durable retries with2..300-second bounded exponential jitter; it does not permanently dead-letter valid events. Invalid envelopes dead-letter for repair. A confirmed publish followed by DB completion failure retains its lease for at-least-once recovery. Consumers commit effects and Inbox before ACK; confirmed retry-copy publication precedes ACK of the original. Recovery never silently deletes a financial/business fact.

HTTP create and processed Inbox identities remain indefinitely. Cleanup is bounded and consumption-safe: eligible published Outbox older than7 days, resolved recovery failures older than30, finished-order dispatch attempts older than30. Pending/unresolved work, orders, payments, financial identities and audit remain protected. Exact replay/retention policy needs separate production approval before pruning identities.

Public liveness reports process state; readiness checks dependencies, migration state and actual worker progress. Backlog/dead alerts are independent of readiness. Operators have live-Admin, payload-free diagnostics and audited bounded replay. Monitor backlog age/counts, retry reasons, failure timestamps and storage; operational success is not established by a green test alone.
