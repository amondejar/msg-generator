# {{Service}}: instructions for coding agents

This service is generated and kept in sync by [Stratum](https://github.com/) {{StratumVersion}} from `stratum.json`.
Read this before changing code: some files belong to Stratum and are rewritten on every change.

## What belongs to Stratum and what to you

- `*.g.cs` files and `stratum.json` are generated. **Never edit them**: Stratum overwrites them (and warns when one was
  edited by hand). To change entities, properties, enums, list item types, operations, features, messaging or
  subscriptions, use the `stratum` CLI (or Stratum Studio / the `stratum mcp` server), not the generated code.
- Files without `.g` are yours (created once, never touched again): partial classes such as `Order.cs` and
  `OrderEndpoints.cs`, event handlers and `Program.cs`. Put custom code there, extending the generated partial classes
  and partial methods.
<!-- @stratum:if cleanDdd -->
- Also yours: `Order.Cancel.cs` and the like (the rule of each operation of the aggregate) and the repositories.
<!-- @stratum:end -->
- `src/{{Service}}.Api/{{Service}}.Api.http` is regenerated while nobody edits it.

## Specification and user stories (`docs/`, in Spanish)

If the service has a `docs/` folder (`stratum spec` creates it), read it before implementing behavior:

- `docs/especificacion.g.md`: what the service does, generated from `stratum.json` (entities and their rules, API and
  responses, operations, events it publishes and listens to). Stratum keeps it up to date; never edit it.
- `docs/contexto.md`: purpose, roles and glossary. Yours.
- `docs/historias/*.md`: one user story per entity, operation and subscription, with acceptance criteria in Gherkin.
  Yours: complete what is marked «por completar» (ask the user, don't invent business rules), implement each rule where
  the story says, and add one test per scenario.

## Useful commands

```bash
stratum entity list                                   # entities, properties and operations
stratum entity update Order --props "Number:string, Items:OrderItem[]"   # full list: missing ones are removed
stratum enum add OrderStatus --values "Draft, Paid"
stratum type add OrderItem --props "ProductId:guid, Quantity:int"
<!-- @stratum:if cleanDdd -->
stratum entity operation add Order Cancel --params "Reason:string"
<!-- @stratum:end -->
stratum migration review                              # after "dotnet ef migrations add": fixes renames, warns about data loss
```

Every command accepts `--dry-run` to see the plan first. After changing the model, create an EF Core migration.

## Architecture

<!-- @stratum:if cleanDdd -->
Clean Architecture + DDD: `Api → Application → Domain`, `Infrastructure → Application`.

- Business rules live in the aggregate (`src/{{Service}}.Domain`): throw `DomainException` for a broken rule (the API
  answers 422). Implement `EnsureInvariants` in the aggregate's own partial file for rules checked on every change.
- Use cases are commands/queries with handlers in `src/{{Service}}.Application`; endpoints only translate HTTP.
- Persistence goes through repositories and `IUnitOfWork`; domain events are dispatched after saving.
<!-- @stratum:else -->
Vertical slices: each feature (`src/{{Service}}.Api/Features/<Entities>`) has its entity, EF configuration,
contracts and endpoints together. Endpoints use the `DbContext` directly: there is no service layer on purpose.
Keep new endpoints in the feature's own `<Entity>Endpoints.cs`.
<!-- @stratum:end -->

## Conventions (keep them in your code)

- Minimal APIs with `TypedResults` and explicit `Results<…>` return types; `CancellationToken` on every endpoint,
  forwarded to every async call. POST answers 201 with `Location`, PUT 204, DELETE 204, unknown id 404.
- Request/response DTOs are `sealed record`s, separate from the stored types; validation with DataAnnotations
  (`AddValidation()` → 400 ProblemDetails). Errors are RFC 7807 ProblemDetails.
- `DateTimeOffset` for dates with time; enums travel and are stored as text.
- EF Core: `AsNoTracking()` for reads, no lazy loading, migrations (never seed in `Program.cs`).
- Integration events go through the outbox (`IEventBus.PublishAsync` before `SaveChangesAsync`): never call the broker directly.
- Tests: `tests/{{Service}}.Api.Tests` (xUnit, in-memory SQLite). Add your own tests in new files or partial classes;
  one behavior per test, assert on data, not only on status codes.
- Package versions live in `Directory.Packages.props` (Central Package Management); build settings in `Directory.Build.props`
  (warnings are errors).

## Recommended skills

The .NET team's official skills ([dotnet/skills](https://github.com/dotnet/skills)) match these conventions:
`dotnet-aspnetcore` (Web API), `dotnet-data` (EF Core queries), `dotnet-test` (test quality) and `dotnet-msbuild`.
In Claude Code: `/plugin marketplace add dotnet/skills`, then `/plugin install <plugin>@dotnet-agent-skills`.
When a skill and this file disagree, this file wins (for example: no service layer in vertical slices, PUT answers 204).
