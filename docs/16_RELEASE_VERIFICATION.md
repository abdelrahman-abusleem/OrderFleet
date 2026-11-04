# Release verification

Date:2026-11-05. Starting source:47c658a. This report records findings verified against actual implementation, executed local checks and remaining publication gates. The independent source audit was static; its recommendations were reproduced or measured before changes.

## Findings and disposition

| Finding | Disposition | Evidence/change |
|---|---|---|
| F-01 broker outage exhausts valid-message budget | FIXED AND VERIFIED | Failing-first real-broker driver;15 failed publishes beyond prior10-attempt budget, successful publication on16. Lease CAS and2..300s delayed retry retained; invalid envelopes still dead-letter. No business/financial event discarded. |
| F-02 missing reconciled capture history | FIXED AND VERIFIED | Capture-first driver failed at2 rather than3 archive rows. Settlement now stages capture fact/succeeded attempt before refund in the same existing save/Inbox transaction. Both interleavings, stable provider effects and duplicate archive/cancellation processing pass. |
| F-03 publication hygiene | FIXED AND VERIFIED locally; hosted verification pending | Source/comment cleanup, technical documentation, LF policy, migration preservation, secret/dependency scans and complete local release gates pass. Publication remains a separate gate below. |
| R-01 history-sensitive recovery selection | FIXED AND VERIFIED for measured Created/Accepted paths | Corrected historical deadlines/distribution,100k/300k active and100k idle plans. New migration20 adds only narrow partial active-order indexes. Other cancellation-history cost remains explicitly limited below. |
| R-02 SKIP LOCKED/LIMIT candidate loss | DISPROVEN WITH EVIDENCE | Locked nearest and nearest-unavailable-after-snapshot tests both choose next courier. Actual PostgreSQL wait witnessed; dispatch algorithm unchanged. |
| R-03 frequent dead-count scans | ACCEPTED DOCUMENTED LIMITATION | Existing indexed dead-count costs at most1.1ms in measurements; no guessed index added. Other exact summary/cancellation queries remain history-sensitive. Five-second sampling is preserved; no production capacity claim. |
| R-04 stale privileged JWT roles | FIXED AND VERIFIED | Six Admin paths failed first; operations already rechecked. Privileged claims now intersect current grants; no old-token expansion. Identity mutation rechecks after actor locks. |
| R-05 authenticated/hub abuse | FIXED AND VERIFIED | Four failing-first boundaries; configurable per-actor create10/write60/read120/hub60 per minute. Shared hub budget across connections, exact429 and no extra order persisted. Route-case bypass reproduced and corrected. |
| CORS and browser SignalR | FIXED AND VERIFIED | Empty default/exact validated allowlist, HTTPS outside local/test, no wildcard credentials; actual preflight and WebSocket Origin allow/deny tests. |
| Role and membership onboarding | FIXED AND VERIFIED | Restricted current-Admin non-Admin role APIs; current scoped-Owner memberships. No self-promotion/public Admin. Ordered user/restaurant locks, immutable identifier-only audit, repeated-write and revocation-race tests. |
| Dashboard order discovery | FIXED AND VERIFIED / existing customer path preserved | Existing customer/restaurant list confirmed. Added paged own active courier snapshots/fences; foreign/role/paging/release/privacy tests. |
| Mock deployment, HSTS and hosts | FIXED AND VERIFIED | Six production cases failed first, guards/labels/HSTS/restricted hosts added; HTTPS issuer and origin validation. No real provider introduced. |
| Unnecessary comments | FIXED AND VERIFIED |187 C# trivia,3 PowerShell and4 configuration/SQL comments removed. Post-format executable token/directive proof passes, including assertions. Migrations/tool/legal markers preserved. |
| Instruction/process artifacts | FIXED AND VERIFIED | Removed seven process documents and two obsolete plan outputs; preserved a nonsecret local archive. Current source inventory and technical links pass review. Historical commits are preserved. |
| Line endings and hosted CI parity | FIXED AND VERIFIED locally; hosted verification pending | Explicit LF policy, clean whitespace, full analyzer/format verification and pinned local EF tool pass. Hosted jobs now include these checks. |
| Exposed local archive credentials | FIXED AND VERIFIED for identified local scopes | Both initialized PostgreSQL stores, RabbitMQ, six owned demo password hashes and JWT configuration rotated. New access succeeds, old passwords/key rejected. No record/volume deletion or MeetWise changes. Unknown reuse outside configured local services is not certified. |
| GitHub publication and hosted CI | PENDING | Authentication/destination verified; no creation/push until remaining local gates pass. Private visibility required. |

