# Technical references

Primary sources for the implemented stack; validate APIs against pinned versions.

## Architecture / DDD / OOP / SOLID / Clean Code

- Microsoft .NET Architecture eBook (Clean Architecture): https://learn.microsoft.com/en-us/dotnet/architecture/modern-web-apps-azure/common-web-application-architectures
- Microsoft architecture — DDD microservice domain model (read **for aggregate patterns**, not as an instruction to create microservices): https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/
- Microsoft Framework Design Guidelines (API design consistency): https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/
- Microsoft C# coding conventions: https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions
- Martin Fowler — patterns of enterprise application architecture: https://martinfowler.com/eaaCatalog/
- Eric Evans, *Domain-Driven Design: Tackling Complexity in the Heart of Software* (book, foundational aggregates/bounded contexts).
- Robert C. Martin, *Clean Architecture: A Craftsman's Guide to Software Structure and Design* (book; apply ideas pragmatically, avoid ceremony).
- Robert C. Martin, *Clean Code* (book; readability principles, **not** automatic universal method line limits).
- Steve McConnell, *Code Complete* (book; general code review and design guidance).
- Michael Feathers, *Working Effectively with Legacy Code* (book; preserving tests when changing existing software).
- Vaughn Vernon, *Implementing Domain-Driven Design* (book; aggregate boundaries and tradeoffs).

## .NET 10 / ASP.NET Core security and HTTP

- ASP.NET Core fundamentals/DI: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection?view=aspnetcore-10.0
- Minimal APIs overview: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/overview?view=aspnetcore-10.0
- JWT bearer authentication: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0
- ASP.NET Core authorization: https://learn.microsoft.com/en-us/aspnet/core/security/authorization/introduction?view=aspnetcore-10.0
- SignalR authentication/authorization: https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0
- RFC 9110 HTTP semantics / idempotent methods: https://www.rfc-editor.org/rfc/rfc9110
- OWASP API Security Top 10: https://owasp.org/API-Security/editions/2023/en/0x11-t10/
- OWASP ASVS (security controls checklist): https://owasp.org/www-project-application-security-verification-standard/

## EF Core, Npgsql, PostgreSQL and testing

- EF Core owned entity types: https://learn.microsoft.com/en-us/ef/core/modeling/owned-entities
- EF Core optimistic concurrency: https://learn.microsoft.com/en-us/ef/core/saving/concurrency
- EF Core transactions: https://learn.microsoft.com/en-us/ef/core/saving/transactions
- EF Core migrations: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/
- Testing against the production database system: https://learn.microsoft.com/en-us/ef/core/testing/testing-with-the-database
- Npgsql EF Core provider: https://www.npgsql.org/efcore/
- Npgsql concurrency (confirm current docs/implementation before using `xmin`): https://www.npgsql.org/efcore/modeling/concurrency.html
- PostgreSQL 17 docs, indexes/constraints/locking: https://www.postgresql.org/docs/17/
- Testcontainers .NET docs: https://dotnet.testcontainers.org/
- Docker Compose dependency and healthcheck ordering: https://docs.docker.com/compose/how-tos/startup-order/

## PostGIS and geospatial

- PostGIS docs: https://postgis.net/documentation/
- `ST_DWithin` (geometry units vs geography meters and index behavior): https://postgis.net/docs/ST_DWithin.html
- Spatial indexing: https://postgis.net/workshops/postgis-intro/indexing.html
- NetTopologySuite/Npgsql spatial provider: https://www.npgsql.org/efcore/mapping/nts.html

## Messaging, outbox and distributed systems

- RabbitMQ consumer acknowledgement and publisher confirms: https://www.rabbitmq.com/docs/confirms
- RabbitMQ reliability guide: https://www.rabbitmq.com/docs/reliability
- RabbitMQ dead lettering: https://www.rabbitmq.com/docs/dlx
- RabbitMQ quorum queues (choose only if topology warrants): https://www.rabbitmq.com/docs/quorum-queues
- Transactional outbox pattern (pattern reference): https://microservices.io/patterns/data/transactional-outbox.html
- Idempotent consumer pattern: https://microservices.io/post/microservices/patterns/2020/10/16/idempotent-consumer.html
- Chris Richardson, *Microservices Patterns* (book: outbox, saga, idempotency principles; **not** a mandate to use microservices).
- Martin Kleppmann, *Designing Data-Intensive Applications* (book: reliability, delivery semantics, concurrency and consistency).

## Observability and local delivery

- OpenTelemetry .NET guide: https://opentelemetry.io/docs/languages/dotnet/
- .NET observability: https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-with-otel
- Docker Compose reference: https://docs.docker.com/reference/compose-file/
- GitHub Actions .NET workflow guidance: https://docs.github.com/en/actions/use-cases-and-examples/building-and-testing/building-and-testing-net

## Source quality checklist

When making a contested technical choice: include link, relevant version/date, exact implication to OrderFleet, alternatives, impact on testability and how code/tests confirm correctness. Books are conceptual references only; online official docs take precedence for current API exactness.

Review reference: PostgreSQL17 monitoring statistics snapshots are transaction-scoped; use a separate autocommit observer or explicitly refresh the snapshot when writing live contention witnesses: https://www.postgresql.org/docs/17/monitoring-stats.html#MONITORING-STATS-VIEWS . DispatchContentionTests demonstrates the applied boundary.
