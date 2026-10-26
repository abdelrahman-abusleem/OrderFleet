# Final review remediation and engineering audit — 2026-10-26

Status: local remediation and final review gates PASS. **428 passed:257 Domain+171 Integration,0 failed,0 skipped.** Branch: phase/13-review-hardening, based on06868bc. No remotes exist. No push, remote creation, merge or deployment was performed. Existing owner changes: none at start; only remediation-authored files changed. Original developer PostgreSQL and unrelated project containers/data remain outside every test target.

## Confirmed findings and fixes

| ID / severity | Original symptom and root cause | Change and rationale | Evidence / principal files |
|---|---|---|---|
| F-01 HIGH | financial.v1 bound payment.# but MessagingWorker started no financial consumer; normal financial traffic accumulated indefinitely | Keep required durable route. ArchiveFinancialEvent validates typed facts; EfFinancialArchive atomically retains immutable audit+Inbox before ACK. No new charges/state mutations, no lossy discard or raw-payload archive | FinancialArchiveTests; Application/Payments/ArchiveFinancialEvent.cs, Infrastructure/Persistence/EfFinancialArchive.cs, Payments/FinancialAuditRecord.cs, FinancialAuditConfiguration, migration17, RabbitConsumer/MessagingWorker |
| F-02 HIGH | WorkersHealthCheck equated enabled flags with progress; stopped/stuck/idle workers were indistinguishable | Explicit monotonic lifecycle/progress, bounded configurable grace/thresholds, real idle AMQP probe, busy-handler protection. Admin runtime diagnostics and worker/queue/recovery metrics. Backlog alerts remain independent of readiness to avoid poison restart loops | WorkerProgressTests, WorkerRuntimeTests, StartupOptionsTests, TelemetryTests; Operations/WorkerProgress.cs, BrokerQueueObserver.cs, OperationalMetrics.cs, DependencyHealthChecks.cs, worker loops and operations/runtime route |
| F-03 MEDIUM | Exception-bearing/unreviewed logs were dropped; consumer callback errors were swallowed; handled HTTP rejections were noisy | All exporters behind SafeLogSink: normalize unknown Warning/Error to controlled category/type/event-code/trace. No original template/properties/exception body. Surface consumer retry/dead failures and propagate escaped errors to supervisor. Own safe handled500 once; expected rejection debug + HTTP summary | SafeIncidentTests, actual EF/SDK TelemetryTests and FinancialArchiveTests consumer logging assertions; SafeLogPolicy/SafeLogSink, TelemetryRegistration, exception handler, RabbitDeliveryProcessor/RabbitConsumer |
| F-04 MEDIUM | Missing cleanup/bounded Compose waits; fixed CI ports; migration-audit finally removed caller env values. No remote CI evidence | Random CI ports, owned-stack always cleanup,30-minute job cap,120s Compose wait cap; restore caller env. Actual source-only clean Docker build/migrate/seed/demo locally. Remote remains PENDING REMOTE VERIFICATION | .github/workflows/ci.yml, scripts/Bootstrap.ps1 and Verify-Migrations.ps1; successful clean stack and explicit env-preservation verification |
| F-05 LOW | Unused Class1/PersistenceConfigurationPlaceholder and stale weather HTTP sample | Confirmed references only in their own definitions; removed the two scaffolds. HTTP file now calls real health/catalog routes | src/OrderFleet.Application/Class1.cs and Infrastructure/Persistence/Configurations.cs removed; Api/OrderFleet.Api.http updated |
| A-01 MEDIUM | Non-object payload/non-string identity threw InvalidOperationException and entered transient retries | Check JSON shape/kind before accessing identity; poison dead-letters immediately and later financial facts proceed | EventEnvelopeCodec.cs; real financial poison/dead/retry tests |
| A-02 MEDIUM test gap | Task.WhenAll launched race tasks but did not prove actual database overlap | New session-tagged test holds root locks, witnesses two pg_stat_activity lock waiters, then releases them. Capacity/history remains exactly one | DispatchContentionTests plus unchanged dispatch/xmin race tests |

ADR-026 records alternatives and tradeoffs. Removing the financial route or merely deleting messages was rejected. Flag-only health, message-count-only idle detection and failing readiness on every poison message were rejected. Unrestricted exception logging and suppressing all unknown incidents were both rejected. No changes to the established Order domain/state/cancellation/fences or approved payment sequence. No new packages or projects.

## Additional fresh engineering review

Reviewed all four production project boundaries, actual Application/Domain/persistence paths, test fixtures and cases, migrations, configuration, deployment scripts and relevant specifications/ADRs. ArchitectureTests remains a real assembly-reference check. Targeted static searches found no production .Result/.Wait/async-void, EF InMemory/EnsureCreated integration shortcut or TODO/NotImplemented behavior. Broad catch boundaries were inspected individually: health/probe returns safe unavailability, publisher disposes/rethrows, worker supervision records failure, consumer callback now forwards failure. No generic repository/framework or speculative restructuring was introduced.

