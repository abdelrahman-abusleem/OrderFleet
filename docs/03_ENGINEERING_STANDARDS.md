# Engineering standards — OOP, SOLID, Clean Code, implementation discipline

These standards are **enforceable review criteria**, not a request to apply patterns mechanically.

## OOP — concrete rules

- **Encapsulation:** business state uses private setters/fields; mutations through intention-revealing methods (`Accept`, `Assign`, `Cancel`), not indiscriminate public setters, public mutable collections, or `SetStatus` escape hatches.
- **High cohesion:** type contains behavior/data required for one business concept; aggregate root protects transaction-local invariants. Avoid god objects (`OrderService` with every operation, `DispatchManager` with persistence, RabbitMQ, HTTP, geo and payment all mixed).
- **Composition before inheritance:** inherit only for true substitutability/shared contract; never large inheritance hierarchies for reuse alone. Prefer narrow interfaces when there are legitimate alternate implementations or test seams.
- **Polymorphism where useful:** payment provider adapter, clock, geo distance/ETA strategy only if there is real variation; not `IOrderItem` for plain value type.
- **Immutability:** prefer immutable value/snapshot data, record structs/classes where justified; avoid mutable shared singletons.
- **Errors:** Domain invalid transitions throw `DomainException` (ADR-013); argument-contract violations stay `ArgumentException`; map to API boundary, never swallow exceptions to force `200 OK`.

## SOLID as decisions and review questions

| Principle | Enforceable meaning | Smell / preferred correction |
|---|---|---|
| **S** Single Responsibility | Class/method changes for one coherent reason | `CreateOrder` doing parsing, catalog lookup, auth, pricing, DB, publishing and HTTP response: split into handler + trusted pricing/domain + persistence adapter + mapping |
| **O** Open/Closed | Extend variation behind deliberate seam | Hardcoded multi-provider switch scattered across controllers: isolate payment provider; do not create plugin frameworks for one strategy |
| **L** Liskov | Implementations preserve contract behavior | Test doubles that violate cancellation, exceptions or idempotency; avoid misleading derived types |
| **I** Interface Segregation | Client depends only on capabilities used | `IOrderRepositoryEverything` with 30 unrelated methods; prefer targeted application-facing interfaces |
| **D** Dependency Inversion | Policies do not depend on adapters | Domain/Application importing EF, `HttpContext`, RabbitMQ client; move runtime details to Infrastructure or API |

## Method-level clean code rule (explicit)

**If a method performs multiple independently meaningful activities, refactor it into cohesive methods/services before marking the feature complete.** Examples:

- Poor: endpoint validates auth, queries catalog, computes totals, saves EF state, publishes event, returns HTTP response.
- Better: endpoint binds/authorizes/returns HTTP; application use case orchestrates trusted catalog data, domain creation and save/outbox; adapters handle infrastructure.
- Poor: dispatch method queries spatial DB, scores candidates, obtains a claim, builds RabbitMQ messages, loops retries and logs format in one giant unit.
- Better: candidate reader + deterministic selection policy + transactional claim service + event recording + separate outbox publisher worker.

**Do not split a short, coherent method simply because it has many lines**, nor create one-line wrapper methods without semantic value. A split is justified when an independent business rule, side effect, error policy, or reason to change can be named/tested separately. Check and refactor after writing a happy path, again during self-review.

## Code conventions

- .NET 10 and compatible versions. Enable nullable reference types; adhere to existing project analyzers and `.editorconfig`. Use `Async` suffix for awaitable methods where idiomatic; pass CancellationToken from endpoints through DB/broker calls.
- Meaningful names and clear domain vocabulary. Avoid names `Process`, `HandleStuff`, `Utils`, `Common`, `Manager` without scope. Prefer explicit types to magic values. Constants/options for limits and timeouts; validate config at startup.
- Prefer structured logs (`logger.LogInformation("Order {OrderId} ...", id)`) over interpolated secrets or entire object dumps.
- Keep async nonblocking; no `.Result`, `.Wait()` or `async void` outside required event handler signatures. Do not share DbContext across threads; one unit of work per scope.
- Do not use `catch (Exception)` to silently ignore failures. Catch narrower expected exceptions and translate only at correct layer. Respect cancellation. Define retryable vs permanent errors.
- Avoid unnecessary nested conditions; use guard clauses but not scattered partial validation. No duplicate computations of persisted money snapshots.
- Verify EF query efficiency: avoid N+1, accidental client-side enumeration of unbounded data, tracking when unnecessary, and eager loading sensitive graphs.
- Repository pattern is **optional**: EF DbContext already supports relevant unit-of-work semantics. Add narrow interfaces only to protect architectural dependency and improve testing, not redundant generic CRUD wrappers.
- CQRS by read/write intent is welcome if complexity supports it; don't require MediatR, event bus wrappers, or framework dependencies without measured benefit.
- Do not store DTOs directly as tracked domain aggregates. Explicit mappers where trust/security boundaries require them.

## Refactoring and review protocol

On every change:

1. Ask: What is this code's reason to change? Is it in the correct layer/module?
2. Scan changed methods for multiple responsibilities; split cohesive units when justified.
3. Scan for duplicated validation and hidden side effects.
4. Check invariants under invalid, repeated, concurrent and partially failed operations.
5. Check auth, privacy, resource scopes and trusted fields.
6. Check transactions, cancellation, retry and idempotency.
7. Check tests cover observable behavior; keep test setup readable and independent.
8. Run formatter/analyzers/build/tests actually configured and record evidence.
9. If a new abstraction adds more indirection than benefit, simplify it.

## Code review red flags — block completion

- A business entity with public mutable `Status`, `Price`, `Items`, `AssignmentId`.
- Customer body `UnitPrice` is trusted or mapped into order without catalog lookup.
- Controller contains pricing or courier-claim transaction SQL.
- `DbContext` used concurrently or as a singleton.
- `SaveChanges` then `RabbitMQ.Publish` treated as guaranteed single atomic effect.
- Worker retry lacks bounds, delay, logging or idempotency.
- Exception broadly caught and success returned.
- Different HTTP users can access each other's orders by guessing IDs.
- Migration silently drops data/changes precision without impact analysis.
- Integration test uses EF InMemory or mocks database behavior.
- Unrelated refactor/mass formatting hides actual changes.
- README says “production-ready” but no failure recovery/concurrency/security validation exists.
