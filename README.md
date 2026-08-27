# iyu-framework-v5

Runtime library for the Iyu stack. Consumed by apps generated from M3L models
via [mdd-booster](https://github.com/iyulab/mdd-booster). Provides a single
`AddIyuMainServer` entry point that wires EF Core, OData, and GraphQL on top of
generator-produced entities, plus optional modules for identity, attachments,
chat, scheduled reports, office-document template rendering, and document-to-PDF
conversion.

Targets .NET 10. All ten projects share one version and ship as separate
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
| `Iyu.Report` | `AddIyuReport` — office-document template rendering via [DocuChef](https://github.com/iyulab/DocuChef); unrelated to `Iyu.VaultAi`'s scheduled reports, see below |
| `Iyu.DocConvert` | `AddIyuDocConvert` — `IDocumentConverter.ConvertToPdfAsync`, backed by a self-hosted [Gotenberg](https://gotenberg.dev) instance |

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

### Keeping a stored value off the API surface

Every public property of a read type is reachable through `$data` and GraphQL. For a
value that is stored but must never leave the server — a password hash, a client secret —
subtract it from the model:

```csharp
configure: options =>
{
    RegisterEntities(options);   // registration you may not own — see below

    options.ODataModel.Exclude<AccountExt>(x => x.PasswordHash);
    options.GraphQL.Exclude<AccountExt>(x => x.PasswordHash);
}
```

**Name the read type.** It is the type both surfaces expose, and — because request bodies
bind to it — excluding it closes reads and writes together: `$select`, `$filter` and
`$orderby` naming the property are rejected, the GraphQL schema has no such field, and a
`POST`/`PATCH` carrying it fails before anything is stored. The write type is not part of
the exposed model, so naming it excludes nothing; both builders refuse it at startup rather
than let the call quietly do nothing.

The property is *removed*, not blanked. A blank value would be indistinguishable from "this
row has no value", and would still let a caller recover the real one a character at a time
with `$filter=startswith(...)`.

### Making one property read-only

A domain field can genuinely need to change — just never through the generic write path.
A state machine's current-state field, say, where a dedicated action endpoint is what
should apply a transition (and log it), while a plain `PATCH` naming the field directly
would let a client skip that endpoint entirely. `Exclude<T>()` above is the wrong tool: it
closes reads too, and this field should stay fully queryable.

```csharp
options.ODataModel.ExcludeFromWrite<OrderExt>(x => x.Status);
```

Unlike `Exclude<T>()`, the property stays in the model — `$select`/`$filter`/`$orderby`
are unaffected — and instead picks up the standard `Org.OData.Core.V1.Computed` term on
`$metadata` ("server-supplied, do not send on insert/update"). A `POST`/`PATCH` naming it
anyway is not rejected: the value is silently dropped from what the generic controller
copies onto the write entity, the same way `Id`/`CreatedAt`/`UpdatedAt` already are. A
write straight to the write-side `DbSet` — a dedicated endpoint reached through your own
controller action, for instance — is unaffected; it never goes through the generic
controller's copy step at all.

Same read-type rule as `Exclude<T>()`, for the same reason: request bodies bind to
`TRead`, so name that side. Also order-independent and callable after the fact from a
generated registration, exactly like `Exclude<T>()`.

### Restricting write verbs

A set backed entirely by a read-only view, or an audit-trail entity only the system itself
should write, refuses some or all of POST/PATCH/DELETE:

```csharp
options.ODataModel.AddEntityPair<OrderSummaryExt, OrderSummary>(
    "OrderSummaries", ODataVerb.Post, ODataVerb.Patch, ODataVerb.Delete);
```

The restriction is advertised on `$metadata` via the standard OData Capabilities vocabulary
(`Org.OData.Capabilities.V1.InsertRestrictions`/`UpdateRestrictions`/`DeleteRestrictions`) and
enforced by the generic controller with `405 Method Not Allowed` — a client that reads the
metadata and one that skips it are rejected identically.

**When the registration is not yours to edit**, e.g. a single generated file that calls
`AddEntityPair(setName)` once per set with no per-call-site control, restrict the set
afterward instead of at registration:

```csharp
RegisterGeneratedEntities(options);   // registration you may not own

options.ODataModel.Restrict("DemoDataProvenances", ODataVerb.Post, ODataVerb.Patch, ODataVerb.Delete);
```

`Restrict` requires the set to already be registered — it throws if it is not — and reaches
`$metadata` and controller enforcement identically to declaring `readOnlyVerbs` at
`AddEntityPair` time, since both read the registry's live state rather than a value captured
at registration.

### Field descriptions

A read type property carrying `[Display(Description = "...")]` — the standard
`System.ComponentModel.DataAnnotations` attribute, and what a generator's `@help`-style
metadata typically becomes — surfaces automatically on both API surfaces, no extra call
needed: OData exposes it as the standard `Org.OData.Core.V1.Description` term on
`$metadata`, and GraphQL exposes it as the field's `description` in schema introspection.
A property without the attribute keeps no description on either surface.

Both calls work after the pair is registered, which is the point: a registration you cannot
edit — one produced by a tool, or shared across several hosts — can still be subtracted from.

## Integration testing (TestServer)

The generated OData controllers live in the server assembly, but MVC discovers
controllers by walking the **entry assembly**'s closure. In production the entry
assembly *is* the server, so discovery finds them. Under a test host the entry
assembly is `testhost`, whose closure does not include the server — so without
help every endpoint silently returns **404**.

`AddIyuMainServer` handles this automatically: it registers the `TContext`
assembly, the registration callback's declaring assembly, and the assembly
serving `$metadata` as application parts. The standard method-group form
therefore just works over `TestServer` — entity sets, `$metadata` and the
service document alike:

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

## Document templates (`Iyu.Report`)

A thin DI wrapper around [DocuChef](https://github.com/iyulab/DocuChef)'s
`Chef`/`IRecipe`/`IDish` API — nothing more. It holds no template storage and maps no
endpoints; the consumer loads a template, binds data, and saves the result:

```csharp
builder.Services.AddIyuReport();   // registers DocuChef's Chef as a scoped service

// wherever the report is generated:
using var recipe = chef.LoadExcelTemplate(templateStream);
recipe.AddVariable("Title", "Shipment Slip");
recipe.AddVariable("Items", items);   // any bindable value — collections included

using var dish = recipe.CookDish();
dish.SaveAs(outputStream);
```

**Not the same thing as `Iyu.VaultAi`'s "scheduled reports"** — `Iyu.VaultAi` schedules and
generates its own report content; `Iyu.Report` fills an Office document template (Excel,
for now) with data the caller supplies, on demand. Independent modules, independent
dependency footprint (`Iyu.Report` has no `Iyu.Core` reference and no
`Microsoft.AspNetCore.App` framework reference), no shared code.

**Non-goals: no template registry, no generation-history tracking, no endpoint mapping.**
Deciding which templates exist and where they live, recording who generated what output and
when, and exposing report generation over HTTP are all consumer-application concerns —
`Iyu.Report` only turns a template stream plus data into an output stream, and stops there by
design, not by omission.

For everything past `AddIyuReport()` — template syntax, binding rules, supported formats —
see [DocuChef's own documentation](https://github.com/iyulab/DocuChef); this package
does not wrap or re-document that surface.

## Document conversion (`Iyu.DocConvert`)

`IDocumentConverter.ConvertToPdfAsync(source, sourceContentType)` — converts an Office or
OpenDocument file to PDF. One built-in implementation, backed by
[Gotenberg](https://gotenberg.dev) (a self-hosted, MIT-licensed HTTP wrapper around
LibreOffice — not a commercial dependency):

```csharp
builder.Services.AddIyuDocConvert(o => o.BaseUrl = "http://localhost:3000");

// wherever a PDF is needed:
using var pdf = await converter.ConvertToPdfAsync(
    docxStream, "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
```

Requires a running Gotenberg instance — `docker run --rm -p 3000:3000 gotenberg/gotenberg:8`
is the whole setup; this fits the on-prem-first deployment `AddIyuMainServer` already assumes
(the same pattern as pointing `UseSqlServer` at a connection string). `sourceContentType` must
be one of the MIME types `GotenbergDocumentConverter` maps to a LibreOffice-readable extension
(`.docx`/`.xlsx`/`.pptx`, legacy `.doc`/`.xls`/`.ppt`, OpenDocument `.odt`/`.ods`/`.odp`,
`.csv`/`.txt`/`.rtf`) — an unrecognized type throws `NotSupportedException` rather than
guessing. `source` is read but not disposed; the caller keeps ownership, same convention as
`IAttachmentStorage.SaveAsync`.

**Not part of `Iyu.Report`.** `Iyu.Report` fills a template with data; `Iyu.DocConvert` takes
an already-produced Office file and renders it to PDF — the two compose (bind a template, then
convert the result) but neither depends on the other. `Iyu.DocConvert` has no `Iyu.Core`
reference and no `Microsoft.AspNetCore.App` framework reference, same independence as
`Iyu.Report`.

## Upgrading

Per-release changes — including every breaking change and the packages each release
actually touched — are in
[CHANGELOG.md](https://github.com/iyulab/iyu-framework-v5/blob/main/CHANGELOG.md), a copy
of which ships inside every package.

All ten `Iyu.*` packages share one version, so a new number does not by itself mean the
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

Version **0.18.0**. Unit and integration tests run against every project on each
build, and warnings are errors. The OData/GraphQL runtime, identity, attachments, chat, and
scheduled-report modules are all in place and consumed in production.

Known gaps, in rough priority order:

- `Iyu.Report` is new and validated against an anonymized template fixture covering the
  structural complexity DocuChef exposes (merged-cell headers, variable-row tables,
  free-text blocks, multi-sheet duplication) — it has not yet been validated against a
  production template.
- `Iyu.DocConvert` is new. `GotenbergDocumentConverter` is covered by unit tests against a
  stubbed HTTP handler (request shape, content-type mapping, error surfacing); it has not yet
  been round-tripped against a live Gotenberg instance in this repo's own test suite.
- The Azure Blob storage backend has no automated coverage — the test suite uses
  a fake and the local filesystem backend. Backend-specific failure modes are
  therefore not caught here.
- Resumable (chunked) upload is **not** supported. A GB-scale upload that drops
  restarts from zero. Adding it means a resumable protocol plus session state,
  which the gateway deliberately does not have today.
- The gateway has no request-timeout or minimum-data-rate policy of its own, so
  a slow but legitimate upload is subject to the host's slow-POST defences
  (Kestrel's `MinRequestBodyDataRate`, 240 bytes/second by default).
