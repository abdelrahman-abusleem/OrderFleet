# Hardening pass — observed evidence (2026-10-26)

Current checkpoint: Gate3 VERIFIED,520 passed (260 Domain+260 Integration),0 failed/skipped; Phase4 documents the result. Phase5 final regression/fresh migration/Compose smoke is pending. Earlier blocked/expected-red checkpoints below are historical evidence and are superseded only by their explicit later reruns. See the acceptance matrix at the end.

This pass follows the owner's attached Hardening & Clean-Code prompt. Its branch and commit conventions supersede the older phase conventions for this pass only. Scope is unchanged. Phase 0 started from clean commit 19b010b.

## Phase 0 — baseline and inventory

Baseline commands use workspace SDK10.0.403/runtime10.0.14 with DOTNET_ROOT and PATH selecting `.tools/dotnet`. Real Testcontainers use Docker access and never the developer database.

| Command | Result |
|---|---|
| `dotnet restore OrderFleet.slnx` | exit0 |
| `dotnet build OrderFleet.slnx -c Release --no-restore -m:1` | exit0,0 warnings/errors |
| `dotnet test OrderFleet.slnx -c Release --no-build --no-restore -m:1` | exit0,431 passed,0 failed/skipped |
| `dotnet format OrderFleet.slnx --verify-no-changes --no-restore --verbosity quiet` | exit0 before adding explicit style rules |

| Test project | Baseline cases |
|---|---|
| tests/OrderFleet.Domain.Tests | 257 |
| tests/OrderFleet.IntegrationTests | 174 (includes actual PostgreSQL/PostGIS/RabbitMQ, HTTP/SignalR and some isolated component cases) |

| Requested scenario | Observed coverage / gap |
|---|---|
| Capture racing cancellation, both orderings | Partial: PaymentWorkflowTests.CaptureCommittedBeforeLocalSave_CancellationReconcilesAndRefunds (known/lost response), CatalogOrderingApiTests.ConcurrentAcceptAndCustomerCancel_PersistOneLegalWinner. Missing deterministic both-interleaving test asserting financial archive and durable compensation obligation. |
| Two couriers/one order; one courier/two orders | DispatchConcurrencyTests.TwoWorkersAndTwoCouriers_AssignOrderOnce_LeavingOtherCapacityAvailable; TwoOrdersCompetingForOneCourier_ReserveOnlyOneCapacity; DispatchContentionTests.TwoDatabaseSessions_AreProvablyBlockedTogether_BeforeCompetingForOneCourier. |
| Stale assignment after release/reassign | DispatchConcurrencyTests.DuplicateDispatch_AndSameCourierReassignment_FenceOldCommands; OrderConcurrencyTests.StalePickup_CannotResurrectOldFence_AfterSameCourierReassignment. |
| Publish succeeds, marking published fails | Partial: RabbitDeliveryTests.PublishBeforeMark_AndConsumerCommitBeforeAck_ReplayWithoutDuplicateSettlement tests the crash gap. Missing actual CompleteAsync-throws relay classification test. |
| Two relays/SKIP LOCKED, per-aggregate ordering | Partial: OutboxLeaseTests.ParallelClaims_AreDisjoint_AndExpiredWorkerCannotCompleteNewLease proves parallel DB claims/fencing. No two-relay publication-order test. Current SQL orders claims by Sequence but does not serialize publication across relays; existing contracts use aggregate sequences/stale-event handling rather than claiming ordered broker arrival. |
| Inbox redelivery idempotency | RabbitDeliveryTests.PublishBeforeMark_AndConsumerCommitBeforeAck_ReplayWithoutDuplicateSettlement; FinancialArchiveTests.CommitBeforeAck_RedeliversAndArchivesOneFact_WithoutChangingPayment. |
| Identical/older GPS observation | CourierSpatialTests.Locations_AreOwnedOrderedAndBounded_AndDiscoveryFiltersStateAndFreshness and CourierTests.Location_IsStrictlyOrdered_AndRejectedWritePreservesProfile reject same timestamp/different coordinates and older observations. Missing same timestamp/same coordinates no-op and per-case Outbox counts. |
| Request cancellation through API | Missing. HttpErrorMappingTests covers other exceptions; no RequestAborted characterization. ASP.NET middleware may already intercept aborted requests before custom handlers; verify actual runtime before assuming a bug. |
| Admin key replay with changed request | OperationsApiTests.DeadOutboxRetry_IsConcurrentIdempotentAudited_AndCannotStealActiveLease verifies changed target/same key409 and stable audit. |

