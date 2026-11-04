# ADR-027: durable checkpoints and focused transactional structure

Status: accepted and implemented. Date:2026-10-26; current verification: [release report](../16_RELEASE_VERIFICATION.md).

## Context

GPS event volume, technical retention and mixed persistence responsibilities needed clearer contracts without changing order/payment state machines or transaction boundaries.

## Decisions

- Persist every accepted courier observation in aggregate and spatial projection together. Emit only for an active assignment when movement is at least25m OR elapsed time at least10s since the last emitted observation. Persist the checkpoint/fence; identical normalized replay writes nothing. Thresholds are validated configuration.
- Retain HTTP request and Inbox deduplication identities indefinitely. Bounded cleanup covers only eligible published Outbox older than7 days, resolved recovery failures older than30 and eligible finished-order dispatch attempts older than30. Preserve pending work, financial identity/history and immutable audits. Recheck eligibility after candidate selection to protect concurrent workers.
- Keep standalone order persistence tolerant of a missing courier profile with a safe identifier-only warning.
- Keep IClock/SystemClock as a TimeProvider wrapper and preserve exact ArgumentException order-limit behavior.
- Extract named transition effects, locking and candidate-query helpers only where responsibilities are distinct. Share the existing DbContext/transaction and preserve lock/query/save ordering. Narrow ordering interfaces resolve to one scoped EF store.
- Keep CourierPosition as an intentional spatial projection; it never grants business authority. No broad clock rewrite, speculative acceptance rewrite or artificial recovery-service split.

## Consequences and verification

Some deduplication storage is unbounded until a separately approved replay horizon exists. Realtime still supports one API instance. Tests cover GPS threshold equality, assignment changes, replay, profile/projection consistency, stale-writer rollback, bounded cleanup and concurrent eligibility, query ordering and scope lifetimes. [Current plans](../evidence/release-recovery-after.txt) and the release report supersede historical rollout ledgers.
