# Engineering standards

Use the existing .NET10 projects and feature vocabulary. Favor encapsulated state, narrow application ports, explicit transaction ownership and small cohesive methods. A long method is a review signal; splitting it is justified by an independent responsibility, not a line-count target.

- Domain mutations use intention-revealing methods with private state. No setters, EF state forcing or SQL may bypass business invariants.
- Derive actor identity and authority from authenticated server state. Catalog prices, payment readiness and assignment identities are authoritative server values.
- Pass CancellationToken through HTTP/use cases/database/broker work. Use fresh scopes/contexts for concurrent workers; never share DbContext between threads.
- Keep decimal money exact. Validate configuration and request bounds; protect multiplication/addition overflow. Argument-contract failures retain their established exact exception types; domain/fence conflicts use DomainException.
- Use database claims, unique constraints, optimistic concurrency and fences for cross-process races. An in-process lock alone does not establish business correctness.
- Couple business state, audit and Outbox in the same transaction. ACK only after durable effects/Inbox. Reconcile ambiguous external results instead of blindly retrying effects.
- Use parameterized SQL and bounded deterministic queries. Measure access paths with representative data before adding indexes. Keep migrations additive and review any live-table locking impact.
- Structured diagnostics must exclude credentials, private coordinates, bodies and unrestricted exception text. SafeLogSink is the exporter boundary.
- Add abstractions only for real policy/adapter seams. Avoid generic repositories, base-class hierarchies and transport dependencies in Application/Domain.
- Comments should be exceptional: legal notices, required tool/directive markers or indispensable operational warnings. Keep explanations in technical guides; do not alter literals or generated migration history to remove comments.
- Preserve meaningful tests and exact assertions. Tests cover outcomes, failure windows and real concurrent sessions; mocks do not replace PostgreSQL/RabbitMQ proof.

Review correctness, authorization, failure modes, locks, cancellation, schema impact and privacy after changes. Run the [supported validation suite](05_QUALITY_GATES.md), inspect the diff and retain failures in verification evidence. A green build alone does not prove runtime or operational readiness.
