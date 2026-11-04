# Quality assurance, testing and mandatory self-review

## Executed evidence — 2026-10-25

Phase 4 local gate: 245 Domain + 70 Integration passed, 0 failed/skipped; Release build 0 warnings/errors; disposable CLI applied seven migrations. Nineteen new HTTP/DB cases cover trusted snapshots, cross-actor denial, private destinations, inline expiry, restart/configuration persistence, idempotency (parallel/replay/misuse), catalog-change fencing, audit rollback and OpenAPI. Eleven new Domain cases cover coordinates/catalog invariants. Prior tests remain. No financial/dispatch/messaging completion claim; those mandatory phases follow.

Phase 3 local gate: Release build 0 warnings/errors; disposable CLI applied five migrations; 234 Domain + 51 Integration pass, 0 failed/skipped. New 17 Identity security cases run actual HTTP/JWT/Identity stores against PostgreSQL, not test-auth bypass. Account uniqueness, password hashing, credential errors, lockout/rate limiting, claim checks and resource isolation/revocation verified. Package advisories: no known vulnerabilities reported. Current API has authentication and order reads; mutation/payment/dispatch workflows are not yet claimed.

Phase 2B local gate: 234 Domain + 34 Integration passed, 0 failed/skipped. Real DB stale-context conflicts (11), persistence (18), HTTP middleware mapping (4) and the historical placeholder (1) are distinguished. Production exception boundary maps concurrency/domain conflicts to safe 409 and argument errors to 400; unexpected failures yield generic 500. Actual order mutation HTTP use cases do not exist yet. Remote CI remains external/unverified under the owner's no-push instruction.

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

### Phase 5 local gate — 2026-10-25
Release build: 0 warnings/errors. Full suite: 253 Domain + 81 Integration passed, 0 failed/skipped. Nine migrations verified in disposable PostgreSQL 17. Payment retries, ambiguity, concurrency and compensation verified; broker delivery and recovery workers remain later gates. Remote CI unverified. See ADR-017 and progress ledger.

### Phase 6 local gate — 2026-10-25
Release build: 0 warnings/errors. Full suite: 256 Domain + 84 Integration passed, 0 failed/skipped. Ten migrations applied to disposable PostGIS. Ordered/authorized location updates, geography meters/dateline/radius, freshness, stale rollback and spatial index eligibility verified. Legacy developer database preserved. Remote CI unverified.

### Phase 7 delivery gate — 2026-10-25
Actual PostgreSQL and RabbitMQ verify leases, fenced completion, bounded failures, rollback before commit, mandatory routing, delayed retries/DLQ, duplicate publish, commit-before-ACK redelivery, broker outage/restart and hosted compensation. No exactly-once or HA claim. Full suite256 Domain +91 Integration, followed by targeted three-case lease suite including new rollback case; final full count recorded in progress. Remote CI remains unverified.

Final Phase7 full suite: 256 Domain +92 Integration, 0 failed/skipped. Local gate passed; no remote CI claim.

### Phase 8 local gate — 2026-10-25
Release build0 warnings/errors. Full regression256 Domain +100 Integration passed, 0 failed/skipped. Eleven migrations verified in disposable PostGIS. Actual DB parallel capacity/fence/cancel/pickup races and real broker-driven dispatch/delivery pass. Remaining recovery scheduling and operational exhaustion handling are Phase9/11. No remote CI or HA claim.

### Phase 9 local gate — 2026-10-25
Release build0 warnings/errors. Full regression257 Domain +108 Integration passed, 0 failed/skipped. Thirteen migrations verified in disposable PostGIS. Actual restart/deadline/fence/cycle/reconciliation/orphan and hosted compensation cases pass. PickedUp capacity cannot be expired by a pickup timer. Operational support/alerts remain Phase11, remote CI unverified.

