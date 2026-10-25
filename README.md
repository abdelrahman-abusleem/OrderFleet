# OrderFleet

A .NET 10 restaurant ordering and courier dispatch backend, built as a modular monolith with Clean Architecture. This is a production-oriented learning project under active implementation.

## Current status

2026-10-25: baseline audited; approved domain additions implemented. Release build passes; 234 Domain tests, 29 PostgreSQL tests and 4 HTTP error-boundary tests pass (plus one retained Integration placeholder). Migration CLI verified against a disposable database. PostgreSQL xmin conflict/stale-fence protection and safe HTTP 409 mapping pass. Identity is next; catalog, payments, dispatch, messaging, realtime, ETA and observability remain required and are not implemented yet.

This folder arrived without Git metadata. The owner authorized local Git initialization after parent-repository and credential checks. No remote is configured and no remote CI run has been verified.

## Prerequisites and verification

- .NET 10 SDK and Docker with Linux containers.
- Run `dotnet restore OrderFleet.slnx`, `dotnet build OrderFleet.slnx -c Release`, then `dotnet test OrderFleet.slnx -c Release`.
- Domain tests need no infrastructure: `dotnet test tests/OrderFleet.Domain.Tests -c Release`.
- Real integration tests provision disposable PostgreSQL through Testcontainers; never point tests at the developer database.
- After a Release build, `pwsh ./scripts/Verify-Migrations.ps1` checks the EF migration CLI using a new disposable PostgreSQL container; requires dotnet-ef. It never targets the developer database. CreatedAt cannot be inferred for populated legacy databases; those require a reviewed backfill before upgrading.

## Running locally

The API currently contains the original weather template, not the ordering workflow. Configure `ConnectionStrings:OrderFleet` through User Secrets or the `ConnectionStrings__OrderFleet` environment variable. Never commit database credentials. The existing Compose PostgreSQL container/data volume is preserved; do not run destructive volume cleanup.

See [the engineering contract](docs/00_START_HERE.md), [roadmap](docs/04_EXECUTION_ROADMAP.md), and [verified progress](docs/07_PROGRESS_AND_HANDOFF.md).
