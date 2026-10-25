# Quality assurance, testing and mandatory self-review

## Executed evidence — 2026-10-25

DB-concurrency checkpoint: Release build 0 warnings/errors; 234 Domain + 30 Integration pass, 0 failed/skipped. Eleven new real-DB conflict cases changed from failing to passing with the approved xmin shadow property. This proves DB stale-write rejection, including same-courier reassignment, but HTTP 409 mapping is still pending. Owner authorized local Git initialization; parent checks and credential exclusion performed. Remote CI is still unverified.

Phase 2A local persistence checks: Release build 0 warnings/errors; 234 Domain + 19 Integration passed (18 real PostgreSQL cases, 1 retained placeholder). Migration CLI applied all three migrations to a separate disposable PostgreSQL container. New schema stores the entire tested Domain money range; UTC conversion and money-check regressions pass. Original migration hashes unchanged. CI workflow created but remote execution UNVERIFIED; Git phase commits pending missing repository. This is local persistence proof, not a completed whole-project gate.

Phase 1.5: Release solution build passes with 0 warnings/errors; 234 Domain tests pass, 0 failed/skipped. The one pre-existing Integration placeholder passes but proves no persistence behavior. Domain events/timestamps/exceptions covered; prior business tests preserved. Phase branch/commit gate is pending because this supplied directory has no Git metadata. No CI run claimed. Full roadmap gates below remain binding.

## Global quality gates (every feature)

A feature is `VERIFIED_COMPLETE` only when **all applicable** items are true:

- [ ] Requirements and trust boundaries identified; negative cases listed.
- [ ] Implementation correct and layered; Domain invariants preserved.
- [ ] Relevant migrations reviewed for safe evolution and database compatibility.
- [ ] Build succeeded; compile warnings/errors reviewed.
- [ ] Unit tests cover rule boundaries and failure behavior.
- [ ] Real PostgreSQL integration tests cover persistence/constraints where changed.
- [ ] Real RabbitMQ integration tests cover semantics when messaging changed.
- [ ] Relevant concurrency and idempotency tests cover races/replays.
- [ ] API/auth tests check 401/403/404/409 and ownership where relevant.
- [ ] Logs/secrets/security/privacy and graceful failure reviewed.
- [ ] Diff critically reviewed for OOP/SOLID, mixed responsibilities, repeated code and hidden side effects; findings corrected.
- [ ] Affected tests rerun **after** fixes (not only before).
- [ ] `07_PROGRESS_AND_HANDOFF.md` and `08_DECISION_LOG.md` updated per slice; `05` and `09` updated at phase end; Release build green and README status section current.

A code reviewer should reject vague claims such as “looks good” or “all tested” without commands and output summaries.

## Test pyramid

1. **Domain unit tests:** state machine, item invariants, money/limits boundaries including overflow, cancel/release/fence; no DB. Arrange/Act/Assert, deterministic IDs/clock where needed.
2. **Application unit tests:** trusted catalog orchestration and authorization decision logic, mock provider states and domain events, controlled failures. Mocks only for true application ports, not as replacement for integration coverage.
3. **Infrastructure integration tests:** disposable PostgreSQL with actual migrations and EF Npgsql, PostGIS and RabbitMQ containers; real concurrent sessions. Cover FK/unique/partial constraints, transactional behavior and provider specifics.
4. **API integration tests:** host via `WebApplicationFactory` where appropriate, auth/JWT or controlled test auth with separate real JWT validation checks, DTO/status/error/privacy contracts.
5. **Critical E2E tests:** actual high-value workflow; not every combination. Reproducible demo.

## Required test matrix