### Phase 10 local gate — 2026-10-25
SDK10.0.403/runtime10.0.14 Release build0 warnings/errors. Full regression257 Domain +119 Integration passed,0 failed/skipped. Real SignalR HTTP/WebSocket, live authorization/revocation, expiry, reconnect/sequence, private location and actual RabbitMQ-to-hub flow verified. Thirteen migrations passed on patched runtime; no known package advisories. Single-API realtime/best-effort and approximate ETA limitations documented. Remote CI unverified.

Phase11 verification:16 additive migrations applied to empty disposable PostGIS and to the isolated Compose audit stack. Container build succeeds, runtime nonroot/read-only and readiness healthy. Real SDK/Serilog privacy, DB+broker outage probes, live admin revocation, replay/lease protection and poison-item recovery tests pass. Initial full regression exposed nondeterministic dispatch trace sampling (137/138 Integration passed); deferred sampler configuration fixes it without removing trace assertions. Targeted telemetry/startup10/10 then passed. Final full result recorded in progress after completion. No developer DB changes.

## Phase12 final local gate — 2026-10-26

PASS locally:402 tests(257 Domain+145 Integration),0 failed/skipped; strict Release build0 warnings/errors; whitespace verification;16 migrations from empty disposable PostGIS; clean source-only export bootstrap with newly generated credentials/new volumes; authenticated full HTTP demo; actual PostGIS/RabbitMQ/SignalR, host restart, financial/capacity/assignment durable assertions. Empty Integration placeholder replaced with real dependency test. Final targeted delivery8/8 passed then full regression rerun. No production Order state-machine change or weakened assertion.

Review found/fixed frozen-clock test helper inconsistency and an eventual-publication observation race. Review checked authority/privacy, payment failure classes/original deadlines, lock order/concurrency/fences, Outbox/Inbox atomicity, bounded retries, seed collision rollback/password preservation, migrations and safe target cleanup. No known unresolved P0/P1 finding. All scoped locally applicable gates pass; human owner review/merge and remote CI remain explicitly pending. No push/deploy/production-readiness/HA/image-scan certification claim. Complete runbook and limits:13_DELIVERY_GUIDE.md.

### Review hardening final local gate — 2026-10-26

PASS locally:428 tests(257 Domain+171 Integration),0 failed/skipped. Strict Release build0 warnings/errors, standalone whitespace and warning-level analyzer verification pass. All17 migrations apply to clean disposable PostGIS; migration-audit caller environment restoration verified. Fresh source-only Linux Docker build/Compose startup/seed/full authenticated demo pass; DB confirms1 Delivered+2 typed financial archive facts+2 archive Inbox records. Real Rabbit financial replay/retry/DLQ/poison/backlog/restart, measured idle/busy/failed/stopped/disabled worker health, live broker recovery, SDK incident privacy and witnessed DB contention verified.26 added Integration cases; all original tests retained. Snapshot-observer test flakiness found by full suite was corrected using separate autocommit context, then targeted/repeated and full tests rerun.

No new Domain behavior/packages/projects, developer migrations, secret commits, remote push or deployment. See14_HARDENING_REVIEW.md and ADR-026 for findings/alternatives/risks. Remote CI remains PENDING REMOTE VERIFICATION (no remote/authorization), not PASSED. Human owner merge/deploy review remains external.

### Focused refactoring local gate — 2026-10-26

PASS: strict Release build (`--no-restore -m:1 -warnaserror`) 0 warnings/errors; solution whitespace and warning-level analyzer verification pass. Baseline428 tests passed before editing; final431 passed (257 Domain +174 Integration), 0 failed/skipped. Three added PostgreSQL rollback cases passed before and after private-method extraction. Targeted ordering/transaction/payment/recovery44 and identity/startup/realtime34 passed; concurrency19 repeated after full regression, all passing. Real Testcontainers PostGIS/RabbitMQ used. SaveAsync transaction/lock/save/commit ownership and bearer callbacks compared to baseline; no Domain/schema/migration/package changes. Recovery/service splitting rejected for lack of demonstrated benefit. Exact commands, reviewed files and limits are recorded in07_PROGRESS_AND_HANDOFF.md. Remote CI remains unverified, no push/deployment.

