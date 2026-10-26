# OrderFleet

A .NET10 modular monolith for restaurant orders and courier dispatch. Clean Architecture keeps Order/payment/courier invariants independent of EF, HTTP and RabbitMQ. PostgreSQL17/PostGIS handles durable state and geospatial claims; RabbitMQ carries transactional Outbox facts; SignalR provides authorized live snapshots. Payments use a durable deterministic mock, never real money.

## Run the local demo

Prerequisites: Docker Engine/Desktop with Linux containers and Compose v2, PowerShell7+, and internet access to the pinned public images/NuGet packages. Local source build/tests additionally require SDK10.0.403 or a compatible newer feature band, with ASP.NET/Core10.0.14 or later patched runtime. See global.json. No paid service is required.

From the repository root:

```powershell
./scripts/Bootstrap.ps1
./scripts/Invoke-Demo.ps1
```

Bootstrap builds the API image, generates missing random secrets in ignored `.env`, starts the **orderfleet-demo** app stack, explicitly migrates, explicitly seeds, and waits for readiness. It preserves existing nonempty secrets and data. It does not push images or contact a remote Git repository. API defaults to http://127.0.0.1:8080, PostGIS5434, RabbitMQ5672 and management15672, all loopback only. Change ports in `.env` before first startup if occupied. The script prints the actual API address; pass it as `-BaseUrl` to the demo if changed.

The seed runs only with `--seed-demo` in Development and a private configured `Demo__Password` (20..128 characters). Seed accounts: `customer`, `owner`, `staff`, `courier`, `admin`, `stranger` at `@demo.orderfleet.test`; their password is `DEMO_PASSWORD` in your ignored `.env`. Seed creates one kitchen/meal, owner/staff memberships and an offline courier. It never resets an existing password or promotes an unrelated account with a colliding email. There are no default usable credentials and no public role-grant endpoint. Keep this stack local: Development credentials, HTTP and a shared demo password are not production security settings.

The HTTP demo signs in, publishes fresh courier position, creates a SAR51 order from trusted catalog prices, verifies duplicate replay and authorization denial, captures/accepts, waits for assignment, picks up and delivers. Tokens/password/private coordinates are not printed. OpenAPI JSON is at `/openapi/v1.json` in Development. Follow [the walkthrough and runbook](docs/13_DELIVERY_GUIDE.md) for manual calls, diagrams, failure recovery and maintenance.

## Build and verify

```powershell
dotnet restore OrderFleet.slnx
dotnet format OrderFleet.slnx --verify-no-changes --no-restore
dotnet build OrderFleet.slnx -c Release --no-restore
dotnet test OrderFleet.slnx -c Release --no-build --no-restore
dotnet list OrderFleet.slnx package --vulnerable --include-transitive
```

SDK analyzers run with warnings treated as errors. Integration tests need a reachable Docker daemon and create/dispose their own PostGIS/RabbitMQ containers with random ports/credentials. They do not use your Compose database. `scripts/Verify-Migrations.ps1` additionally applies the full migration chain to a new disposable PostGIS instance; it requires dotnet-ef10 and never targets the developer database. In this workspace a hash-verified patched SDK is available under ignored `.tools/dotnet`; set DOTNET_ROOT/PATH to it if the system SDK is older.

[CI](.github/workflows/ci.yml) restores/builds/checks whitespace and runs Domain tests; a Docker job runs integration tests and clean Compose bootstrap/demo. Local execution evidence is in [progress](docs/07_PROGRESS_AND_HANDOFF.md). Remote CI has not been run or claimed: no remote push is authorized.

Run Domain and infrastructure verification separately when investigating a failure:

```powershell
dotnet test tests/OrderFleet.Domain.Tests -c Release
dotnet test tests/OrderFleet.IntegrationTests -c Release
```

The second project contains real Testcontainers tests and isolated component tests. Docker failure blocks infrastructure verification; those cases must not be skipped or replaced by an in-memory database.

## Maintenance settings

Cleanup starts with the API, drains eligible work in cancellable bounded deletes, and waits between sweeps. Set `Cleanup__Enabled=false` to disable it; test hosts disable it unless explicitly exercising maintenance. Environment keys use double underscores (for example `Cleanup__BatchSize=100`).

| Setting | Default | Valid range / behavior |
|---|---|---|
| Cleanup:BatchSize | 200 | 1..1000 rows per DELETE |
| Cleanup:PollSeconds | 300 | 1..86400 |
| Cleanup:PublishedOutboxDays | 7 | 7..3650; published and durably consumed facts only |
| Cleanup:ResolvedRecoveryDays | 30 | 30..3650; unresolved/reopened failures protected |
| Cleanup:FinishedDispatchDays | 30 | 30..3650; attempt and terminal timestamp must both be older |
| CourierLocationEvents:MinimumDistanceMeters | 25 | 1..5000 metres since last emitted event |
| CourierLocationEvents:MinimumIntervalSeconds | 10 | 1..300 seconds since last emitted event |
| Outbox:BatchSize | 5 | 1..100; must fit publish-timeout/lease budget |
| Recovery:PollMilliseconds | 1000 | 100..10000; reset after work |
| Recovery:MaxIdlePollMilliseconds | 10000 | base..30000; idle backoff cap |

