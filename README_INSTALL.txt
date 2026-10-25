OrderFleet AI Engineering Pack — Installation
==========================================

1. Extract this ZIP.
2. Copy `AGENTS.md` and the entire `docs` folder into the ROOT of your REAL OrderFleet repository, next to `OrderFleet.slnx` and `compose.yaml`.
3. DO NOT overwrite existing project README, code, tests or .git files. If your repo already has AGENTS.md or a docs folder with matching names, merge carefully rather than overwriting user edits.
4. Give the coding AI the root folder and tell it to read `AGENTS.md`; optionally paste `docs/12_AGENT_LAUNCH_PROMPT.md`.
5. The AI must verify what currently exists; these documents cannot prove code or tests were run. Initial state is historical handoff.
6. These are Markdown/TXT files; no special documentation tool needed.

Includes:
AGENTS.md — hard rules
00_START_HERE.md — reading order & audit
01_PRODUCT_AND_SCOPE.md — actual product scope
02_ARCHITECTURE.md — architectural boundaries
03_ENGINEERING_STANDARDS.md — OOP/SOLID/Clean Code contract
04_EXECUTION_ROADMAP.md — 13 phases with tasks & acceptance criteria
05_QUALITY_GATES.md — testing & self-review contract
06_SECURITY_AND_RELIABILITY.md — auth, concurrency, Outbox, retries
07_PROGRESS_AND_HANDOFF.md — truth-based progress ledger
08_DECISION_LOG.md — approved/proposed ADRs
09_LEARNING_GUIDE.md — future learning & interviews
10_REFERENCE_LIBRARY.md — official docs & books
11_API_DATA_CONTRACTS.md — proposed endpoint & schema contracts
12_AGENT_LAUNCH_PROMPT.md — copy/paste kickoff instruction

IMPORTANT: Historical local developer PostgreSQL credentials deliberately NOT embedded in this pack.

Revision 2026-10-25: ADR-008 to ADR-013 decided and binding; Phase 1.5 (approved Order additions) added; continuous autonomous phase progression after verified quality gates, local-commit Git workflow, CI/README moved to Phase 2A; SignalR/ETA and OpenTelemetry remain mandatory deliverables, with no feature-skipping permission.
