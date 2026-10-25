# OrderFleet — agent entry point and reading order

> Copy the `AGENTS.md` file and the entire `docs/` folder into the **actual repository root**, next to `OrderFleet.slnx`. Do not copy this package's `README.md` over the project's existing README.

## Purpose

These files are the working **engineering agreement** for an autonomous implementation agent. They define the expected implementation, how to evaluate success, and what future human reviewers must be able to understand. Documents in this pack are **specifications, not evidence that features exist**.

## Required reading order

1. `../AGENTS.md` — binding execution rules.
2. `01_PRODUCT_AND_SCOPE.md` — business actors, expected behavior, scope and lifecycle.
3. `02_ARCHITECTURE.md` — boundaries, dependency rules, data ownership, consistency.
4. `03_ENGINEERING_STANDARDS.md` — OOP/SOLID/Clean Code and coding conventions.
5. `04_EXECUTION_ROADMAP.md` — exact sequence, slices, tests and acceptance criteria.
6. `05_QUALITY_GATES.md` — test strategy, evidence, review process, delivery gates.
7. `06_SECURITY_AND_RELIABILITY.md` — authorization, idempotency, messaging and recovery.
8. `08_DECISION_LOG.md` — architectural decision records, including open decisions.
9. `10_REFERENCE_LIBRARY.md` — primary technical reference list.
10. `07_PROGRESS_AND_HANDOFF.md` — observed progress and verification ledger.
11. `09_LEARNING_GUIDE.md` — teaching-oriented documentation contract.
12. `11_API_DATA_CONTRACTS.md` — proposed concrete contracts to refine before implementation.
13. `12_AGENT_LAUNCH_PROMPT.md` — ready-to-copy launch instruction.

## First run — repository audit

Record output in `07_PROGRESS_AND_HANDOFF.md`:

- `git status --short` (if Git is initialized); list untracked and changed files. Do not alter them without reason.
- `dotnet --info`; inspect `OrderFleet.slnx`, all `.csproj`, package versions, project dependency references.
- Locate all Domain types, full `Order` state transitions, EF mappings, migration files and test frameworks.
- `dotnet build OrderFleet.slnx -c Release`; `dotnet test OrderFleet.slnx -c Release` (when infrastructure requirements can be met).
- Inspect `compose.yaml` and `docker compose ps` if Docker is available; avoid destroying shared volumes.
- Check whether a **real Postgres persistence integration test** exists and actually ran (do not confuse with placeholder test).
- Verify schema precision, generated migrations and their target environment before altering data.
- Write a truth table of each requirement: **observed complete / observed partial / not implemented / unverified / blocked**.
- Record only factual test results and commands executed; **do not repeat old “223 passed” as a current result without rerunning**.

## Document precedence and change control

- User's explicit new decisions outrank these repository planning documents, subject to higher-priority safety instructions.
- `AGENTS.md` is the day-to-day agent contract; other docs expand it.
- ADR-008 to ADR-013 in `08_DECISION_LOG.md` are ACCEPTED (owner, 2026-10-25) and binding; do not reopen them.
- `01_PRODUCT_AND_SCOPE.md` defines business scope; `08_DECISION_LOG.md` records approved deviations and decisions.
- A proposal in `11_API_DATA_CONTRACTS.md` is **not an unchangeable established domain rule**. Inspect existing contracts first and propose changes if they conflict.
- Do not silently redefine established `Order` statuses to make payment/dispatch easier.
- For each major architecture/business choice, record an ADR with problem, alternatives, choice, consequences and verification.

## Definition of “complete”

A feature is COMPLETE only when its business acceptance behavior exists, real applicable tests pass, API/auth/DB edge cases are covered, changed code has been critically reviewed, and docs reflect actual implementation. If evidence is missing, mark it UNVERIFIED.

## Immediate starting point

**Phase 0 audit, then Phase 1.5** (owner-approved Order additions, ADR-013), **then Phase 2A:** inspect and verify existing EF Core persistence/migrations and build a real Testcontainers/PostgreSQL save-and-reload test for the `Order` aggregate (plus a minimal CI workflow and README skeleton). Then Phase 2B database concurrency and all remaining required phases. **After each verified phase gate, record the results and continue automatically without waiting for the owner's "go"; do not remove or skip roadmap features.**

14. `13_DELIVERY_GUIDE.md` — implemented runtime walkthrough, diagrams, operations and known limitations (read alongside the specifications and current verification ledger).