| Concern | Happy path | Negative/edge | Parallel/replay/failure |
|---|---|---|---|
| Order persistence | Save/load items/amount/status | missing row/invalid stored constraints | stale context update conflict |
| Trusted catalog | product price used | foreign/unavailable product, changed price | create retry same key |
| Identity | permitted role and owned resource | missing/invalid token, wrong role | revoked access and stale claims documented |
| Payments | mock authorize/capture/refund | decline, mismatch currency/status | repeated webhook, concurrent capture, crash |
| Geo | nearby eligible courier | invalid/stale coordinate, just outside radius | out-of-order location events |
| Dispatch | single eligible assignment | no candidate, cancel in flight | 2 workers/1 courier, 2 couriers/1 order, stale fence |
| Outbox | event after commit | broker disconnected, invalid schema | publish duplicate, lost ACK, relay restart |
| Timeout | expires eligible order | no change after pickup/delivered | duplicate timer, race with accept/reassign |
| Realtime | authorized parties receive | cross-user subscribe denied | reconnect and delayed event ordering |

## Determinism and fixtures

- Use **per-test disposable DB/container or isolated schema** with deliberate fixture reuse/cleanup; no shared mutable test data or dependency on dev database `orderfleet`.
- Run `MigrateAsync` and verify migration history; don't replace migrations with `EnsureCreated` when testing migrations.
- Do not assume Docker present in every CI job; isolate integration job and mark skipped/blocked explicitly if unavailable.
- Fixed clocks/random seeds for deterministic policy tests. Integration concurrency tests require coordination/barriers rather than arbitrary `Task.Delay` races.
- Avoid flaky network waits; use readiness and bounded timeouts. Capture useful logs on failure without leaking credentials.
- Tests should assert durable state and business effects after restart, not only method invocation counts.

## Mandatory adversarial review after every feature

The implementing agent acts as a skeptical reviewer of its own diff. Inspect actual changed files and answer:

1. **Correctness:** Is any input unchecked? Existing invariant bypassed? Wrong status/role? Numeric overflow or precision mismatch?
2. **OOP/SOLID:** Is any method doing independent parsing + auth + pricing + DB + broker? Split by responsibility. Does any interface/service exist solely to decorate simple code? Simplify.
3. **Security:** Can a user send another owner ID or price? Guess order/group ID? Inject SQL? Logs contain token/location? Is an administrative action audited?
4. **Concurrency:** Can two contexts/workers pass a check then both commit? Are idempotency uniqueness and fence validation atomic?
5. **Reliability:** What happens if process crashes before/after commit, before/after broker publish, before/after ACK? Can messages arrive twice/out of order?
6. **DB:** Is schema migration destructive or mis-sized? Are uniqueness/FK/partial/spatial indexes present and provider compatible? Is transaction scope correct?
7. **Operations:** Retry is bounded? Cancellation honored? Timeouts and fallback safe? No unbounded queues/queries?
8. **Tests:** Did real DB/broker tests exercise the actual danger? Are tests checking outcomes or just mocks? Did a refactor invalidate fixture assumptions?
9. **Docs:** Do README, ADRs, specs and API examples match the merged behavior?

If findings found: log severity (**P0 blocker, P1 major, P2 minor**), add regression coverage, fix, rerun. Never label P0/P1 complete while open.

## Commands — adapt to actual repo and installed tooling

```powershell
# From repository root
dotnet restore OrderFleet.slnx
dotnet build OrderFleet.slnx -c Release
dotnet test OrderFleet.slnx -c Release

# Targeted integration tests after verifying Docker health
dotnet test tests/OrderFleet.IntegrationTests/OrderFleet.IntegrationTests.csproj -c Release

# Inspect actual current local services
docker compose ps
```

Don't blindly run `dotnet ef database update` against unverified databases. Inspect migration target/connection first and use disposable testing database. If using format/analyzers, run only tools installed/configured in repo and document their outcome.

## Evidence ledger — mandatory sample

```text
Phase: 2A | Slice: Order save/reload
Status: VERIFIED_COMPLETE / BLOCKED / IN_PROGRESS
Files changed: ...
Commands: dotnet build ... ; dotnet test ...
Observed: Tests passed: N, failed: 0 (date + environment)
Container versions: ...
Review findings: P0=0 P1=0 P2=...
Known risks: ...
Next exact slice: ...
```

The ability to compile, a passing mocked unit test, a generated migration, a working swagger page or an unreviewed PR are **not** equivalent to production behavior verified.