### New hardening pass — Phase 0 blocked (2026-10-26)

Initial full baseline431 passed (257 Domain+174 Integration),0 failed/skipped; restore/build/formatter passed. Requested message correction had a failing-first new HTTP test then Operations7/7 passed. Hygiene build0 warnings/errors, full formatting verification and Domain257 passed. Post-hygiene Integration run had19 passed/156 failed at Docker fixture startup; independent Docker version/ps probes return HTTP500, including API1.41. Gate is BLOCKED, not passed. Do not proceed to Phase1 or claim432 green until real infrastructure recovers and all tests rerun. No tests skipped/weakened; no container/data deletion/restart without owner permission.15_HARDENING_PASS.md inventories coverage and decisions.

### Hardening Gate0 recovery — 2026-10-26

VERIFIED after a single explicitly authorized normal Docker Desktop restart. No persistent service failed automatic recovery; no existing container/image/volume/data deletion or manual state changes. Full real-infrastructure recheck432 passed (257 Domain+175 Integration),0 failed/skipped, exit0. The earlier Docker HTTP500 failures remain documented; they are not reclassified as passes. Phase0 restore/build0 warnings/errors/full formatter/unchanged dependency graph/inventory complete. No schema changes. Phase1 begins with tests only; expected-red drivers must be reported separately from passing regressions.

### Hardening Gate1 — expected-red driver milestone

Tests-only phase: Release build0 warnings/errors; targeted14 new cases10 passed/4 expected failed, full257 Domain+185 Integration passed with4 explicit Phase2 drivers failing,0 skipped. Existing432 cases pass. This establishes failure evidence, not a green production gate. Phase2 must turn all four drivers green before its gate. No schema/production changes or existing test modifications. See15_HARDENING_PASS.md.

### Hardening Gate2 — VERIFIED

Release build0 warnings/errors, full formatter/analyzer verification exit0, full real-infrastructure solution tests499 passed (258 Domain+241 Integration),0 failed/skipped. All four preserved Phase1 drivers green. Includes deterministic payment races, assignment fences, GPS transactional/replay/throttle boundaries, publish/complete failure classification, bounded retention/relay and PostgreSQL lock contention, aborted requests and proxy spoofing. Six EXPLAIN ANALYZE BUFFERS plans over100000 orders/jobs committed before/after one evidence-driven partial index. EF model drift clean; two additive migrations, all17 historical migrations preserved. Exact commands in07_PROGRESS_AND_HANDOFF.md. Gate5 fresh migration/bootstrap smoke still pending; no operational-readiness or remote CI claim.

### Hardening group3A — VERIFIED

Named fixed vocabularies preserve stored/wire strings; seven actual PostgreSQL dispatch-vocabulary round trips pass and EF drift is clean. DomainException pure shared-namespace move retains every existing exact-exception assertion and HTTP/error mapping. Strict Release build0 warnings/errors,258 Domain+248 full Integration=506 passed,0 failed/skipped. No schema/package change in3A. Continue group3B; exact commands/results in07_PROGRESS_AND_HANDOFF.md.

### Hardening Gate3 — VERIFIED

Groups3A–3D complete under approved exceptions. Full Release solution test run520 passed (260 Domain+260 Integration),0 failed/skipped. All slice builds0 warnings/errors; final isolated style/projection rebuild260 Domain+20 real clock/GPS tests passed; full formatter recheck exit0 after correcting one indentation and two unused imports. Actual query-order tests passed before/after effects extraction; lock helper preserves tracked-root CAS and sorted ordering; nearest-courier SQL compared exactly; real mandatory-return test proves original remains unacked and redelivers after reconnect. Role IDs/stamps and currency CHECK produce no EF drift; no Phase3 schema/package change. Exact commands and rejected speculative changes recorded in07_PROGRESS_AND_HANDOFF.md. Final clean-source/migration/smoke gate remains Phase5.

