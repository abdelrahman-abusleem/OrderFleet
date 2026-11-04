# Testing and validation

Prerequisites: the SDK selected by global.json, Docker Linux containers and PowerShell7 for scripts. Tests use real disposable PostgreSQL/PostGIS and RabbitMQ with random credentials/ports. They never target a developer database. No EF InMemory replacement or skipped-infrastructure success is supported.

```powershell
dotnet restore OrderFleet.slnx
dotnet tool restore
dotnet build OrderFleet.slnx -c Release --no-restore -m:1 -warnaserror
dotnet format OrderFleet.slnx --verify-no-changes --no-restore --verbosity quiet
dotnet test OrderFleet.slnx -c Release --no-build --no-restore -m:1
```

The latest complete local regression recorded in [release verification](16_RELEASE_VERIFICATION.md) passed260 Domain and302 Integration cases,0 failed/skipped. Integration collections are capped at two concurrent threads to bound shared-Docker fixture pressure. Explicit competing sessions, workers, barriers and lock witnesses within each test remain concurrent. The runner treats skips as failures.

## What the suites establish

| Concern | Representative tests |
|---|---|
| Aggregate invariants, state/reason matrix, rejection atomicity, exact money | Domain OrderCreation/OrderItem/OrderStateMachine/OrderCancellation/OrderPricingLimits tests |
| Complete payment history and compensation | CaptureCancellationRaceTests, PaymentWorkflowTests, FinancialArchiveTests |
| Prolonged broker outage and eventual publication | OutboxOutageTests:15 failures beyond the previous10-attempt budget, success on16 |
| Lease fencing, confirmed-publish crash gap, commit-before-ACK | OutboxLeaseTests, OutboxRelayReliabilityTests, RabbitDeliveryTests |
| Actual concurrent dispatch and snapshot changes | DispatchContentionTests, DispatchConcurrencyTests, OrderConcurrencyTests |
| GPS threshold, replay, projection consistency and stale-writer rollback | CourierEventThrottleTests, CourierReplayTests, CourierSpatialTests |
| Live role/membership boundaries and revocation races | IdentityAdministrationTests, AdminRevocationTests, IdentityApiTests |
| Browser origin, private listing and SignalR authorization | FrontendBoundaryTests, RealtimeApiTests, HubLogPrivacyTests |
| Startup, proxies and abuse limits | ProductionConfigurationTests, StartupOptionsTests, ProxyRateLimitingTests, AuthenticatedRateLimitTests |
| Recovery/cleanup safety and realistic plans | RecoveryTests, RecoveryFailureIsolationTests, DataCleanupTests, RecoveryQueryPlanTests |
| Exporter privacy and worker health | SafeIncidentTests, TelemetryTests, WorkerProgressTests, WorkerRuntimeTests |
| Project dependency direction | ArchitectureTests |

## Migrations and dependency checks

`scripts/Verify-Migrations.ps1` starts its own disposable PostGIS database, applies the complete chain and checks history, then restores caller environment settings. Do not replace its target with an existing database. The current chain has20 migrations; the original19 remain unchanged.

For model drift use the installed dotnet-ef tool with a design-only nonconnecting connection setting:

```powershell
$env:ConnectionStrings__OrderFleet='Host=127.0.0.1;Port=1;Database=design_only;Username=design_only'
dotnet ef migrations has-pending-model-changes --project src/OrderFleet.Infrastructure --startup-project src/OrderFleet.Api --configuration Release --no-build
```

Restore your prior environment value afterwards. Query-plan tests seed only their disposable database, including100k/300k histories and an idle workload; full [before](evidence/release-recovery-before.txt) and [after](evidence/release-recovery-after.txt) plans include ANALYZE/BUFFERS output. Synthetic timings do not establish a production SLA.

Run `pwsh -File ./scripts/Verify-Dependencies.ps1`, source/history secret checks and `git diff --check` before publication. The local tool manifest pins dotnet-ef10.0.4; restore it with `dotnet tool restore`. Run the full suite after final source cleanup. The GitHub workflow must be observed remotely; local execution alone cannot prove hosted CI.

For an executable system smoke run [Bootstrap and Invoke-Demo](13_DELIVERY_GUIDE.md). Use a new project and free ports, inspect only its resources, and stop it without deleting existing data. Verify health, migrations, durable broker topology and financial records as well as HTTP outcomes.
