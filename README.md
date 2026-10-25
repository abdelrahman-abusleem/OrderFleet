# OrderFleet

A .NET 10 restaurant ordering and courier dispatch backend, built as a modular monolith with Clean Architecture. This is a production-oriented learning project under active implementation.

## Current status

2026-10-25: Domain, PostgreSQL persistence/concurrency, Identity/JWT, trusted catalog, private delivery information, idempotent ordering, persisted acceptance deadlines and transactional audit are implemented. Release build passes; 256 Domain + 92 Integration tests pass (includes one retained historical placeholder). Ten migrations verified against disposable PostgreSQL. Durable mock payments, capture retries/reconciliation and transactional event persistence are implemented; courier availability/location and PostGIS discovery are implemented; confirmed RabbitMQ delivery, fenced Outbox leases and idempotent payment consumption are implemented; dispatch, recovery, realtime, ETA and observability remain required and are not implemented yet.

This folder arrived without Git metadata. The owner authorized local Git initialization after parent-repository and credential checks. No remote is configured and no remote CI run has been verified.

## Prerequisites and verification

- .NET 10 SDK and Docker with Linux containers.
- Run `dotnet restore OrderFleet.slnx`, `dotnet build OrderFleet.slnx -c Release`, then `dotnet test OrderFleet.slnx -c Release`.
- Domain tests need no infrastructure: `dotnet test tests/OrderFleet.Domain.Tests -c Release`.
- Real integration tests provision disposable PostgreSQL 17 + PostGIS 3.5 through Testcontainers; never point tests at the developer database.
- After a Release build, `pwsh ./scripts/Verify-Migrations.ps1` checks the EF migration CLI using a new disposable PostGIS container; requires dotnet-ef. It never targets the developer database. CreatedAt cannot be inferred for populated legacy databases; those require a reviewed backfill before upgrading.

## Running locally

Configure `ConnectionStrings:OrderFleet`, `Jwt:Issuer`, `Jwt:Audience` and `Jwt:SigningKey` through User Secrets or environment variables (double underscores replace colons). The signing key must contain at least 32 random bytes; no usable default is supplied. Tokens default to 15 minutes with 30 seconds of clock skew; no refresh tokens. Public registration grants Customer only. Privileged demo provisioning comes later; never expose a client-selected role.

Current endpoints include register/login/me, public restaurant/product browse, authorized catalog writes, idempotent order create/get/list, restaurant accept/reject, actor-derived cancel and private delivery reads. See [implemented request contracts](docs/11_API_DATA_CONTRACTS.md). OpenAPI is available at `/openapi/v1.json` in Development. Order reads require actor ownership/membership/current courier assignment; unauthorized resources use privacy-aware 404. Credentials are rate-limited; five failed logins lock the account for 15 minutes. Role revocation takes effect at token expiry; restaurant membership is checked live. Email verification, refresh tokens and production identity governance are not claimed.

Order create requires a valid destination and `Idempotency-Key`. Names/prices come only from the restaurant catalog. `Ordering:AcceptanceTimeoutMinutes` defaults to 5 and is frozen per order; expired accept returns 409 even before the future worker processes cancellation. Create authorizes and acceptance captures against a durable mock ledger. Permanent capture declines return 409; transient and uncertain results return 503 with safe same-operation retry. Cancellation settlement consumer voids/refunds idempotently; broker transport is implemented; timeout recovery workers remain forthcoming. MockPayments:AuthorizeOutcome supports Success/Declined/Transient; CaptureOutcome adds Unknown to simulate a committed charge with a lost response. This mock never charges real money.

The original weather template remains but is protected by deny-by-default authentication. The complete ordering workflow is under implementation. Never commit database credentials. Existing Compose PostgreSQL data is preserved; do not run destructive volume cleanup. All tested migrations have been applied only to disposable databases, not the developer database.

See [the engineering contract](docs/00_START_HERE.md), [roadmap](docs/04_EXECUTION_ROADMAP.md), and [verified progress](docs/07_PROGRESS_AND_HANDOFF.md).

Courier/spatial setup uses a PostGIS-capable database: `docker compose --profile spatial up -d postgis` provisions a separate database/volume on loopback5434. Configure the API connection to this new database before applying migrations. The legacy postgres:17 service on5433 remains preserved; it cannot apply the spatial migration without PostGIS installed. Never attach the new image to the legacy volume or run destructive volume cleanup. Courier endpoints use authenticated own identity; nearby discovery requires restaurant authority. See ADR-018 for radius, timestamp freshness and privacy policy.

Messaging defaults enabled. Configure Messaging__Uri through User Secrets/environment, e.g. an amqp URI containing your local credentials, never in tracked settings. `docker compose --profile messaging up -d rabbitmq` uses the ignored .env RABBITMQ_PASSWORD and loopback ports5672/15672. Messaging:Enabled=false explicitly pauses transport for isolated component tests/maintenance; it does not make the workflow operationally complete. Retry/DLQ, lease settings and event routes are documented in ADR-019. Real RabbitMQ integration tests need Docker and start their own broker; no local service credentials are reused.