### Hardening Gate4 — VERIFIED

Documentation diff reviewed: README settings/limitations/test paths, ADR-027 (avoids duplicate014), changelog and all-item matrix; current type/method references updated. Local README/ADR links and diff whitespace pass. Strict Release -warnaserror build0 warnings/errors;260 Domain passed. No runtime/schema/package change; final regression, fresh migration and owned Compose smoke remain Gate5 work.

### Hardening Gate5 — VERIFIED

Final restore0, strict Release -warnaserror0 (0 warnings/errors), full formatter/analyzer check0, model drift0/no changes. Full solution520 passed:260 Domain+260 Integration,0 failed/skipped. The final command uses checked-in Integration runner cap2 (diagnostics confirmed); all explicit concurrency/lock/race tests remain enabled. Earlier unrestricted broker-budget failures and diagnostic/isolated reruns are retained in07 and15, not relabeled as green. No assertions, existing timeouts or production algorithms changed to obtain the final result.

Fresh Verify-Migrations.ps1 exit0 with all19 migrations; original17 unchanged and only two approved additive migrations. New owned Compose image/project migration/seed/readiness/public HTTP/full delivered-captured demo passed;24 durable queues/exchange and durable financial facts checked. Original .env unchanged, original three services healthy/unless-stopped; owned smoke services stopped without deleting volumes/data. Final independent production diff review found no further confirmed defect; package versions/resolved graphs unchanged. Documentation matrix complete and local Git clean after final evidence commits. Exact commands/results/limitations in15_HARDENING_PASS.md. No remote push, PR, merge, deployment or remote CI; remote CI PENDING REMOTE VERIFICATION. All requested hardening phases complete; stop here.

## Final release baseline — 2026-11-05
Clean47c658a; no remote or owner edits. Restore0; strict Release0 warnings/errors;260 Domain+260 real Integration passed,0 failed/skipped (Integration5m14s). Original running MeetWise services healthy; stopped OrderFleet resources preserved.771 historical blobs scanned without matching current local secret values or high-confidence key/token signatures. Archive exposure and original audit availability remain separate publication gates. Current findings and evidence:16_RELEASE_VERIFICATION.md. Latest owner request authorizes a private GitHub repository/push only after local/security gates.

## Confirmed defects — verified
F-01/F-02 regression run failed2 and passed1 before production edits. Fix: broker unavailability retains capped durable retries indefinitely; invalid envelopes retain the permanent budget. Lease/deadline CAS is unchanged; attempt counter saturates safely. Reconciled capture stages Captured fact and Succeeded attempt before refund in the same existing SaveChanges/Inbox transaction. Both race interleavings and duplicate archive/cancellation processing retain one provider effect and correct fact order. Strict Release0 warnings/errors;44 focused payment/archive/Outbox/operator tests passed,0 failed/skipped. Existing permanent-dead test now uses InvalidEnvelope; historical operator replay explicitly seeds the old BrokerUnavailable dead state and retains all assertions. Diff reviewed. No migration or Domain edit.
Existing live-Admin paged diagnostics (max50) plus per-message audited/idempotent retry already provide bounded recovery for historical dead messages. A new unfiltered bulk revive would risk replaying invalid envelopes and is not justified by a measured operational need; no bulk endpoint added. Pending/dead counts, oldest lag and retry reason metrics already provide alert inputs. New outages recover automatically after more than the old budget without operator replay.

