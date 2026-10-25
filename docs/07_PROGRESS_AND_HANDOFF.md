# Live progress and engineering handoff (update after every slice)

> IMPORTANT: Values marked **historical handoff** are statements from earlier work, **not proof** that current local repo still has the same build/test state. Implementation agent must verify and replace them with dated observations.

## Baseline (historical handoff; VERIFY FIRST)

| Area | Handoff statement | Actual verification status |
|---|---|---|
| Solution | `.NET 10`, `OrderFleet.slnx` | UNVERIFIED by incoming agent |
| Domain | 222 Domain tests + 1 placeholder Integration test = **223 total** previously passed Release | HISTORICAL; rerun |
| Domain model | Order/OrderItem/OrderPricingLimits, state transitions and assignment fence implemented | HISTORICAL; inspect |
| Docker | PostgreSQL 17 via Compose, host localhost:5433; container reportedly healthy | HISTORICAL; inspect current environment |
| Persistence | Npgsql EF Core 10.0.3, DbContext and OrderConfiguration implemented | HISTORICAL; inspect packages/code |
| Migration | `InitialCreate` generated and reportedly applied; `Orders`, `OrderItems`, migrations history | HISTORICAL; verify migration/database |
| Application | Api configured for DI connection and local User Secrets | HISTORICAL; inspect; do not copy secrets |
| Integration tests | A save/reload Testcontainers test was proposed in chat | **NOT CONFIRMED CREATED OR RUN** |
| Other modules | Identity/catalog/payment/geo/messaging/dispatch/realtime/observability | NOT VERIFIED; do not assume present |

## Current-phase tracker — fill with evidence

| Phase | Current status | Evidence | Blocking defects | Next action |
|---|---|---|---|---|
| 0 Audit | VERIFIED_COMPLETE | dated audit below | none | complete |
| 1 Domain | VERIFIED_COMPLETE | baseline 222 Domain passed | none | preserve rules |
| 1.5 Approved Domain additions (ADR-013) | VERIFIED_COMPLETE locally | 234 Domain passed; baseline commit 062e3ad | remote CI unverified | complete |
| 2A Persistence | VERIFIED locally / external CI UNVERIFIED | 18 real PostgreSQL cases + disposable migration CLI | no remote configured by owner instruction | preserve evidence, execute CI when owner enables remote |
| 2B Concurrency | VERIFIED_COMPLETE locally | 11 DB conflicts + 4 HTTP error boundary cases | remote CI unverified | proceed to Identity |
| 3 Identity | NOT_VERIFIED | — | — | after 2B |
| 4 Catalog + Orders API | NOT_VERIFIED | — | — | after Identity |
| 5 Payments | NOT_VERIFIED | — | — | after order flow |
| 6 Couriers + Geo | NOT_VERIFIED | — | — | after core order flow |
| 7 Outbox + RabbitMQ | NOT_VERIFIED | — | — | after events defined |
| 8 Dispatch | NOT_VERIFIED | — | — | after courier + outbox |
| 9 Recovery | NOT_VERIFIED | — | — | after dispatch |
| 10 Realtime + ETA | NOT_VERIFIED | — | — | after reliable events |
| 11 Observability | NOT_VERIFIED | — | — | hardening |
| 12 Delivery | NOT_VERIFIED | — | — | final E2E |

## Session log template (copy and populate per milestone)

```markdown
### YYYY-MM-DD — Phase X.Y — Slice title
- Status: IN_PROGRESS / BLOCKED / VERIFIED_COMPLETE
- Business behavior added:
- Architecture impact:
- Files modified:
- Migration and data implications:
- Commands executed:
- Test results (actual passed/failed/skipped, environment):
- Independent diff-review findings, severity, fixes, re-test:
- Known risks/tradeoffs:
- ADR changes:
- Next smallest coherent slice:
```

## Blocking rule

### 2026-10-25 — Phase 2B HTTP conflict boundary and local phase report

