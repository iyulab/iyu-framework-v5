# iyu-framework-v5

Runtime library for the Iyu stack. Consumed by apps generated from M3L models
via [mdd-booster](https://github.com/iyulab/mdd-booster). Provides a single
`AddIyuMainServer` entry point that wires EF Core, OData, and GraphQL on top of
generator-produced entities, plus optional modules for identity, attachments,
chat, and scheduled reports.

Targets .NET 10. All eight projects share one version and ship as separate
NuGet packages.

## Layers

| Project | Role |
|---|---|
| `Iyu.Core` | `IyuEntity` base class, marker attributes (`[Lookup]`, `[Rollup]`, `[Computed]`, `[Reference]`), value objects (`PhoneNumber`, `EmailAddress`, `WebUrl`), identity contracts, and the attachment contracts (`IAttachmentStorage`, `FileAccessToken`, `FileAccessTokenService`) |
| `Iyu.Data` | `IyuDbContext` base + `IyuTimestampInterceptor` (automatic `CreatedAt`/`UpdatedAt`) + EF Core `ValueConverter`s for the value objects |
| `Iyu.Server.OData` | `IyuEdmModelBuilder.AddEntityPair<TRead,TWrite>(setName)` + generic `IyuODataController<TRead,TWrite>` (CRUD), `$search` binder |
| `Iyu.Server.GraphQL` | `IyuGraphQLSchemaBuilder.AddEntityPair<TRead,TWrite>(queryName, mutationPrefix)` (HotChocolate-based) |
| `Iyu.MainServer` | Composite — `AddIyuMainServer` / `UseIyuMainServer`; also `AddIyuIdentity` / `MapIyuIdentity` (cookie + JWT bearer, OAuth2 `client_credentials` service clients) |
| `Iyu.FileServer` | `AddIyuFileGateway` / `MapIyuFileGateway` — token-gated byte gateway with Azure Blob and local filesystem backends |
| `Iyu.Server.Chat` | `AddIyuChat` / `UseIyuChat` — bare-chat adapter |
| `Iyu.VaultAi` | `AddVaultAiReports` / `UseVaultAiReports` — scheduled report generation |

## Namespaces a consumer needs

Entity classes, the context, and the controllers all sit in different packages, so
a consuming project ends up repeating the same `using` lines in most of its files.
Declaring them once — in `GlobalUsings.cs`, or any file, with `global using` — is
usually less friction:

```csharp
global using Iyu.Core.Attributes;   // Lookup, Rollup, Computed, Reference, Binding
global using Iyu.Core.Entities;     // IyuEntity
global using Iyu.Data;              // IyuDbContext
global using Iyu.MainServer;        // IyuMainServerOptions
global using Iyu.Server.OData;      // IyuODataController<,>
```

Take the lines for the packages you actually reference; a project with no OData
surface has no reason for the last one. Anything missing shows up as a normal
`CS0246` naming the type, so the fix is mechanical.

## Minimum consumer

```csharp
using Iyu.MainServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIyuMainServer<AppDbContext>(
    configureDb: db => db.UseSqlServer(builder.Configuration.GetConnectionString("Default")),
    configure: options =>
    {
        options.ODataModel.AddEntityPair<OrderExt, Order>("Orders");
        options.GraphQL   .AddEntityPair<OrderExt, Order>("orders", "order");
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

## Integration testing (TestServer)

The generated OData controllers live in the server assembly, but MVC discovers
controllers by walking the **entry assembly**'s closure. In production the entry
assembly *is* the server, so discovery finds them. Under a test host the entry
assembly is `testhost`, whose closure does not include the server — so without
help every endpoint silently returns **404**.

`AddIyuMainServer` handles this automatically: it registers the `TContext`
assembly **and** the registration callback's declaring assembly as application
parts. The standard method-group form therefore just works over `TestServer`:

```csharp
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseTestServer();
builder.Services.AddIyuMainServer<AppDbContext>(
    configureDb: db => db.UseSqlite(conn),
    configure: ApiRegistration.RegisterGeneratedEntities); // method group → server assembly
```

If your controllers live in yet another assembly, or you pass the callback as a
**lambda wrapper** (whose declaring assembly is the caller, not the server), name
the controller-hosting assemblies explicitly — registration is deduplicated, so
this never double-registers:

```csharp
configure: options =>
{
    options.ControllerAssemblies.Add(typeof(SomeGeneratedController).Assembly);
    ApiRegistration.RegisterGeneratedEntities(options);
}
```

## File gateway (`Iyu.FileServer`)

A standalone host that moves bytes and nothing else. It holds no database: every
request carries an HMAC-SHA256-signed `FileAccessToken` minted by whoever owns
the attachment metadata, and the token names the storage key, so the gateway
cannot be redirected to another object.

```csharp
builder.Services.AddIyuFileGateway(
    gw =>
    {
        gw.SigningKey = builder.Configuration["Files:SigningKey"]!; // ≥32 bytes, shared with the minter
        gw.MaxBytes = 50L * 1024 * 1024;
        gw.AllowedContentTypes = ["application/pdf", "image/png"];  // empty = allow all
    },
    (FileSystemOptions fs) => fs.RootPath = "/var/attachments");     // or AzureBlobOptions

var app = builder.Build();
app.MapIyuFileGateway();   // PUT / GET / DELETE at gw.RoutePrefix (default "/files")
```

Behaviour worth knowing before deploying it:

- **`MaxBytes` is the authority on its own endpoint.** The upload handler aligns
  the server's per-request body limit to it, so the host's global default
  (30,000,000 bytes on Kestrel, HTTP.sys and IIS in-process) does not silently
  cap uploads below it. Ceilings the gateway cannot raise still apply and must be
  configured by the operator: IIS out-of-process (`maxAllowedContentLength`) and
  any reverse proxy.
- **Rejections are structured**, so a caller can tell them apart:
  `413` with `{"Error":"too_large"}`; `400` with
  `{"Error":"content_type_not_allowed"}` or `{"Error":"content_type_required"}`.
  An oversized body answers `413` whichever layer noticed it — the header check,
  the gateway's stream ceiling, or the host's guard.
- **`AllowedContentTypes` checks the type the token declares**, not the bytes
  that arrive. It enforces a policy the minter committed to; it does not sniff
  the payload. Matching compares `type/subtype` and ignores parameters, so
  `image/jpeg; charset=binary` satisfies an entry of `image/jpeg`; wildcards are
  not expanded. Once non-empty it fails closed — a token declaring no content
  type is rejected rather than waved through.
- **Downloads support `Range`**, so a large transfer can resume and a media
  client can seek. This is verified end-to-end against the filesystem backend;
  on Azure Blob it depends on the SDK's read stream reporting its length when
  opened, which is untested here (see Status). Uploads are a single request with
  **no resume**: a dropped connection restarts from zero, which is why the
  default limit is sized for document attachments rather than bulk media.
- **A missing object is 404, not 500.** Absence is a normal state — a key can be
  deleted while a still-valid token is in flight.
- Every rejection is logged under the category `Iyu.FileServer.FileGateway`
  (`FileGatewayExtensions.LogCategory`) so it can be filtered independently.
  Tokens are never logged. Successful transfers are not logged either — that is
  the host's request log.

## Upgrading

Per-release changes — including every breaking change and the packages each release
actually touched — are in
[CHANGELOG.md](https://github.com/iyulab/iyu-framework-v5/blob/main/CHANGELOG.md), a copy
of which ships inside every package.

All eight `Iyu.*` packages share one version, so a new number does not by itself mean the
code you depend on moved. Each release entry opens with **Packages affected**; if yours is
not listed, the upgrade is a version bump and nothing else. When skipping releases, read
every entry between your current version and the target — each one states its own breaking
changes only.

## Build & test

```bash
dotnet build IyuFramework.slnx
dotnet test  IyuFramework.slnx
```

All warnings are treated as errors across every project in the solution.

## Status

Version **0.10.0**. 216 unit and integration tests passing; build clean with no
warnings. The OData/GraphQL runtime, identity, attachments, chat, and reports
modules are all in place and consumed in production.

Known gaps, in rough priority order:

- The Azure Blob storage backend has no automated coverage — the test suite uses
  a fake and the local filesystem backend. Backend-specific failure modes are
  therefore not caught here.
- Resumable (chunked) upload is **not** supported. A GB-scale upload that drops
  restarts from zero. Adding it means a resumable protocol plus session state,
  which the gateway deliberately does not have today.
- The gateway has no request-timeout or minimum-data-rate policy of its own, so
  a slow but legitimate upload is subject to the host's slow-POST defences
  (Kestrel's `MinRequestBodyDataRate`, 240 bytes/second by default).