Existing live-Admin paged diagnostics (max50) and audited/idempotent per-message retry provide bounded recovery for historical dead rows. An unfiltered bulk revival was rejected: repaired eligibility must remain explicit, and no measured operational need justified a new endpoint. Pending/dead counts, oldest lag, retry reasons and worker timestamps provide alert inputs. Consumer dead queues remain controlled operator work; no lossy automatic eviction.

## Performance evidence

Actual six EF recovery queries and nine OperationsSummary queries were captured and explained against disposable representative datasets. [Before](evidence/release-recovery-before.txt) and [after](evidence/release-recovery-after.txt) each contain45 ANALYZE/BUFFERS plans. The old future-deadline fixture was corrected; historical orders are completed/cancelled with expired deadlines and few actionable rows.

| Query/workload | Before ms | After ms |
|---|---:|---:|
| Expired creation,100k active |0.855|0.568|
| Expired creation,300k active |2.837|0.566|
| Due dispatch,100k active |1.109|0.228|
| Due dispatch,300k active |2.327|0.212|
| Expired creation,100k idle |0.928|0.040|
| Due dispatch,100k idle |0.213|0.023|

PostgreSQL did not choose the audit's predicted deadline-first per-row scan; the measured non-leading status index walk still grew with history. Migration20261104215503_IndexActiveOrderRecovery adds Created(Id) and Accepted(AcceptedAt,Id) partial indexes. All19 historical migrations remain unchanged in Git.

Exact unsettled-cancellation summary counts measured39/246ms before and24/59ms after at100k/300k. Idle cancellation recovery measured272ms before and85ms after at100k. These differences are not attributed to the new active-order indexes: load/cache affect timings. Those paths still inspect retained history. Consumption-safe retention,5s metrics sampling and up-to10s idle recovery bound frequency, not production-scale cost. Capacity/load validation remains required; no SLA or broad denormalized flag/index was invented.

## Executed local verification

Workspace SDK10.0.403/runtime10.0.14; Docker29.8.2; real PostGIS/RabbitMQ fixtures; original two-collection Integration cap preserved.

| Check | Actual result so far |
|---|---|
| Baseline restore/build/full tests | exit0;0 warnings/errors;260 Domain+260 Integration,0 failed/skipped |
| Confirmed-defect regression before fix |2 failed,1 passed; both defects reproduced |
| Corrected payment/Outbox/archive/operator group |44 passed,0 failed/skipped |
| Risk/security group |46 passed, plus260 Domain+29 recovery/GPS/operator cases;3 plan datasets passed |
| Production startup/proxy/demo group |38 passed; later28 option/environment-boundary cases passed |
| Frontend/identity/ownership/realtime/limit group |74 passed; all existing assertions retained |
| Source cleanup format and strict Release build | exit0;0 warnings/errors |
| Full regression after source cleanup |260 Domain+302 Integration=562 passed,0 failed/skipped; Integration4m02s |
| Final metadata/docs build/format/full regression | Strict Release and format exit0;260 Domain+302 Integration=562 passed,0 failed/skipped; Integration4m39s |
| Final model drift and fresh20-migration chain | No pending model changes; Verify-Migrations.ps1 exit0, all20 migrations applied and history queried on fresh disposable PostGIS |
| Fresh owned Compose smoke | Bootstrap/image build and full delivery journey passed; role/membership/relogin/revocation/private listing/CORS/mock-label checks passed;24 durable queues, durable topic exchange,20 migrations,1 Delivered order,1 Captured payment,2 financial facts and4 identity audits verified |
| Final source/history/dependency hygiene |949 Git blobs: no current local secret/high-confidence signature match or tracked .env; six projects: no known vulnerable direct/transitive packages;38 original migration files identical;15 Markdown files,0 broken local links/encoding errors,37 reference URLs successful |

