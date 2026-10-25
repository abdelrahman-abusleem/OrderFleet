# OrderFleet

A .NET 10 restaurant ordering and courier dispatch backend, built as a modular monolith with Clean Architecture. This is a production-oriented learning project under active implementation.

## Current status

2026-10-25: Domain, PostgreSQL persistence/concurrency, Identity/JWT, trusted catalog, private delivery information, idempotent ordering, persisted acceptance deadlines and transactional audit are implemented. Release build passes; 245 Domain + 70 Integration tests pass (includes one retained historical placeholder). Seven migrations verified against disposable PostgreSQL. Mock payments are next; dispatch, messaging, recovery, realtime, ETA and observability remain required and are not implemented yet.

This folder arrived without Git metadata. The owner authorized local Git initialization after parent-repository and credential checks. No remote is configured and no remote CI run has been verified.

## Prerequisites and verification

- .NET 10 SDK and Docker with Linux containers.
- Run `dotnet restore OrderFleet.slnx`, `dotnet build OrderFleet.slnx -c Release`, then `dotnet test OrderFleet.slnx -c Release`.
- Domain tests need no infrastructure: `dotnet test tests/OrderFleet.Domain.Tests -c Release`.
- Real integration tests provision disposable PostgreSQL through Testcontainers; never point tests at the developer database.
- After a Release build, `pwsh ./scripts/Verify-Migrations.ps1` checks the EF migration CLI using a new disposable PostgreSQL container; requires dotnet-ef. It never targets the developer database. CreatedAt cannot be inferred for populated legacy databases; those require a reviewed backfill before upgrading.

## Running locally

Configure `ConnectionStrings:OrderFleet`, `Jwt:Issuer`, `Jwt:Audience` and `Jwt:SigningKey` through User Secrets or environment variables (double underscores replace colons). The signing key must contain at least 32 random bytes; no usable default is supplied. Tokens default to 15 minutes with 30 seconds of clock skew; no refresh tokens. Public registration grants Customer only. Privileged demo provisioning comes later; never expose a client-selected role.

Current endpoints include register/login/me, public restaurant/product browse, authorized catalog writes, idempotent order create/get/list, restaurant accept/reject, actor-derived cancel and private delivery reads. See [implemented request contracts](docs/11_API_DATA_CONTRACTS.md). OpenAPI is available at `/openapi/v1.json` in Development. Order reads require actor ownership/membership/current courier assignment; unauthorized resources use privacy-aware 404. Credentials are rate-limited; five failed logins lock the account for 15 minutes. Role revocation takes effect at token expiry; restaurant membership is checked live. Email verification, refresh tokens and production identity governance are not claimed.

Order create requires a valid destination and `Idempotency-Key`. Names/prices come only from the restaurant catalog. `Ordering:AcceptanceTimeoutMinutes` defaults to 5 and is frozen per order; expired accept returns 409 even before the future worker processes cancellation. Financial authorization/capture/compensation are the next mandatory phase and are not yet present in current create/accept.

The original weather template remains but is protected by deny-by-default authentication. The complete ordering workflow is under implementation. Never commit database credentials. Existing Compose PostgreSQL data is preserved; do not run destructive volume cleanup. All tested migrations have been applied only to disposable databases, not the developer database.

See [the engineering contract](docs/00_START_HERE.md), [roadmap](docs/04_EXECUTION_ROADMAP.md), and [verified progress](docs/07_PROGRESS_AND_HANDOFF.md).