- Local baseline commit 062e3ad created after owner-authorized Git initialization; phase/2b-concurrency created from it. No remote configured, no push. The initial snapshot includes the previous audited slices; no fabricated per-phase history.
- Added HttpErrorMappingTests first (missing handler compile failure), then OrderFleetExceptionHandler registered in actual API composition. DbUpdateConcurrencyException and DomainException return safe 409 ProblemDetails; argument errors return generic 400; unexpected errors retain generic 500. Handled errors log only exception type and trace identifier, not message/PII. No blind retry.
- HTTP tests use real ASP.NET middleware/TestServer for the production handler; DB races are separate real PostgreSQL tests. The suite does not claim an order-mutation endpoint exists yet.
- `dotnet test OrderFleet.slnx -c Release --no-restore`: 234 Domain + 34 Integration passed, 0 failed/skipped (29 real DB + 4 HTTP boundary + 1 retained placeholder). Reviewed actual diff and checked whitespace; no new P0/P1 found. New migrations leave InitialCreate unchanged.
- Phase 2B local behavior gate passed. Remote CI remains unverified because owner explicitly prohibits pushing/configuring a remote at this time; workflow and local equivalent commands exist. Continuing implementation without claiming remote execution.
- Next exact slice: Phase 3 tests for Identity registration/login, JWT signature/issuer/audience/lifetime validation, role escalation rejection and ownership isolation; implement ADR-010.

### 2026-10-25 — owner-approved Git initialization and concurrency evidence

- Owner confirmed this is the correct folder and authorized initialization after parent-repository/secret checks. Checked every parent for .git and `git rev-parse --show-toplevel`; no enclosing repository found. Initialized local `main` only; no remote/push.
- Expanded gitignore for .NET/Visual Studio/Rider/VS Code, caches, build/test outputs, secrets, environment files and temporary artifacts. Moved the existing Compose password into an ignored local .env; compose.yaml now requires POSTGRES_PASSWORD interpolation. Existing credential retained locally without exposing it; no container recreation or DB writes. `docker compose config --quiet` passed. Reviewed candidate file list; source/tests/migrations/AGENTS/docs/Compose retained, no caches/artifacts.
- ADR-011 tests first reproduced 11 failures: no xmin mapping; parallel accept/cancel both saved; stale pickup overwrote newer same-courier assignment. Added `Property<uint>("xmin").IsRowVersion()` shadow property and provider migration. Npgsql recognizes the xid rowVersion as the existing system column rather than an ordinary user column; actual migrated PostgreSQL tests verify it. Domain has no concurrency version field.
- Latest `dotnet build OrderFleet.slnx -c Release --no-restore`: 0 warnings/errors. `dotnet test OrderFleet.slnx -c Release --no-build --no-restore`: 234 Domain + 30 Integration passed, 0 failed/skipped. Integration = 29 real PostgreSQL cases + 1 original placeholder; 11 conflict cases verify accept/cancel, assignment, release/pickup races, parallel independent-context saves and stale fence after reassignment. No automatic retry is used.
- Initial local baseline snapshot includes the inspected original project plus already-tested Phase 1.5/2A/DB-concurrency work performed while Git authorization was pending. These are not claimed as historical phase commits. Future slices use phase branches. Automation commits use explicit Codex local authorship rather than inventing owner identity.
- Phase 2B still needs HTTP conflict mapping verification. Remote CI remains UNVERIFIED; nothing was pushed. Identity package references prepared for the next approved phase; auth behavior is not yet implemented.

### 2026-10-25 — Phase 2A persistence evidence

- Added Testcontainers.PostgreSql 4.16.0 fixture using postgres:17, random credentials/port and MigrateAsync. No local developer connection is consumed. Added OrderPersistenceTests and minimal GitHub Actions jobs (Domain and Docker integration) plus README and gitignore.
- Tests first: migration test failed on PendingModelChanges; generated new timestamp/money migration without editing InitialCreate. Removed generated fabricated year-1 CreatedAt default; nonempty legacy data requires approved backfill. Existing developer Orders count read as 0; no developer data writes occurred.
- Real test discovered P1 mapping defects: fixed numeric(31,2) caused Npgsql 10.0.3 IndexOutOfRangeException on decimal.MaxValue; non-UTC timestamp input caused Npgsql rejection. Corrected with scale-preserving numeric plus positivity/range/precision checks and UTC EF value conversion. Domain behavior unchanged. Both regression tests rerun and pass.
- `dotnet build OrderFleet.slnx -c Release --no-restore`: 0 warnings/errors. `./scripts/Verify-Migrations.ps1`: applied InitialCreate and both new migrations to a separate temporary postgres:17 container, printed migration history, cleaned up its own container. First CLI attempt used stale Debug output and failed PendingModelChanges; corrected to `--configuration Release`, then passed. dotnet-ef 10.0.3 reports version mismatch with EF 10.0.4, but execution succeeded.
- `dotnet test OrderFleet.slnx -c Release --no-build --no-restore`: 234 Domain + 19 Integration passed, 0 failed/skipped. Integration includes 18 meaningful PostgreSQL cases and the original placeholder. Assertions cover fresh-context aggregate/items/totals, complete lifecycle timestamps, every permitted cancel, null-clearing, stale fence after reload, duplicate-root transaction failure, money boundaries and database rejection of invalid money.
- Self-review checked generated Up/Down, model snapshot, nullable timestamp converters, test fixture isolation and no secret logging; InitialCreate hashes remain unchanged: 3B79630A5CA4D420AF8981720DE31FEA2BE3464929BFF6F5343EAEA475054EE0 and designer 357E7F1EC87B3C03FD52324C3FB33E7DB147FF1857D3B439A764BD19BDE6132E.
- Phase 2A persistence behavior verified locally. Remote CI UNVERIFIED (workflow authored, no remote/push). Branch/commit remains blocked on absent Git metadata and pending owner decision; do not mark whole phase gate complete. Independent next work: ADR-011 DB conflict regression tests, then mapping. Full later roadmap scope remains mandatory.