Hygiene findings: required ignore patterns already present; `git ls-files` found no tracked bin/obj/TestResults/.vs/log/user files, so no untracking was necessary. Existing Nullable/ImplicitUsings already enabled per project; global analyzers and warnings-as-errors already enabled with a zero-warning baseline. Added explicit C#14/determinism/shared defaults and style rules. Historical migrations retain existing encoding/namespaces and are excluded from the new namespace/unused-import style diagnostics. No migration edits or schema changes. Full formatter changes are confined to unused imports and committed separately from central package metadata. Direct package versions and resolved dependency-library names/versions are checked against pre-change assets for all six projects; no upgrades.

The requested validation typo matched current code. MissingIdempotencyKey_ExplainsMaximumLength was added first and failed against the original message, then passed after the single-space correction; OperationsApiTests7/7 and Release build passed. Commit4edc628.

## Verified mismatches and decisions

- RecoveryOptions.PollMilliseconds is already configurable and validated; idle backoff remains a gap. Do not add duplicate interval options.
- README/ADR-022 already document single-instance realtime and required tested backplane/fanout for scale-out; improve the concrete upgrade path only if needed.
- New requested file docs/adr/ADR-014-hardening.md must explicitly avoid superseding the existing ADR-014 monetary-range decision.
- D1 GPS thresholds, D2 retention/replay window, D3 legacy missing-courier handling and D4 clock replacement require the owner's explicit choices. D5 GUID generation remains out of scope.
- Section3.7's dedicated limit exception conflicts with current exact ArgumentException assertions and ADR-013's established argument contract. Under the prompt's no-existing-test-change rule, ask before that operation; preserve all assertions until a decision.
- Idempotency currently has no expiry (ADR-016/017). Deleting rows after7 days cannot be called behavior-preserving or longer than an infinite replay window. D2 must define an explicit new bounded replay contract or retain these rows; unbounded manual broker replay also affects Inbox deletion safety.

Later gates, exact commands, expected-failing drivers and item-by-item completion are appended as executed; no future work is claimed complete here.

## Phase 0 — current gate report

- Branch / commits: hardening/p0-baseline;4edc628 (typo/test),bf777db (dedicated formatting/import cleanup). Central package/build metadata and this inventory are the next local slice.
- Tests added/changed: MissingIdempotencyKey_ExplainsMaximumLength failed first on the requested typo, then passed; no existing assertion changed. Formatting removes unused imports only.
- Commands: `dotnet restore OrderFleet.slnx` exit0; `dotnet format OrderFleet.slnx --no-restore --verbosity quiet` exit0; `dotnet build OrderFleet.slnx -c Release --no-restore -m:1` exit0,0 warnings/errors; `dotnet format OrderFleet.slnx --verify-no-changes --no-restore --verbosity quiet` exit0; `dotnet test tests/OrderFleet.Domain.Tests -c Release --no-build --no-restore` exit0,257 passed. Full `dotnet test OrderFleet.slnx -c Release --no-build --no-restore -m:1` after hygiene: exit1,257 Domain passed; Integration19 passed/156 failed/0 skipped. These failures occur during fixture startup with Docker API HTTP500, not business assertion failures. Baseline earlier in the same pass was431 green.
- Findings not matching prompt: interval already configurable; single-instance SignalR limitation already documented; no tracked build/user artifacts; several required hygiene properties already exist. Per-aggregate publication ordering is not an established guarantee across concurrent relays.
- Schema changes: none; no EF mappings or migrations changed. Direct package versions (24) and resolved package-library names/versions in all6 projects match baseline.
- Decisions needed: Docker Desktop recovery interrupts unrelated containers, so owner permission or owner-operated recovery is required. Later D1–D4 and the section3.7 test/exception contradiction remain pending their applicable operations.
- Not done/deferred: Phase0 full integration recheck and gate completion BLOCKED on Docker. Phases1–5 NOT_STARTED; no driver test is falsely marked passed or skipped. No push, merge, PR, deployment, container reset or data deletion.

## Docker recovery and Gate 0 — verified

Owner authorized one normal Docker Desktop restart, explicitly allowing last-known service names because API HTTP500 prevented live pre-restart running state/health/restart-policy verification. Recorded this limitation before restart in ignored local `.tools/docker-restart-precheck.json`. Executed `docker desktop restart --timeout 120` exactly once: exit0. No Windows reboot, container reset/recreation, cleanup/prune, volume/image/data deletion or manual service start/stop.