Cutoffs are strict: a row exactly at its retention age remains. Pending/unpublished/leased work and outstanding cancellation compensation are protected. Financial facts require durable archive evidence before Outbox removal. Request idempotency records, processed Inbox identities, financial audit/deduplication identities, order/operation audits, active assignments, payments and orders remain indefinitely. This preserves the existing unbounded replay contract; production needs a separately approved retention/replay policy before those identities can expire. See [ADR-027](docs/adr/ADR-027-hardening.md) and [query-plan evidence](docs/evidence/recovery-query-plans-before.txt).

Every accepted GPS update still persists the Courier profile and spatial projection atomically. An identical normalized replay writes neither. Only assigned couriers emit location integration events, on movement >=25m OR elapsed >=10s by default; a new assignment starts a new checkpoint. Coordinates stay private and are absent from public event payloads. Threshold suppression can delay location notifications; authorized snapshots use current persisted state.

Forwarded headers are disabled by default. Behind a controlled proxy, enable `TrustedProxy__Enabled` and configure a `KnownProxies` IP or `KnownNetworks` CIDR array, such as `TrustedProxy__KnownProxies__0`. One hop is processed; empty enabled trust, malformed entries and /0 ranges fail startup. Untrusted X-Forwarded-For cannot change rate-limit partitions. See [security details](docs/06_SECURITY_AND_RELIABILITY.md).

## What is implemented

- JWT/Identity roles and ownership, live restaurant membership, controlled registration, lockout/rate limits and private delivery information.
- Catalog snapshots, decimal SAR pricing, complete established Order state/cancellation matrix and assignment fences.
- Idempotent create/payment effects, capture-before-acceptance, permanent/transient/unknown outcome handling and refund/void compensation.
- Real PostGIS radius/freshness queries and atomic courier capacity claims, xmin concurrency and unique active-assignment constraints.
- Transactional Outbox, leased publication, publisher confirms, manual ACK, Inbox dedupe, automatic typed financial archive, bounded retry/dead letters and persisted timeout/recovery processing.
- Private SignalR subscriptions, live authority checks, reconnect snapshots, GPS freshness/throttling and explicitly approximate ETA.
- Safe Serilog, W3C/OpenTelemetry HTTP/DB/message/dispatch traces and metrics, measured worker-progress readiness, broker backlog diagnostics and audited administrator replay.

## Operational boundaries

Catalog listing uses OFFSET with page1..100000 and pageSize1..50. Deep pages can be expensive; validation bounds are not a performance guarantee. Keyset pagination is outside this pass. Payments remain a durable mock; duplicate registration retains a documented timing tradeoff. Public health routes are `/health/live` and `/health/ready`; there is no `/health` alias.

One API instance supports realtime; DB claims/leases are separately tested across concurrent contexts/workers. A tested SignalR backplane/fanout design is needed before multiple API replicas. Delivery is at least once, with idempotent business effects; client notifications are best effort with snapshot refresh. ETA uses geography distance and configured speed/buffers, not roads or traffic. Mock payments require a real provider adapter/reconciliation contract before real-money use.

For realtime scale-out, plan a Redis SignalR backplane (or equivalent), load-balancer affinity for the current negotiated transports, and delivery of each relevant fact to every replica owning subscribed connections. A competing consumer on the current single realtime queue would miss connections on other replicas; adding a backplane alone does not distribute the local authorization/subscription registry. Choose and test per-replica fanout with local live authorization, or a distributed registry with explicit revocation and sequence semantics. Verify multi-replica reconnect, stale-event suppression, role/membership revocation, token expiry, private location filtering and broker/backplane outages before enabling replicas. This upgrade remains future work; no backplane is implemented in this pass.

The existing `orderfleet-postgres` service/database on5433 is preserved behind the `legacy` profile. New PostGIS uses a separate volume; do not attach the new image to the old volume. All migration/demo verification targeted disposable databases. Existing populated legacy assignments would require owner-reviewed history/deadline reconciliation before upgrading. No automated production migration, destructive reset, remote push, deployment, HA/SLA certification or completed human review is claimed.

Stop the demo without deleting data:

```powershell
docker compose --project-name orderfleet-demo --profile app stop
```

For a fresh demo, use a new `-ProjectName orderfleet-yourname` and free ports; retain the old volume for review. Do not run unqualified volume deletion. Backup/retention/security-patching guidance and remaining operational decisions are in [the delivery guide](docs/13_DELIVERY_GUIDE.md).

Repository agreement: [AGENTS.md](AGENTS.md), [navigation](docs/00_START_HERE.md), [architecture](docs/02_ARCHITECTURE.md), [ADRs](docs/08_DECISION_LOG.md), [quality gates](docs/05_QUALITY_GATES.md).

Infrastructure test collections run with at most two parallel xUnit threads to bound independent PostGIS/RabbitMQ fixture pressure on shared Docker resources. Tests still run their own concurrent database sessions, workers and barrier-controlled races. No test timeout or assertion is relaxed. See [xUnit runner configuration](https://xunit.net/docs/config-xunit-runner-json).

Hardening completed through Phase5 (2026-10-26): **520 passed,0 failed,0 skipped** —260 Domain and260 Integration. Strict Release, full formatting/analyzers, model drift, fresh19-migration chain and owned Compose HTTP/broker/database smoke pass. Two additive migrations support GPS checkpoints and the measured pending-capture index; the original17 migrations remain unchanged. Final commands, review, failed-run history and resource-bound runner verification are recorded in [the current report](docs/15_HARDENING_PASS.md). [The earlier Phase13 review](docs/14_HARDENING_REVIEW.md) retains its historical428-test evidence. Remote CI remains **PENDING REMOTE VERIFICATION**; owner merge/deploy review is external.
