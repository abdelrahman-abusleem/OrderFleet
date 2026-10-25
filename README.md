# OrderFleet

A .NET 10 restaurant ordering and courier dispatch backend, built as a modular monolith with Clean Architecture. This is a production-oriented learning project under active implementation.

## Current status

2026-10-25: Domain, PostgreSQL persistence/concurrency, Identity/JWT, trusted catalog, private delivery information, idempotent ordering, persisted acceptance deadlines and transactional audit are implemented. Release build passes; 257 Domain + 119 Integration tests pass (includes one retained historical placeholder). Thirteen migrations verified against disposable PostgreSQL. Durable mock payments, capture retries/reconciliation and transactional event persistence are implemented; courier availability/location and PostGIS discovery are implemented; confirmed RabbitMQ delivery, fenced Outbox leases and idempotent payment consumption are implemented; atomic dispatch and fenced courier pickup/delivery/release are implemented; persisted timeout/retry/financial recovery is implemented; authorized SignalR updates and approximate ETA are implemented; observability/operational hardening and final delivery verification remain required.

This folder arrived without Git metadata. The owner authorized local Git initialization after parent-repository and credential checks. No remote is configured and no remote CI run has been verified.

## Prerequisites and verification

- .NET SDK10.0.403 or later compatible10.0 feature band, ASP.NET/Core runtime10.0.14 or later patch, and Docker with Linux containers. global.json and Directory.Build.props enforce the patched baseline.
- Run `dotnet restore OrderFleet.slnx`, `dotnet build OrderFleet.slnx -c Release`, then `dotnet test OrderFleet.slnx -c Release`.
- Domain tests need no infrastructure: `dotnet test tests/OrderFleet.Domain.Tests -c Release`.
- Real integration tests provision disposable PostgreSQL 17 + PostGIS 3.5 through Testcontainers; never point tests at the developer database.
- After a Release build, `pwsh ./scripts/Verify-Migrations.ps1` checks the EF migration CLI using a new disposable PostGIS container; requires dotnet-ef. It never targets the developer database. CreatedAt cannot be inferred for populated legacy databases; those require a reviewed backfill before upgrading.

## Running locally

Configure `ConnectionStrings:OrderFleet`, `Jwt:Issuer`, `Jwt:Audience` and `Jwt:SigningKey` through User Secrets or environment variables (double underscores replace colons). The signing key must contain at least 32 random bytes; no usable default is supplied. Tokens default to 15 minutes with 30 seconds of clock skew; no refresh tokens. Public registration grants Customer only. Privileged demo provisioning comes later; never expose a client-selected role.

Current endpoints include register/login/me, public restaurant/product browse, authorized catalog writes, idempotent order create/get/list, restaurant accept/reject, actor-derived cancel and private delivery reads. See [implemented request contracts](docs/11_API_DATA_CONTRACTS.md). OpenAPI is available at `/openapi/v1.json` in Development. Order reads require actor ownership/membership/current courier assignment; unauthorized resources use privacy-aware 404. Credentials are rate-limited; five failed logins lock the account for 15 minutes. Role revocation takes effect at token expiry; restaurant membership is checked live. Email verification, refresh tokens and production identity governance are not claimed.

Order create requires a valid destination and `Idempotency-Key`. Names/prices come only from the restaurant catalog. `Ordering:AcceptanceTimeoutMinutes` defaults to 5 and is frozen per order; expired accept returns 409 even before the future worker processes cancellation. Create authorizes and acceptance captures against a durable mock ledger. Permanent capture declines return 409; transient and uncertain results return 503 with safe same-operation retry. Cancellation settlement consumer voids/refunds idempotently; broker transport is implemented; persisted timeout and compensation recovery workers are implemented. MockPayments:AuthorizeOutcome supports Success/Declined/Transient; CaptureOutcome adds Unknown to simulate a committed charge with a lost response. This mock never charges real money.

The original weather template remains but is protected by deny-by-default authentication. The complete ordering workflow is under implementation. Never commit database credentials. Existing Compose PostgreSQL data is preserved; do not run destructive volume cleanup. All tested migrations have been applied only to disposable databases, not the developer database.

See [the engineering contract](docs/00_START_HERE.md), [roadmap](docs/04_EXECUTION_ROADMAP.md), and [verified progress](docs/07_PROGRESS_AND_HANDOFF.md).

Courier/spatial setup uses a PostGIS-capable database: `docker compose --profile spatial up -d postgis` provisions a separate database/volume on loopback5434. Configure the API connection to this new database before applying migrations. The legacy postgres:17 service on5433 remains preserved; it cannot apply the spatial migration without PostGIS installed. Never attach the new image to the legacy volume or run destructive volume cleanup. Courier endpoints use authenticated own identity; nearby discovery requires restaurant authority. See ADR-018 for radius, timestamp freshness and privacy policy.

Messaging defaults enabled. Configure Messaging__Uri through User Secrets/environment, e.g. an amqp URI containing your local credentials, never in tracked settings. `docker compose --profile messaging up -d rabbitmq` uses the ignored .env RABBITMQ_PASSWORD and loopback ports5672/15672. Messaging:Enabled=false explicitly pauses transport for isolated component tests/maintenance; it does not make the workflow operationally complete. Retry/DLQ, lease settings and event routes are documented in ADR-019. Real RabbitMQ integration tests need Docker and start their own broker; no local service credentials are reused.

Local verification in this workspace uses the ignored .tools/dotnet SDK10.0.403/runtime10.0.14 (official archive SHA512 verified); the system SDK was preserved. If your PATH still selects older .NET, use `./.tools/dotnet/dotnet.exe` or install the patched SDK. Migration audit detects this local Windows SDK automatically and restores its process environment afterward. CI installs the SDK from global.json. See ADR-023 for the SignalR security patch evidence.

SignalR /hubs/orders supports private owned subscriptions with live role/membership checks, expiry closure and reconnect snapshot refresh. GET /api/v1/orders/{id}/eta labels its approximation and stale-location fallback. This demo supports one API instance for realtime; multi-worker DB concurrency is separately verified. See ADR-022 for privacy and scale boundaries.

Phase11 local gate passed:257 Domain +138 Integration,0 failed/skipped,16 migrations verified. Operations/health/telemetry/container policies are in ADR-024 and API contracts. Delivery/bootstrap/E2E documentation is the next required phase.