Commands: `dotnet restore OrderFleet.slnx`; `dotnet build OrderFleet.slnx -c Release --no-restore -m:1 -warnaserror`; `dotnet format OrderFleet.slnx --verify-no-changes --no-restore --verbosity quiet`; `dotnet test OrderFleet.slnx -c Release --no-build --no-restore -m:1`. Migration/drift commands and targeted classes are in the [testing guide](05_QUALITY_GATES.md).

Failures retained: an overlapping build could not copy a test DLL held by the benchmark; serialized rerun resolved the orchestration error. Admin ETA initially returned privacy-aware404 after role removal; the supported-role endpoint guard was made consistent and the exact403 driver passed. Two additional review drivers reproduced create-route case bypass and stale-membership deletion eligibility before correction. A blank line left trailing whitespace in Bootstrap after comment removal was corrected. No assertion, timeout or skip was relaxed.

Local rotation required two corrections: loopback PostgreSQL trust was unsuitable evidence, so password verification used the container network address; an unsupported Compose create flag was replaced with owned stateless up --no-start --no-deps --force-recreate. Final verification confirmed both database old-password rejection/new access, Rabbit old401/new200, six updated demo hashes, HTTP new login/token200 and old password/signing-key401. All public-table row counts were preserved. Temporarily started owned services were stopped again; the stateless API remains stopped with new configuration. MeetWise remains healthy. No Docker engine restart or unrelated credential change.

No credential values are printed or committed. The local .env stays ignored. A nonsecret documentation/study archive was preserved outside the publishable repository before cleanup. History is preserved; deleted instruction files remain in historical commits rather than rewriting Git history.

## Remaining limitations and release status

Mock payments, heuristic ETA, one realtime API instance, indefinite request/Inbox identities, deep OFFSET cost, history-sensitive exact counts and unverified production-scale/operational capacity remain explicit limits. Local tests do not certify HA, real-money processing, OS image-CVE status or unknown external credential reuse.

Publication/remote CI are not yet claimed complete. The verified destination is the authenticated account's private OrderFleet repository; no unrelated remote may be overwritten. The final report must provide the actual repository URL, commit SHA, workflow URL, job conclusions and clean Git status after publication.

## Documentation cleanup verification
Removed seven instruction/install/planning/handoff/repeated-review files and two superseded plan outputs after preserving a nonsecret local copy outside the repository. Retained thirteen current technical Markdown guides/decision records plus README/changelog and current plan evidence. Rechecked actual endpoint/event/option names and original ADR identities. Fifteen Markdown files: zero broken local links/encoding errors;37 primary reference URLs responded successfully. Strict Release0 warnings/errors and260 Domain passed. Bootstrap's comment-removal blank-line whitespace was corrected. No C# implementation or assertion changed in this documentation slice. Git LF/CI parity and final regression/publication remain separate gates.