### 2026-10-25 — observed audit and Phase 1.5 implementation

- Read AGENTS.md, README_INSTALL.txt, and all 13 docs. Inspected all projects, production source, Domain tests, the Integration placeholder, mappings, InitialCreate and snapshot.
- `git status --short` and `git branch --show-current`: fatal, this directory is not a Git repository. Owner asked whether to initialize here or use another checkout. No branch/commit claimed; baseline files preserved in a temporary audit copy. No owner files discarded.
- SDK 10.0.401; runtime/ASP.NET 10.0.12; Npgsql EF 10.0.3; EF runtime 10.0.4 (transitive); dotnet-ef 10.0.3. `dotnet --info` worked outside sandbox. Initial sandbox access to Docker/service control was denied; approved external checks worked.
- `docker compose ps`: existing postgres:17 container healthy on loopback 5433. Read-only SQL confirms InitialCreate 20261024133928 recorded with EF 10.0.4. Developer database was not changed.
- `dotnet list OrderFleet.slnx package --vulnerable --include-transitive`: no known vulnerable packages reported by configured sources.
- Initial `dotnet build OrderFleet.slnx -c Release` failed CS0101: empty Persistence.Configurations template class collides with real Configurations namespace. Renamed only the unused template type; Release rebuild passed with 0 warnings/errors.
- Baseline Domain: 222 passed, 0 failed/skipped. Solution after template fix: 222 Domain + 1 Integration placeholder passed. Placeholder does not verify infrastructure.
- Added OrderDomainAdditionsTests first; pre-implementation run failed compilation on missing OrderDomainEvent, as expected for the absent API. Implemented ADR-013 without changing pricing/state/cancellation/fence rules. Existing tests now supply deterministic time and expect DomainException for business conflicts; unchanged snapshots additionally compare timestamps/events.
- `dotnet build OrderFleet.slnx -c Release --no-restore`: passed, 0 warnings/errors. `dotnet test OrderFleet.slnx -c Release --no-build --no-restore`: 234 Domain passed + 1 Integration placeholder passed; 0 failed/skipped.
- Self-review: checked all transition guards precede mutations/events, release/cancel capture old fences, event collection is read-only, clock stays outside Domain, EF ignores transient events. No new P0/P1 issue found in this slice. Monetary guards from baseline preserved.
- Phase 0 audit evidence recorded; Phase 1 verified. Phase 1.5 code/test gate passes; phase Git gate pending missing repository. Continuing independent Phase 2A persistence verification while awaiting owner Git decision.

| Requirement | Observed state |
|---|---|
| Order domain/pricing/state/fencing | observed complete, 234 Domain tests pass after ADR-013 |
| Domain events/timestamps/DomainException | implemented/tested; persistence timestamps pending new migration |
| EF DbContext/owned mappings/InitialCreate | observed partial; real round trips not yet verified |
| PostgreSQL developer container | healthy; migration history read; no writes performed |
| Integration coverage | only placeholder at baseline; Testcontainers work in progress |
| Optimistic DB concurrency | not implemented |
| Identity/catalog/order HTTP/payment/geo | not implemented |
| Outbox/RabbitMQ/dispatch/recovery | not implemented |
| SignalR/ETA/Serilog/OpenTelemetry/health | not implemented |
| CI/portfolio delivery/E2E | not implemented; README skeleton added |

- Next slice: Testcontainers migration + aggregate save/reload/transition tests; timestamp migration and monetary schema compatibility; minimal CI. Later phases remain in scope.

If current repo state conflicts with this document, **the inspected repository wins as evidence**. Update this log with what is real. Do not silently modify established Domain API to make a copied test snippet compile; adapt the test to actual signatures and add evidence.