`docker version --format '{{.Server.Version}}'` returned29.8.2. Read-only full inventory and per-container state/policy inspection found orderfleet-postgres,meetwise-postgres,meetwise-redis automatically running/healthy with unless-stopped policies, matching their last-known healthy states. All24 exited255/no-restart containers were confirmed org.testcontainers=true (PostGIS/RabbitMQ); left untouched. No known persistent service failed to recover. Ignored local before/after evidence retained.

Resumed `dotnet test OrderFleet.slnx -c Release --no-build --no-restore -m:1 --logger 'console;verbosity=minimal'`: exit0,257 Domain+175 Integration=432 passed,0 failed/skipped; Integration duration1m13s. This supersedes the blocked post-hygiene run without hiding that failure. All Phase0 hygiene/build/format and real-infrastructure verification now pass. No schema changes. Branch hardening/p0-baseline commits4edc628,bf777db,11f0c6f plus this evidence update. Gate0 VERIFIED; proceed to Phase1 tests only. D1–D4 and the section3.7 contract contradiction remain for applicable later operations.

## Phase 1 — safety-net gate (tests only)

Branch hardening/p1-safety-net. Added14 cases across CaptureCancellationRaceTests,CourierReplayTests,OutboxRelayReliabilityTests,RequestCancellationTests,RoleSeedIntegrityTests,HubLogPrivacyTests. No production code, existing tests, dependencies, mappings or schema changed. Build Release/no-restore/-m:1 exit0,0 warnings/errors. Full formatter was applied only to the six new files before build.

Tests-first targeted command: `dotnet test tests/OrderFleet.IntegrationTests -c Release --no-build --no-restore --filter 'FullyQualifiedName~CourierReplayTests|FullyQualifiedName~RoleSeedIntegrityTests|FullyQualifiedName~OutboxRelayReliabilityTests|FullyQualifiedName~CaptureCancellationRaceTests|FullyQualifiedName~RequestCancellationTests|FullyQualifiedName~HubLogPrivacyTests'`: exit1,10 passed/4 expected failures/0 skipped. Full `dotnet test OrderFleet.slnx -c Release --no-build --no-restore -m:1`: exit1; Domain257 passed,Integration185 passed/4 expected failures/0 skipped (189 Integration,total446). All432 previous cases remain passing.

Expected-failing Phase2 drivers (explicit Phase=2 traits, never Skip):
- CourierReplayTests.LocationReplay_PreservesProjectionAndEmitsOnlyAcceptedChanges(Replay): expected204/0 new events; actual409. Other same-timestamp/different-point,older,newer cases pass and verify profile/geography equality, microsecond normalization and event deltas.
- CourierReplayTests.UnassignedLocation_PersistsBothRepresentationsWithoutOutboxEvent: location/projection persist but unwanted Outbox fact exists.
- OutboxRelayReliabilityTests.ConfirmedPublication_CompletionFailureRetainsLease_AndRedeliveryArchivesOnce: actual confirmed Rabbit publish then injected DB-completion error invokes FailAsync once; expected0. The test continues through real lease recovery/duplicate broker delivery and atomic financial archive+Inbox once the driver is fixed. Genuine publication failure/backoff characterization passes.
- RequestCancellationTests.ExceptionHandler_AbortedRequestWritesNoErrorOrBody: direct custom handler currently writes500; expected no error/body. API-boundary tests show the runtime already intercepts RequestAborted as499 with no error logs, whereas an unrelated OperationCanceledException remains500. Report this narrower actual defect rather than claiming every client abort currently becomes500.

Passing characterizations: deterministic capture-first and cancellation-first interleavings use task barriers, no scheduling sleeps; durable cancellation obligation, terminal Order/Payment, provider effects, financial Outbox, FinancialAuditRecords and Inbox replay are asserted. Stable5-role seed IDs pass against old code and fail if a future role lacks its explicit expected ID; current seed content is valid (positional coupling remains the Phase3 risk). Actual hub negotiation/WebSocket handshake and rejected anonymous negotiation keep valid and invalid query tokens out of captured application logs. All characterizations passed before any relevant production fix.

Mismatches: runtime already handles the observed aborted API request; current stable role seed IDs are valid, so neither characterization is incorrectly labelled red. No schema changes. Gate1 DRIVER_ESTABLISHMENT_COMPLETE under the owner's explicit expected-red rule; production correctness gate is still red until Phase2. D1,D2,D3,D4 and section3.7 exception/test contract questions sent to owner; dependent work waits, independent Phase2 fixes proceed. No test weakened/deleted/skipped; no old container/data changes.