## Repository and CI preparation — verified
Explicit .gitattributes/.editorconfig LF policy normalized55 working files without semantic changes. All38 source/designer files of the original19 migrations match their original Git blobs; no massive source diff was introduced. Additional ignore rules exclude instruction/cache artifacts. Four redundant HTTP/environment-example comments were removed; required HTTP request delimiters remain. Final removal total:187 C# trivia,3 PowerShell comments and8 configuration/example/SQL comments (198 total).
Pinned the repository-local EF tool to10.0.4 without modifying global tools or runtime dependency versions. Tool restore/version and nonconnecting drift passed with no mismatch warning. Added fail-on-advisory Verify-Dependencies.ps1; all six projects report no known vulnerable direct/transitive packages from configured sources. This is not an OS image-CVE scan. CI now performs full formatting/analyzers, explicit strict Release, dependency audit/model drift and fresh migration verification alongside real integration/Compose checks. Final local format verification0, strict Release0 warnings/errors and260 Domain passed. Remote execution remains pending until the verified private push.

## Final chronology-test correction
The first full run after metadata cleanup passed260 Domain+301/302 Integration, with one failure in a new assertion added during this pass. That assertion incorrectly used physical Outbox identity sequence as financial chronology; EF legitimately reordered two inserts by their keys. The existing delivery contract explicitly permits reordering. Production already persisted the complete Captured/Refunded snapshots with correct provider timestamps. Corrected the new query to verify FinancialAuditRecords ordered by OccurredAtUtc and added an exact settled-fact timestamp assertion. Exact fact counts, state/order expectations, succeeded attempt, capture/refund provider effects and duplicate Inbox/archive checks remain. No existing pre-pass assertion was removed or relaxed and no production ordering mechanism was invented. Strict build0 warnings/errors and24 payment/archive cases passed. The failed full run is retained; the subsequent complete rerun passed all562 tests with no failures/skips.

## Final local gates — passed

The final serialized strict build, full format/analyzer check and complete solution tests passed after the chronology correction. Coverage includes the real PostgreSQL locking/revocation races, assignment fencing, both GPS representations, SignalR privacy, financial compensation/history, broker outage recovery and retention safeguards. No skips, test parallelism changes or weakened preexisting assertions.

The fresh Compose project orderfleet-release-ee2b4bb1b5df built the API, migrated and seeded a new database, then passed the supported HTTP journey and additional browser/identity checks. Durable database and broker outcomes match the verification table. Its three containers were stopped successfully; volumes and data remain. The root .env SHA256 was unchanged. Previously stopped OrderFleet stores remain stopped and MeetWise PostgreSQL/Redis remain running healthy; no unrelated service was modified. No restart, reset, prune or destructive cleanup was used.

The final source review confirms that Domain changes only remove comments, existing dispatch/recovery/transaction algorithms and lock ordering are unchanged, and the only schema addition is migration20's measured active-order indexes. Original19 migrations and their designers match all38 baseline Git blobs. Runtime dependency versions and the existing four production/two test project structure are unchanged. Source and historical secret checks disclose no values and are limited to the four configured local secrets and high-confidence signatures; they do not certify unknown external reuse or every possible secret pattern.

Local gates are complete. Private publication, remote tree verification and both hosted CI job conclusions remain required before the overall release is complete.

## Final diff review

Reviewed the complete baseline-to-release inventory and the changed Outbox eligibility/CAS, settlement facts, live-role intersection, identity row locks/audits, limiter partitions, Origin validation, deployment guards, private listing and partial indexes. Rechecked the existing transaction/save paths and all38 historical migration blobs. The review found no additional confirmed defect requiring a production change. No skipped tests, new runtime packages, EF InMemory substitution, Redis dependency, tracked .env or generated build outputs were found. Source comment removal was separately verified by executable-token/directive comparison; the final complete tests ran after cleanup. Seven process-document deletions are intentional and authorized; Git history is preserved.

Rejected speculative changes: a dispatch selection rewrite (contention risk disproven), guessed metrics indexes or denormalized recovery state (measurements insufficient), a bulk replay endpoint (existing bounded audited repair suffices), a real gateway or broad identity platform (outside scope), and additional file-length/clock/exception refactors (no confirmed benefit or incompatible contracts). Remaining measured capacity and demo limitations are documented above. The next action is the authorized private publication and observation of every hosted job, without deployment.
