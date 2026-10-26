# Changelog

## 2026-10-26 — hardening and focused clean code

- Separate confirmed publication/DB completion failures; configure safe Outbox batch leases and bounded jittered retries/reconnects.
- Persist assignment-aware GPS emission checkpoints with replay-safe atomic profile/projection updates.
- Add conservative batched technical cleanup while retaining request/Inbox/financial/audit identities and compensation obligations.
- Handle client aborts without500/error output; explicitly trust configured proxies for rate limits; warn safely for missing legacy couriers.
- Back off idle recovery and add only the capture index justified by actual100k-order/job query plans.
- Name vocabularies, shared DomainException, transactional effects, locking/candidate queries, focused ordering ports, cancellation policy and endpoint/clock composition. Preserve persisted/wire values and state-machine/payment contracts.
- Document realtime scale-out, unbounded replay retention, mock payments, registration timing, deep OFFSET cost and operational settings in ADR-027, without duplicating monetary ADR-014.
- Expand actual Domain/PostGIS/RabbitMQ regression coverage. Exact counts, commands, migrations and smoke evidence: docs/15_HARDENING_PASS.md and docs/07_PROGRESS_AND_HANDOFF.md.