- Domain/data: guards, read-only immutable item snapshots, SAR/decimal range, aggregate rejection atomicity, existing timestamp conversion, shadow xmin, unique active capacity/fences, trusted catalog share locks and request fingerprints preserved. New archive money round-trips0.01 and decimal.MaxValue on real Postgres; all six financial states, reused-ID conflict and concurrent replay tested. Original InitialCreate/main+designer hashes unchanged; migration17 only creates archive table/index/checks.
- Transactions/reliability: root-before-courier locks, concurrent claims/constraints, capture stable keys/permanent/transient/unknown outcomes, original acceptance deadline before/after capture, compensation and orphan recovery rechecked. Archive INSERT conflict dedupe and Inbox share transaction. No publisher call inside that transaction. Confirm-before-copy ACK, bounded retries/DLQ and stale leases unchanged. Poison cannot monopolize the financial consumer.
- Security/privacy: JWT issuer/audience/signature/algorithm/lifetime, deny-by-default and resource/membership scopes retained. Runtime endpoint adds live Admin check through Application. Customer destination stays out of public order/event/log/trace outputs. Financial archive is a typed minimal history projection, not unrestricted JSON or payment authority. Real sink/SDK tests assert private markers excluded, actual EF incidents visible and exactly one unexpected HTTP incident. Source labels canonicalized and event-code is numeric metadata; no raw SQL/body/token/stack export.
- API/operations: existing status/ProblemDetails/fencing contracts preserved; safe unexpected500 now handled at the boundary. Shutdown/timeout/cancellation paths and context scopes inspected. Worker progress is explicit process-local technical state, not business authority. Idle health needs broker response; busy work cannot be masked by idle probing. Pending/dead recovery, Outbox age/counts and broker main/retry/dead ready counts observable. Readiness grace is bounded, not infinite startup health. Liveness never depends on DB/broker.
- Tests: retained all257 Domain and145 prior Integration cases; added focused regressions rather than replacing meaningful assertions. Per-class disposable infrastructure and GUID-scoped data inspected. Original race tests remain; new test adds witnessed overlap. A new test's first full-suite run exposed stale activity snapshots, corrected below rather than increasing timeout or weakening assertions.
- Delivery/config: .NET10.0.403/runtime10.0.14 pins, actual project references/package versions, Dockerfile/nonroot/read-only/capability/resource settings, ignored secrets and source-only build context reviewed. CI commands match local commands; remote runner execution is not asserted. No owner database/volume target is used for migrations/tests.

No additional confirmed unresolved BLOCKER/HIGH production defect was found in this review. This is code review and executed test evidence, not formal certification or a guarantee against all future faults.

## Regression correction from the independent second pass

After initial targeted tests and three repeated19-case race groups passed, a full run produced257 Domain pass +170/171 Integration pass: the new contention witness timed out. It queried pg_stat_activity through the transaction deliberately holding locks. PostgreSQL retains an activity snapshot within a transaction, so a first sample before both contenders arrived could remain stale. The observer now uses a separate autocommit context; both real blocked sessions must still be observed and both tasks must still be incomplete before lock release. No timeout inflation/assertion relaxation. Corrected single test and two repeated19-case groups pass; final full regression passed257 Domain+171 Integration,0 failed/skipped.