## Phase2 item2.1 completed

Commit0c39456 preserves and verifies the interrupted relay classification fix (27 targeted green). Follow-up adds configurable Outbox:BatchSize5 (1..100,lease budget validated), injectable bounded jitter and explicit worker reconnect loops. New options tests failed first at runtime; new retry API tests were written first and initially failed compilation before the API existed. Release build0 warnings/errors; retry/options/relay/lease/live-worker/progress/startup/Rabbit42 passed. Earlier options/startup/relay18 passed. No schema changes, no skipped/weakened tests. Full phase gate remains pending GPS and cancellation drivers plus remaining Phase2 work. Approved owner decisions are recorded in08_DECISION_LOG.md.

## Phase2 item2.4 completed

Conservative D2 retention implemented for published Outbox, resolved recovery failures and old finished-order dispatch attempts only. Request/Inbox/financial/audit/business identities remain indefinitely. Consumer evidence and settled compensation are additional deletion prerequisites, preserving recovery obligations. Bounded DELETE repeats current eligibility outside its limited key subquery so PostgreSQL rechecks a concurrently reopened failure after lock waits. Eight new real-infrastructure tests passed (initial missing-type build failed first; an invalid synthetic envelope fixture was corrected after first executable4/5 run). Defaults and exact safety behavior are recorded in07_PROGRESS_AND_HANDOFF.md. No schema change. Full Phase2 gate still pending remaining items.

## Gate2 — VERIFIED

All Phase2 items complete under accepted D1–D4/section3.7; no additional approval needed. Full formatter exit0, strict Release build0 warnings/errors, full real-infrastructure solution499 passed (258 Domain+241 Integration),0 failed/skipped. All four original failing drivers preserved and green. Six100k-order/job query plans in docs/evidence; only the observed Payments sequential scan led to an additive partial index. GPS checkpoint is the other additive migration; all17 older migration files preserved. Detailed options, failed-first tests, corrected fixture/setup issues, review correction and exact slice results are in07_PROGRESS_AND_HANDOFF.md. Commit on hardening/p2-reliability and proceed to Phase3. Gate5 fresh full migration and compose smoke remains pending, not implicitly passed.

## Gate3 — VERIFIED

Groups3A–3D complete in separate buildable commits on hardening/p3-clean-code. Full520 passed (260 Domain+260 Integration),0 failed/skipped, strict Release0 warnings/errors. Full formatter exit0 after isolated indentation/import correction; final projection/clock build plus260 Domain/20 targeted real cases green. Query-order characterizations, exact moved-body/SQL comparison, lifetime test, multirole authority cases, product availability and actual failed-retry-publication redelivery augment existing regressions. No Phase3 migration/package change; role/currency mapping drift clean. Rejected changes and approved ArgumentException/IClock exceptions are recorded in07_PROGRESS_AND_HANDOFF.md. Final migration and fresh Compose smoke are still pending Phase5, not inferred from unit/integration success.

## Current acceptance matrix

Status at Gate4: done unless an explicit approved alternative/mismatch is named. Phase5 stays pending until executed. Slice commands and actual failures/corrections are in07_PROGRESS_AND_HANDOFF.md; original test assertions/drivers remain preserved.

