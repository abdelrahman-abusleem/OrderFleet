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
dotnet format whitespace OrderFleet.slnx --verify-no-changes --no-restore
dotnet build OrderFleet.slnx -c Release --no-restore
dotnet test OrderFleet.slnx -c Release --no-build --no-restore
dotnet list OrderFleet.slnx package --vulnerable --include-transitive
```

SDK analyzers run with warnings treated as errors. Integration tests need a reachable Docker daemon and create/dispose their own PostGIS/RabbitMQ containers with random ports/credentials. They do not use your Compose database. `scripts/Verify-Migrations.ps1` additionally applies the full migration chain to a new disposable PostGIS instance; it requires dotnet-ef10 and never targets the developer database. In this workspace a hash-verified patched SDK is available under ignored `.tools/dotnet`; set DOTNET_ROOT/PATH to it if the system SDK is older.

[CI](.github/workflows/ci.yml) restores/builds/checks whitespace and runs Domain tests; a Docker job runs integration tests and clean Compose bootstrap/demo. Local execution evidence is in [progress](docs/07_PROGRESS_AND_HANDOFF.md). Remote CI has not been run or claimed: no remote push is authorized.

## What is implemented

- JWT/Identity roles and ownership, live restaurant membership, controlled registration, lockout/rate limits and private delivery information.
- Catalog snapshots, decimal SAR pricing, complete established Order state/cancellation matrix and assignment fences.
- Idempotent create/payment effects, capture-before-acceptance, permanent/transient/unknown outcome handling and refund/void compensation.
- Real PostGIS radius/freshness queries and atomic courier capacity claims, xmin concurrency and unique active-assignment constraints.
- Transactional Outbox, leased publication, publisher confirms, manual ACK, Inbox dedupe, bounded retry/dead letters and persisted timeout/recovery processing.
- Private SignalR subscriptions, live authority checks, reconnect snapshots, GPS freshness/throttling and explicitly approximate ETA.
- Safe Serilog, W3C/OpenTelemetry HTTP/DB/message/dispatch traces and metrics, bounded readiness probes and audited administrator replay.

## Operational boundaries

One API instance supports realtime; DB claims/leases are separately tested across concurrent contexts/workers. A tested SignalR backplane/fanout design is needed before multiple API replicas. Delivery is at least once, with idempotent business effects; client notifications are best effort with snapshot refresh. ETA uses geography distance and configured speed/buffers, not roads or traffic. Mock payments require a real provider adapter/reconciliation contract before real-money use.

The existing `orderfleet-postgres` service/database on5433 is preserved behind the `legacy` profile. New PostGIS uses a separate volume; do not attach the new image to the old volume. All migration/demo verification targeted disposable databases. Existing populated legacy assignments would require owner-reviewed history/deadline reconciliation before upgrading. No automated production migration, destructive reset, remote push, deployment, HA/SLA certification or completed human review is claimed.

Stop the demo without deleting data:

```powershell
docker compose --project-name orderfleet-demo --profile app stop
```

For a fresh demo, use a new `-ProjectName orderfleet-yourname` and free ports; retain the old volume for review. Do not run unqualified volume deletion. Backup/retention/security-patching guidance and remaining operational decisions are in [the delivery guide](docs/13_DELIVERY_GUIDE.md).

Repository agreement: [AGENTS.md](AGENTS.md), [navigation](docs/00_START_HERE.md), [architecture](docs/02_ARCHITECTURE.md), [ADRs](docs/08_DECISION_LOG.md), [quality gates](docs/05_QUALITY_GATES.md).

Latest final local result (2026-10-26): **402 passed,0 failed,0 skipped** —257 Domain and145 Integration. Strict Release build and whitespace check pass;16 migrations and clean-source Docker bootstrap/demo verified. Human review and remote CI remain external/unverified.