## Risk investigation — verified
R-01: corrected synthetic history to include expired deadlines and completed/cancelled orders. Actual six EF recovery queries plus nine actual OperationsSummary queries explained over100k active,300k active and100k idle histories. Committed45 before and45 after plans. PostgreSQL did not use the predicted deadline-first per-row scan, but non-leading status indexes walked retained history. Additive migration20261104215503_IndexActiveOrderRecovery creates only small Created(Id) and Accepted(AcceptedAt,Id) partial indexes. All19 historical migrations preserved. Active expiry100k/300k:0.855/2.837ms before,0.568/0.566ms after; dispatch1.109/2.327ms before,0.228/0.212ms after. Idle expiry0.928→0.040ms, dispatch0.213→0.023ms. Access-path evidence, not a production SLA; cache/load affect timing.
R-02 DISPROVEN WITH EVIDENCE: both nearest-row-locked and nearest-becomes-unavailable-after-statement-snapshot scenarios select the next eligible courier. Snapshot test witnesses an actual PostgreSQL advisory-lock wait and commits availability change before releasing the query. Existing two-session contention witness remains. No dispatch algorithm change.
R-03 ACCEPTED DOCUMENTED LIMITATION: dead-count plans are already index-based and cost at most1.1ms in the measured retained data; no dead-letter index justified. Other exact unsettled-cancellation counts still join retained Orders/Payments (39/246ms before,24/59ms after at100k/300k; timing variation is not attributed to the new indexes). Idle cancellation recovery still scans retained cancellation facts (272ms before,85ms after at100k). Five-second operations sampling and up-to10s idle recovery bound frequency, not production-scale cost. Keep consumption-safe cleanup and document load testing before production; no guessed index/denormalized business flag added.
R-04: six of seven stale-Admin HTTP cases failed first; operations already checked live roles. Remove revoked Admin claims at token validation using authoritative DB roles. ETA now applies the same supported-role guard as other order reads; retained exact403 regression passes. R-05: four boundary drivers failed first then passed with configurable per-actor create10/write60/read120 and hub60 per minute defaults. Hub budget is shared across connections. Credential/health limits retained, no queuing. Strict Release0 warnings/errors;46 identity/proxy/hub/Admin/limit/contention cases passed.260 Domain+29 recovery/operations/GPS/outage regressions passed. Real outage strengthened to15 failures against the old10-attempt default, then successful publication on attempt16; passed. Existing assertions/timeouts/skips unchanged. One accidental overlapping build failed on a locked test DLL before execution; serialized rerun produced the recorded results. Full cleanup/final suite still pending.

## Deployment and local credential security — verified
Six new production configuration cases failed first. Non-Development/Testing hosts now require explicit Demo:Enabled acknowledgement, explicit non-wildcard host names and an HTTPS JWT issuer. All responses label payments as mock. Non-local hosts use HSTS and HTTPS redirect; existing explicit trusted-proxy processing precedes those controls. Default AllowedHosts is localhost/127.0.0.1. No real provider introduced. Strict Release0 warnings/errors;38 deployment/startup/proxy/demo provisioning tests passed,0 failed/skipped. Original JWT algorithm/key/lifetime validation retained.
Owner confirmed the reviewed archive contained .env outside the trusted machine and authorized coordinated local rotation. Inventory identified two stopped OrderFleet PostgreSQL services, one stopped RabbitMQ and a stopped stateless API; no external service credential was configured. Changed PostgreSQL role passwords in both existing databases using SCRAM, RabbitMQ password via its local management API, all six owned demo password hashes/security stamps, and .env JWT key. New database/Rabbit credentials verified, old credentials rejected. Real temporary API login/new JWT200; old demo password and old-key signed JWT401. All user-table row counts preserved. Only the stateless owned API container was replaced without starting it, to remove old JWT/credential configuration; DB/Rabbit containers and persistent volumes retained. Services temporarily started solely for rotation were stopped again. MeetWise services/configuration/data were not changed and remain healthy. Current .env holds new private credentials and stays ignored. No claim is made about reuse in unknown external environments.
Two incomplete attempts are retained: initial PostgreSQL verification used the container loopback trust rule and was correctly rejected as evidence; corrected verification uses the container's network address and requires password authentication. A compose create flag unsupported by the installed CLI then stopped the workflow; corrected to up --no-start --no-deps --force-recreate for the owned stateless API. The final complete verification succeeded; no failed attempt is labeled successful. No secrets printed, copied to technical archives, staged or committed. No Docker engine restart, data deletion, database reset or unrelated service mutation.