Primary source: [PostgreSQL17 statistics snapshots](https://www.postgresql.org/docs/17/monitoring-stats.html#MONITORING-STATS-VIEWS). This behavior also explains why repeated isolated success was insufficient proof against suite-level scheduling changes.

## Verification ledger

- SDK10.0.403/runtime10.0.14. Restore succeeded. Final strict Release build0 warnings/errors. `dotnet format whitespace ... --verify-no-changes --no-restore` and `dotnet format analyzers ... --verify-no-changes --no-restore --severity warn` pass. Final `dotnet test OrderFleet.slnx -c Release --no-build --no-restore`:257 Domain+171 Integration passed,0 failed/skipped.
- First expanded full regression:257 Domain+167 Integration passed,0 failed/skipped. Later full run found the contention test issue above (170/171 Integration), then corrected-target and repeated-race checks passed. No failed run is presented as final success.
- Targeted financial/lifecycle/incident/privacy checks:21 cases passed initially; incident/privacy/startup12 passed; live-worker/broker/metrics/startup/privacy15 passed; final financial/money/disabled-worker/incident/SDK23 passed. Counts overlap; do not sum them as unique coverage.
- Race groups:19 cases ×3 passed before snapshot correction; corrected witness1 passed and19×2 passed afterward. Final whole suite rerun after the correction passed257+171,0 failed/skipped.
- `scripts/Verify-Migrations.ps1`:17 migrations applied from empty disposable PostGIS, including20261026000247_ArchiveFinancialFacts. Repeated with caller environment sentinels: password/connection settings restored correctly. Developer target unchanged. Global dotnet-ef10.0.3 emits a compatibility warning against EF10.0.4; command completes and17-row history verified.
- Clean source-only export: no .env/.tools/bin/obj/Git data copied. Bootstrap generated independent credentials, built Linux image, started new random-port volumes, explicitly migrated/seeded and reached healthy. `Invoke-Demo.ps1` passed SAR51/create replay/role403/foreign404/capture-accept-dispatch/pickup/delivery/terminal409/ETA. SQL counts:17 migrations,1 Delivered,2 FinancialAuditRecords,2 financial.archive.v1 Inbox rows. Container UID1654, read-only root, healthy. No passwords/tokens/private coordinates printed. Initial EF history probe emits safe DatabaseCommandFailure/EventCode20102; migration success assessed by completion/history, not log absence.
- Dependency audit: all six projects report no known vulnerable packages from configured sources. This is not a container OS CVE scan.
- Final diff/security review passed; the exact-label disposable stack and its two volumes/source export were removed, original OrderFleet/MeetWise resources remained healthy, and final Git state is recorded below. Source Domain diff empty; original migration hashes3B79630A5CA4D420AF8981720DE31FEA2BE3464929BFF6F5343EAEA475054EE0 /357E7F1EC87B3C03FD52324C3FB33E7DB147FF1857D3B439A764BD19BDE6132E.

## New and updated tests

New: FinancialArchiveTests(11 financial typed-state/money/replay/crash-gap/retry/DLQ/poison/backlog/restart cases), WorkerProgressTests(7 lifecycle/monotonic/idle/busy/failed/disabled/stopped cases), WorkerRuntimeTests(1 real idle/outage/reconnect/auth/metrics case), SafeIncidentTests(2 privacy/correlation/single500 cases), DispatchContentionTests(1 witnessed DB overlap). Updated: StartupOptionsTests(+4 worker-bound validation cases), TelemetryTests(stronger actual host EF incident and new SDK metric assertions). Existing tests are retained. No new test-only business implementation.

## Remote CI — PENDING REMOTE VERIFICATION

No Git remote is configured, so there are no accessible remote checks to inspect. No remote creation/push authorization was granted. Local Linux Docker build/bootstrap/E2E and real Testcontainers provide local evidence, not a GitHub-hosted runner result.

After owner approval to connect/push the reviewed branch, trigger the existing Build and test workflow via its push/pull_request events. Confirm both domain and integration jobs pass; inspect integration-results TRX artifact and the clean Compose demo step. The domain job restores, whitespace-checks, strict-builds and tests Domain; integration job checks Docker, restores, runs real PostGIS/Rabbit tests and clean Compose/demo using random ports. The always-cleanup step targets only orderfleet-ci. If either job fails, preserve redacted diagnostics and fix/rerun before merge. No workflow_dispatch trigger is currently defined; use the configured push/PR trigger. Do not treat this report as owner authorization to push.

## Remaining classifications

| Classification | Remaining item and justification |
|---|---|
| MEDIUM release gate | PENDING REMOTE VERIFICATION: no remote/authorization. Workflow prepared and local equivalents run; hosted runner result remains external |
| LOW tooling warning | Global dotnet-ef10.0.3 vs EF runtime10.0.4. Successful generation/application/history proof; global developer tools were not silently modified. Align tool patch version when maintaining local tooling |
| ACCEPTED TRADEOFF | Broker metric counts ready messages, not unacked or oldest broker age. Consumer prefetch is bounded; worker progress and DB Outbox age provide complementary signals. Runtime snapshots include observation time; no false zero on failed sample |
| ACCEPTED TRADEOFF | Poison/dead backlog retained for controlled repair/replay; no automatic lossy eviction. Healthy financial traffic drains automatically. Monitor queue/DB capacity and establish retention/dedupe horizons before production pruning |
| ACCEPTED TRADEOFF | Single API realtime, best-effort client notification with snapshot refresh, approximate ETA and mock payments remain explicit approved demo boundaries |
| ACCEPTED TRADEOFF | Production TLS/identity governance, secret manager, backup/restore drills, OS image scan, retention/alert thresholds and human merge/deploy review are not certified by local test success |

No unresolved business-policy decision was introduced. No BLOCKER/HIGH remains confirmed. Owner review/merge and remote CI are still external steps; no production-ready/HA/SLA claim.

## Git handoff

Initial commit06868bc; remediation branch phase/13-review-hardening. Implementation commit: **3372c7a — Remediate financial delivery and worker observability findings**. Git status was verified clean immediately after that commit; this documentation-only follow-up records the hash and cleanup evidence. Use `git log -2 --oneline` for both implementation and evidence commits. Changes are limited to archive/migration, progress/health/metrics/runtime authorization, incident handling, verified scripts/CI/cleanup, regression tests and documentation. No Domain state-machine rewrite, push, merge, rebase, amend or deployment.


## Complete changed-file inventory

- `.github/workflows/ci.yml`
- `docs/00_START_HERE.md`
- `docs/02_ARCHITECTURE.md`
- `docs/05_QUALITY_GATES.md`
- `docs/06_SECURITY_AND_RELIABILITY.md`
- `docs/07_PROGRESS_AND_HANDOFF.md`
- `docs/08_DECISION_LOG.md`
- `docs/09_LEARNING_GUIDE.md`
- `docs/10_REFERENCE_LIBRARY.md`
- `docs/11_API_DATA_CONTRACTS.md`
- `docs/13_DELIVERY_GUIDE.md`
- `docs/14_HARDENING_REVIEW.md`
- `README.md`
- `scripts/Bootstrap.ps1`
- `scripts/Verify-Migrations.ps1`
- `src/OrderFleet.Api/Configuration/MessagingRegistration.cs`
- `src/OrderFleet.Api/Configuration/OperationsRegistration.cs`
- `src/OrderFleet.Api/Configuration/TelemetryRegistration.cs`
- `src/OrderFleet.Api/Diagnostics/SafeLogPolicy.cs`
- `src/OrderFleet.Api/Diagnostics/SafeLogSink.cs`
- `src/OrderFleet.Api/Endpoints/OperationsEndpoints.cs`
- `src/OrderFleet.Api/Errors/OrderFleetExceptionHandler.cs`
- `src/OrderFleet.Api/OrderFleet.Api.http`
- `src/OrderFleet.Api/Program.cs`
- `src/OrderFleet.Application/Class1.cs`
- `src/OrderFleet.Application/Operations/OperateOrderFleet.cs`
- `src/OrderFleet.Application/Operations/OperationsContracts.cs`
- `src/OrderFleet.Application/Operations/RuntimeDiagnostics.cs`
- `src/OrderFleet.Application/Payments/ArchiveFinancialEvent.cs`
- `src/OrderFleet.Infrastructure/Messaging/EventEnvelopeCodec.cs`
- `src/OrderFleet.Infrastructure/Messaging/MessagingWorker.cs`
- `src/OrderFleet.Infrastructure/Messaging/RabbitConsumer.cs`
- `src/OrderFleet.Infrastructure/Messaging/RabbitDeliveryProcessor.cs`
- `src/OrderFleet.Infrastructure/Operations/BrokerQueueObserver.cs`
- `src/OrderFleet.Infrastructure/Operations/DependencyHealthChecks.cs`
- `src/OrderFleet.Infrastructure/Operations/OperationalMetrics.cs`
- `src/OrderFleet.Infrastructure/Operations/WorkerProgress.cs`
- `src/OrderFleet.Infrastructure/Payments/FinancialAuditRecord.cs`
- `src/OrderFleet.Infrastructure/Persistence/Configurations.cs`
- `src/OrderFleet.Infrastructure/Persistence/Configurations/FinancialAuditConfiguration.cs`
- `src/OrderFleet.Infrastructure/Persistence/EfFinancialArchive.cs`
- `src/OrderFleet.Infrastructure/Persistence/EfOperationsStore.cs`
- `src/OrderFleet.Infrastructure/Persistence/Migrations/20261026000247_ArchiveFinancialFacts.cs`
- `src/OrderFleet.Infrastructure/Persistence/Migrations/20261026000247_ArchiveFinancialFacts.Designer.cs`
- `src/OrderFleet.Infrastructure/Persistence/Migrations/OrderFleetDbContextModelSnapshot.cs`
- `src/OrderFleet.Infrastructure/Persistence/OrderFleetDbContext.cs`
- `src/OrderFleet.Infrastructure/Recovery/RecoveryWorker.cs`
- `tests/OrderFleet.IntegrationTests/DispatchContentionTests.cs`
- `tests/OrderFleet.IntegrationTests/FinancialArchiveTests.cs`
- `tests/OrderFleet.IntegrationTests/SafeIncidentTests.cs`
- `tests/OrderFleet.IntegrationTests/StartupOptionsTests.cs`
- `tests/OrderFleet.IntegrationTests/TelemetryTests.cs`
- `tests/OrderFleet.IntegrationTests/WorkerProgressTests.cs`
- `tests/OrderFleet.IntegrationTests/WorkerRuntimeTests.cs`
