# OrderFleet — binding instructions for autonomous coding agents

**Priority:** These are repo-specific instructions. Follow them when compatible with higher-priority system/developer instructions and explicit owner decisions. Never silently bypass them. If instructions disagree, stop the conflicting operation, record the contradiction, and ask the owner for a decision.

## Mission and scope

Build a **.NET 10 production-oriented Restaurant Ordering & Courier Dispatch Backend** that demonstrates domain modeling, reliable transactions, concurrency, distributed messaging, recovery, security, geospatial processing and real tests. This is a **modular monolith with Clean Architecture**, not a collection of microservices or a CRUD showcase. See `docs/00_START_HERE.md` for navigation. Source of truth for business scope: `docs/01_PRODUCT_AND_SCOPE.md`; architecture: `docs/02_ARCHITECTURE.md`; execution: `docs/04_EXECUTION_ROADMAP.md`.

## MUST follow — no exceptions by convenience

1. **Inspect before modifying.** Read the actual repository, Git working tree, projects, packages, tests, migrations, existing code and all documents referenced by `docs/00_START_HERE.md`. Do not assume the handoff notes are proof of current repo state.
2. **Preserve user work.** Never reset/rebase/clean the working tree, discard unrelated edits, remove tests, recreate existing implementations, or silently rewrite the established Order domain; the only approved Domain changes are the additions listed in **ADR-013** (Phase 1.5). No push, merge, deploy, publish or destructive DB changes without explicit owner authorization. Local commits follow the **Git workflow** section below.
3. **Stay within architecture.** Domain has no EF/HTTP/RabbitMQ dependencies. Application owns use cases/ports and orchestrates. Infrastructure implements storage/messaging/adapters/workers. Api is the composition root and HTTP/SignalR boundary. No project explosion or hidden cross-module mutable state.
4. **Maintain Domain invariants.** No setter bypass, reflection hack, raw SQL bypass or forced EF state to circumvent business rules. Client-provided price, user identity, restaurant authority or courier identity is never trusted.
5. **Apply OOP and SOLID pragmatically.** Encapsulate state; one coherent responsibility per type/method; express intent via names; compose small collaborators when behavior has truly independent reasons to change. A long method is a review signal, not an automatic line-count violation. Do not invent pointless interfaces, layers, base classes, generic repositories, or giant orchestrators.
6. **Review after implementation.** For every vertical slice: build, targeted tests, real integration tests as applicable, inspect the changed lines, check correctness/concurrency/security/failure modes, fix discovered issues, rerun. Mark phase DONE only if its quality gate passes with commands and recorded results.
7. **Use the actual technologies.** PostgreSQL via EF Core/Npgsql; PostGIS for spatial queries; RabbitMQ for messaging; Testcontainers for real infrastructure tests; SignalR for authorized realtime; Docker Compose for local reproducibility. Never substitute EF InMemory for database integration verification or mock RabbitMQ as final proof of delivery semantics.
8. **Reliability is a contract.** Domain checks alone do not resolve multi-process races. Use DB-side atomic claims and optimistic concurrency. Outbox records and state changes share a DB transaction. Expect at-least-once message delivery; consumers and HTTP retries must be idempotent. Fence stale courier assignments.
9. **Security is cross-cutting.** Deny-by-default access, resource ownership, validation of token issuer/audience/lifetime/signature, controlled secrets, no PII/token leakage in logs. Never claim an insecure demo implementation is production-ready.
10. **No false claims.** Report only commands/tests actually executed. Distinguish compile success, tests passing, end-to-end verified, and operational readiness. If Docker/network unavailable, mark relevant tests BLOCKED/UNVERIFIED, not PASSED.
11. **Changes must be traceable.** After every slice update `docs/07_PROGRESS_AND_HANDOFF.md` and, when a design choice was made, `docs/08_DECISION_LOG.md`. At the end of every phase also update `docs/05_QUALITY_GATES.md` and `docs/09_LEARNING_GUIDE.md`. New modules, APIs, DB migrations and key design choices must be documented.
12. **Proceed autonomously across phase gates without reducing scope.** Within each phase, implement coherent slices (test/review, then continue) without requesting approval for routine implementation. **When a phase gate passes: commit on the phase branch, write the phase report, then immediately proceed to the next phase without waiting for a "go".** All roadmap features, including SignalR, ETA and OpenTelemetry, are mandatory for final completion: never skip, remove, silently defer indefinitely, or downgrade them to optional. Stop only when an explicit owner decision is required for incompatible business changes, destructive operations, adding cloud-paid services, changing tech stack, or major architecture shifts, or when a genuine blocker prevents safe progress; continue independent approved work where possible.
13. **Decisions already taken are binding.** ADR-008 to ADR-013 in `docs/08_DECISION_LOG.md` are ACCEPTED (owner, 2026-10-25). Do not re-open them; if one proves defective, stop and report with evidence.

## Mandatory execution loop

**Inspect → Specify acceptance tests → Implement → Build → Test (including real infra as relevant) → Adversarial self-review of diff → Correct defects → Re-test → Update docs → Report evidence → Next slice.**

At the end of a phase the loop continues with: **Commit on phase branch → Phase report → Proceed automatically to the next phase**, provided the quality gate passes. Never skip required features or treat incomplete/unverified work as complete.

Never mark a stage done merely because it builds. Review the diff for mixed responsibilities, duplicated business logic, unnecessary abstractions, leaked credentials, authorization mistakes, missing cancellation tokens, unsafe EF use, migration risk and races. See `docs/03_ENGINEERING_STANDARDS.md` and `docs/05_QUALITY_GATES.md`.

## Git workflow (owner pre-authorization, 2026-10-25)

- Work on one local branch per phase: `phase/<id>-<short-name>` (for example `phase/1.5-domain-additions`), created from the current branch at phase start.
- If the working tree already has uncommitted owner changes, never commit, stash or discard them; stage only the files you changed and report the situation.
- Commit small, coherent slices **after** build and targeted tests pass. Message: imperative summary line, then a body saying what changed and which tests were run.
- Never push, merge, rebase, amend, force, tag, or delete branches. The owner reviews each phase branch and merges it.
- Never commit secrets, User Secrets values, real `.env` files or local developer passwords.

## Repository snapshot — unverified handoff, inspect first

`OrderFleet.slnx`; `src/OrderFleet.{Domain,Application,Infrastructure,Api}`; `tests/OrderFleet.{Domain.Tests,IntegrationTests}`; `compose.yaml`. Domain previously passed **223 solution tests** (222 Domain + 1 Integration placeholder). Existing `Order` root and `OrderItem` owned collection, `OrderPricingLimits`, state machine, `OrderFleetDbContext`, `OrderConfiguration`, `InitialCreate` migration reportedly generated/applied; do not assume integration test file was created or executed. PostgreSQL 17 developer container historically used localhost port 5433. Do not store developer password in code or docs.

## First action

Read `docs/00_START_HERE.md` and execute the audit described there. Do not immediately regenerate the repository. Then implement **Phase 1.5** (owner-approved Order additions, ADR-013, tests first) and **Phase 2A** (persistence verification), based on observed facts, and continue automatically through every required phase, reporting at each phase gate without pausing for routine approval.