| Item | Status and evidence |
|---|---|
| 0.1 baseline | Done:431 initially passed; Docker outage/recovery recorded,432 post-hygiene green. |
| 0.2 inventory | Done: initial matrix above; actual parallel claims/fences and stale-sequence consumers verified. Strict broker arrival/publication ordering across concurrent relays is not an established guarantee and was not invented. |
| 0.3 hygiene | Done: ignores verified, no tracked artifacts, same24 package versions centralized, SDK/build/style metadata, typo failed first then passed. |
| 1.1 capture/cancel | Done: two deterministic barrier interleavings, provider/local/archive/Outbox/Inbox/compensation assertions. |
| 1.2 GPS replay | Done: identical/different/older/newer cases plus event counts and both representations. |
| 1.3 relay classification | Done: real confirmed publish with injected Complete failure and duplicate financial archive replay. |
| 1.4 request abort | Done: direct handler driver plus actual API499 characterization; unrelated cancellation remains500. |
| 1.5 role seeds | Done: all fixed IDs/names/stamps checked independently before/after map extraction. |
| 1.6 hub privacy | Done: actual negotiate/WebSocket and rejected query tokens absent from captured logs. |
| 2.1 relay/batch/backoff | Done: separated completion failures, validated batch/lease budget, deterministic injectable jitter, explicit reconnect loops. |
| 2.2 GPS throttle | Done:25m OR10s, active assignment only, durable fence/checkpoint, transactional rollback and boundaries. |
| 2.3 replay no-op | Done: same normalized observation returns without Save/event; conflict/order checks retained. |
| 2.4 cleanup | Done under D2: only three approved technical tables, strict7/30/30 floors, bounded deletes, consumption/compensation/reopened-failure guards. Request/Inbox expiration deliberately excluded by owner decision. |
| 2.5 cancellation handler | Done: aborted requests produce no500/error/body; unrelated cancellations unchanged. |
| 2.6 recovery | Done: existing interval retained, idle cap10s/reset on work, actual six-query100k-order/job before/after plans; only measured partial payment index added. |
| 2.7 missing courier | Done under D3: retain standalone leniency, safe OrderId warning, no throw. |
| 2.8 proxy limits | Done: disabled default, configured IP/CIDR trust, one hop, no/0, real spoofing/partition/startup tests. |
| 2.9 SignalR | Done: existing single-instance limitation extended with concrete backplane/affinity/registry/fanout acceptance; no backplane implemented. |
| 2.10 registration timing | Done: accepted tradeoff documented; no identity behavior change. |
| 3.1 vocabularies | Done: constants retain strings,7 PostgreSQL round trips, no model drift. Broad replacement script rejected before execution; explicit reviewed patches used. |
| 3.2 exception placement | Done: shared Domain.Common, same implementation/exact assertions/HTTP mapping. |
| 3.3 order locks | Done: shared load/xmin and tracked-root lock-only helpers, identical SQL, existing sorted/actor-first order. |
| 3.4 transition effects | Done: named internal collaborator, same context/transaction/save ownership,3 actual courier→job→history characterizations and existing multi-fact rollback. |
| 3.5 dispatch structure | Done: SQL compared to baseline, candidate claim isolated, retry versus reservation staging named. |
| 3.6 capabilities | Done: creation/transition/query ports share one scoped EF store; lifetime isolation tested. |
| 3.7 validation | Done: private collection/pricing helpers; dedicated exception deliberately skipped under approved section3.7, exact ArgumentException/mapping retained. |
| 3.8 command structure | Done: explicit cancellation role priority with5 actual HTTP cases. Acceptance rewrite skipped: actual source already has clear before/after-capture deadline/compensation logic. |
| 3.9 broker processor | Done: handling/retry/ACK steps, explicit hosted worker names; compatible old direct-call overload. Inbox stays in atomic handlers. Real failed mandatory retry copy redelivers original. |
| 3.10 ETA composition | Done: unchanged route/auth/response moved; actual ETA/realtime/full journey/privacy cases green. |
| 3.11 catalog | Done: initial availability once, default preserved,2 new Domain cases; honest internal snapshot flag with unchanged API error outcome. |
| 3.12 stable role map | Done: byte-identical GUIDs/stamps, no seed migration. |
| 3.13 clock registration | Done: TimeProvider registration in composition, original lifetime/override tested before/after. |
| 3.14 clock abstraction | Done under D4: retain documented IClock wrapper; broad replacement/testing package deliberately excluded. |
| 3.15 projection | Done: intentional spatial projection comment/decision, new-in-pass equality/replay/stale-writer transaction tests; neither representation removed. |
| 4.1 README | Done: tests, cleanup/settings, retention, GPS, mock provider, single-instance realtime and deep OFFSET limitations. |
| 4.2 ADR | Done: ADR-027 used because014 already defines monetary rules; no duplicate identifier. |
| 4.3 docs/matrix | Done: current test matrix, progress/quality/learning records and renamed current references updated. Historical checkpoint evidence retained. |
| 4.4 changelog | Done: concise hardening entry. |
| 5 restore/build/format/tests | Pending final execution; Gate3 had520 green and full format0. |
| 5 drift/migrations | Pending final CLI/fresh chain check; prior drift checks clean and Testcontainers apply19 migrations. |
| 5 Compose smoke | Pending fresh owned stack; expected health routes are /health/live and /health/ready, not nonexistent /health. |
| 5 Git/report | Pending final branch/evidence commit; no push/merge/PR/deployment performed. |
