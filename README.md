# iyu-framework-v5

Runtime library for the Iyu stack. Consumed by apps generated from M3L models
via [mdd-booster](https://github.com/iyulab/mdd-booster). Provides a single
`AddIyuMainServer` entry point that wires EF Core, OData, and GraphQL on top of
generator-produced entities.

## Layers

| Project | Role |
|---|---|
| `Iyu.Core` | `IyuEntity` base class, marker attributes (`[Lookup]`, `[Rollup]`, `[Computed]`, `[Reference]`), value objects (`PhoneNumber`, `EmailAddress`, `WebUrl`) |
| `Iyu.Data` | `IyuDbContext` base + `IyuTimestampInterceptor` (automatic `CreatedAt`/`UpdatedAt`) + EF Core `ValueConverter`s for the value objects |
| `Iyu.Server.OData` | `IyuEdmModelBuilder.AddEntityPair<TRead,TWrite>(setName)` + generic `IyuODataController<TRead,TWrite>` (CRUD) |
| `Iyu.Server.GraphQL` | `IyuGraphQLSchemaBuilder.AddEntityPair<TRead,TWrite>(queryName, mutationPrefix)` (HotChocolate-based) |
| `Iyu.MainServer` | Composite — `AddIyuMainServer` / `UseIyuMainServer` |

## Minimum consumer

```csharp
using Iyu.MainServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIyuMainServer<YesungDbContext>(
    configureDb: db => db.UseSqlServer(builder.Configuration.GetConnectionString("Yesung")),
    configure: options =>
    {
        options.ODataModel.AddEntityPair<OrderExt, Order>("Orders");
        options.GraphQL .AddEntityPair<OrderExt, Order>("orders", "order");
        // ...additional pairs, or a generated RegisterEntities(options) call
    });

var app = builder.Build();
app.UseIyuMainServer();
app.Run();
```

Resulting endpoints:
- `GET /$data/$metadata` — OData EDM document
- `GET /$data/Orders?$filter=Status eq 'confirmed'` — OData query
- `POST /graphql` with `{ orders { ... } }` — GraphQL query

## Read/Write pair model

Each logical entity has two CLR types:

- **Write type** (e.g. `Order`) — mapped to the base SQL table. Contains only
  stored fields. Used for POST/PATCH/DELETE inside the controller.
- **Read type** (e.g. `OrderExt`) — mapped to a SQL view. Contains stored
  fields **plus** lookups/rollups/computed fields. Exposed as the OData entity
  set and GraphQL query field.

The controller copies overlapping properties from the read body to a fresh
write entity using reflection; extras are dropped. `CreatedAt`/`UpdatedAt`/`Id`
are explicitly excluded because they are owned by the interceptor or the
caller's explicit assignment.

See `claudedocs/cycle-logs/cycle-01.md` … `cycle-14.md` for the step-by-step
rebuild history; `D:\data\mdd-booster\claudedocs\plans\2026-04-05-mdd-booster-rewrite-design.md`
for the full design spec.

## Status

**Plan 2 complete** (2026-04-05). Runtime scaffold exists, unit-tested (57
tests passing), and an HTTP E2E smoke through `Yesung.MainServer` verified the
composition end-to-end.

**Next**: Plan 3 (mdd-booster C# entity/DbContext generator) starting at
cycle 15 — see `claudedocs/cycle-logs/ROADMAP.md`.

## Build & test

```bash
dotnet build IyuFramework.slnx
dotnet test  IyuFramework.slnx
```

All warnings are treated as errors across every project in the solution.
