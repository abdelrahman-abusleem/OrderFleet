# Hardening pass — observed evidence (2026-10-26)

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
