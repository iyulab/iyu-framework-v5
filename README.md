# iyu-framework-v5

Runtime library for the Iyu stack. Consumed by apps generated from M3L models
via [mdd-booster](https://github.com/iyulab/mdd-booster). Provides a single
`AddIyuMainServer` entry point that wires EF Core, OData, and GraphQL on top of
generator-produced entities, plus optional modules for identity, attachments,
chat, scheduled reports, office-document template rendering, and document-to-PDF
conversion.

Targets .NET 10. All ten projects share one version and ship as separate
NuGet packages.

## Role in the stack

A consumer app builds directly on this framework's interfaces, abstractions, and base
classes — `IyuEntity`, `IyuDbContext`, the OData/GraphQL builders, the identity/authorization
primitives — while mdd-booster generates the model-shaped code that sits on top of them from
an M3L model. The split is deliberate: this framework owns the parts that stay the same no
matter what the model looks like (extension points, cross-cutting mechanisms, wiring), so a
consumer app doesn't reimplement them per project; mdd-booster owns the parts that depend on
the model itself. Neither side assumes anything about which app is consuming it.

## Layers

| Project | Role |
|---|---|
| `Iyu.Core` | `IyuEntity` base class, marker attributes (`[Lookup]`, `[Rollup]`, `[Computed]`, `[Reference]`), value objects (`PhoneNumber`, `EmailAddress`, `WebUrl`), identity contracts, and the attachment contracts (`IAttachmentStorage`, `FileAccessToken`, `FileAccessTokenService`) |
| `Iyu.Data` | `IyuDbContext` base + `IyuTimestampInterceptor` (automatic `CreatedAt`/`UpdatedAt`) + `IyuDateTimeOffsetNormalizationInterceptor` (normalizes every saved `DateTimeOffset` to UTC) + EF Core `ValueConverter`s for the value objects |
| `Iyu.Server.OData` | `IyuEdmModelBuilder.AddEntityPair<TRead,TWrite>(setName)` + generic `IyuODataController<TRead,TWrite>` (CRUD), `$search` binder, `$filter` binder (`in` accepts `[EnumMember]` wire values like `eq`) |
| `Iyu.Server.GraphQL` | `IyuGraphQLSchemaBuilder.AddEntityPair<TRead,TWrite>(queryName, mutationPrefix, authorizePolicy)` (HotChocolate-based) |
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

## Identity

`AddIyuIdentity` wires authentication/authorization *infrastructure*: dual-scheme authentication
(cookie for browsers/API clients, JWT bearer for OAuth2 `client_credentials` service clients),
per-permission authorization policies built from a `permissionCatalog`, and `IIdentityStore` as
the read-side seam a consumer implements against its own user store. `MapIyuIdentity` maps only
the service-client surface (`/api/auth/token` plus `/api/service-clients/*`) — **there is no
built-in human sign-in endpoint, local or federated.** That is deliberate: authenticating a
person and turning the result into a signed-in `ClaimsPrincipal` is left to the consuming app,
the same way the generic OData controller leaves business validation to the consumer. The
framework's job stops at making the cookie scheme, the policies, and `IIdentityStore` available
to build on.

Two recipes for that composition — same seam, same shape, only the credential check differs:

**Local username/password:**

```csharp
app.MapPost("/api/auth/login", async (LoginRequest req, IIdentityStore store, HttpContext http, CancellationToken ct) =>
{
    var user = await store.FindUserByUsernameAsync(req.Username, ct);
    if (user is null || !VerifyPassword(req.Password, user.PasswordHash)) return Results.Unauthorized();

    var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, user.Id.ToString())], CookieAuthenticationDefaults.AuthenticationScheme));
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.Ok();
}).AllowAnonymous();
```

**External OIDC / AD federation** (a customer-hosted IdP — common in on-premises deployments
where each site brings its own directory):

```csharp
builder.Services.AddAuthentication()
    .AddOpenIdConnect("oidc", opts => { /* Authority, ClientId, ClientSecret from config */ });
// AddIyuIdentity already registered the cookie scheme this callback signs into.

app.MapGet("/api/auth/oidc/callback", async (HttpContext http, IIdentityStore store, CancellationToken ct) =>
{
    var externalPrincipal = (await http.AuthenticateAsync("oidc")).Principal!;
    var user = await store.FindUserByUsernameAsync(externalPrincipal.Identity!.Name!, ct)
        ?? await ProvisionFromExternalClaimsAsync(externalPrincipal, ct);   // consumer-owned find-or-provision

    var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new(ClaimTypes.NameIdentifier, user.Id.ToString())], CookieAuthenticationDefaults.AuthenticationScheme));
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
    return Results.Redirect("/");
});
```

`ProvisionFromExternalClaimsAsync` — whether a first-seen federated identity is auto-provisioned
or must already exist, and which external claims map to which local fields — is a policy
decision specific to each deployment, which is why it stays application code rather than a
framework callback signature.

**Native/mobile clients (bearer token instead of a cookie):** a client that cannot hold a cookie
session verifies the user by whichever means the app already uses (the local check above, or an
external IdP), then calls `IdentityTokenService.IssueUserToken` — the same signing primitive
`IssueClientCredentialsAsync` uses for service clients, opened up for a caller-supplied claim set:

```csharp
app.MapPost("/api/auth/mobile-login", async (LoginRequest req, IIdentityStore store, IdentityTokenService tokens, CancellationToken ct) =>
{
    var user = await store.FindUserByUsernameAsync(req.Username, ct);
    if (user is null || !VerifyPassword(req.Password, user.PasswordHash)) return Results.Unauthorized();

    var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()) };
    var result = tokens.IssueUserToken(claims, lifetimeOverride: TimeSpan.FromDays(30));
    return Results.Ok(new { accessToken = result.AccessToken, expiresIn = result.ExpiresInSeconds });
}).AllowAnonymous();
```

`IssueUserToken` does not authenticate — it only signs the claims it is handed, using the same
`IdentityTokenOptions` (`SigningKey`/`Issuer`/`Audience`) the cookie/OIDC recipes above never touch.
`lifetimeOverride` is there because a client without a refresh flow (a mobile app queuing work
offline) typically needs a longer-lived token than the short default tuned for service clients —
pass nothing to keep `IdentityTokenOptions.Lifetime`.

### Diagnosing a service client that stopped working

`ServiceClientSummary` — what `ListServiceClientsAsync` returns to an owner — carries two
timestamps that answer different questions, and the pair is what makes a failing credential
diagnosable without reading the database:

| | Means |
|---|---|
| `LastUsedAt` | When it last obtained a token, or `null` if it never has |
| `SecretRotatedAt` | When the secret was last replaced, or `null` if it is still the one issued |

Read together they separate causes that look identical from the outside — the endpoint answers
every rejection with one `invalid_client`, deliberately:

- `SecretRotatedAt` **newer than** `LastUsedAt` — the holder is presenting the previous secret.
  The credential is healthy; the rotation was never delivered.
- `LastUsedAt` is `null` on an active client — the secret never worked at all, so what was
  handed over was wrong from the start.
- Neither, and `IsActive` is false — it was revoked.

`SecretRotatedAt` names one event rather than being a general "last modified": a permission
change is not a rotation, and folding the two into one column puts the owner back to guessing
which happened.

**Implementing the store**: `IServiceClientStore.UpdateSecretAsync` receives the rotation
timestamp — persist it, and return it on the summary. The value is passed in rather than taken
from the store's own clock so that a rotation and a last-use can be compared without having been
stamped by two different machines.

**Server-side diagnosis**: rejected `client_credentials` requests are logged with the cause —
no such client, revoked, expired, or secret mismatch — at `Warning`, against the category
`Iyu.MainServer.Identity.IdentityTokenService`. The *response* stays one undifferentiated
`invalid_client` with equalized timing, which is what stops the endpoint confirming which client
ids exist; the log is how the operator, who already has the database, is not left with the same
information as the caller. The secret is never logged in any form.

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
caller's explicit assignment. On a `PATCH` the drop is not unconditional — an
update that could store *none* of what it was sent is refused rather than
reported as done; see [What a `PATCH` can store](#what-a-patch-can-store).

### Write failure responses

`AddIyuMainServer` wires a global exception handler (`AddExceptionHandler` + `AddProblemDetails`,
completed by `UseIyuMainServer`'s `app.UseExceptionHandler()`) so no unhandled write-path exception
reaches a client raw. A `SaveChangesAsync` failure that surfaces as `DbUpdateException` — a
unique-index violation, a foreign-key violation, an optimistic-concurrency conflict — is answered
with a `409 Conflict` `application/problem+json` body; every other unhandled exception still falls
through to a generic, structured `500`. Neither response includes the underlying exception's message
or type: this framework stays provider-agnostic (no Npgsql/SqlClient package reference), so it does
not attempt to distinguish *which* constraint failed — only that the write conflicted with the
current state of the data. No opt-in is needed; a consumer that never hits either path pays nothing
for it.

⚠ **This handling runs inside `UseIyuMainServer`, so it displaces an exception-catching middleware
placed before it.** `UseIyuMainServer` calls `app.UseExceptionHandler()` as its first step; a
`try`/`catch` middleware registered ahead of that call sits *outside* it and no longer sees a
write-path `DbUpdateException` at all. Nothing breaks loudly when this happens — the status stays
`409` and only the body changes — so a consumer that classified failures itself finds out from its
own tests, not from a compiler or a stack trace.

**To keep provider-specific classification, register your own `IExceptionHandler` before
`AddIyuMainServer`.** The exception-handling middleware calls handlers in registration order and
stops at the first one that returns `true`, so ordering is the whole mechanism:

```csharp
// Yours is asked first; whatever it declines falls through to the provider-neutral 409 above.
builder.Services.AddExceptionHandler<SqlStateExceptionHandler>();
builder.Services.AddIyuMainServer<AppDbContext>(...);
```

Answer only what you recognise — return `false` for everything else rather than mapping unknown
failures yourself, and the layering stays clean: your handler owns the cases your provider lets you
name, this framework owns the rest. Registering it *after* `AddIyuMainServer` reverses the order and
the neutral `409` wins instead.

A request body a client sends *before* `SaveChangesAsync` is even reached can also fail — a
malformed EDM literal (e.g. a `DateTimeOffset` string with no UTC offset) fails OData's own body
binder, not the database. `Post`/`Patch` still answer `400`, but with a generic, sanitized message
in place of the binder's own error: internal vocabulary like the `Edm.*` type name or the
underlying exception's type never reaches the response. An ordinary `[Required]`/annotation-driven
validation failure is unaffected — it never carried that vocabulary to begin with, and is returned
exactly as authored.

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

### Per-entity authorization

Beyond authentication, a caller may need a specific claim to touch a given set/field at all — both
surfaces below take an ASP.NET Core authorization policy name, the same ones `AddIyuIdentity`'s
`permissionCatalog` registers.

**OData** — `IyuODataController<TRead,TWrite>` is a plain MVC controller, so this rides ASP.NET
Core's standard `IControllerModelConvention` mechanism rather than anything OData-specific:

```csharp
options.ODataModel.AddEntityPair<OrderExt, Order>("orders");
options.ODataModel.RestrictPolicy("orders", readPolicy: "orders.read", writePolicy: "orders.write");
```

GET requires `readPolicy`, POST/PATCH/DELETE require `writePolicy`; either may be left `null` (the
default) to leave that side unrestricted by this mechanism. The first call that uses either wires
the `AuthorizeFilter` automatically — no separate registration step. A distinct method from
`Restrict` (verbs, below) to avoid `params` overload ambiguity; both read the registry's live state,
so `RestrictPolicy` may run before or after `AddEntityPair`, in either order relative to `Restrict`.

When "may edit" and "may delete" are different permissions, name the third one:

```csharp
options.ODataModel.RestrictPolicy("orders",
    readPolicy: "orders.read", writePolicy: "orders.write", deletePolicy: "orders.delete");
```

`deletePolicy` governs DELETE alone; omitting it — the default — leaves DELETE under `writePolicy`,
so an app that does not separate the two never sees the parameter. This is the same per-verb
discrimination `Restrict` already offers on the *availability* axis (it can withdraw `Delete` by
itself), applied to authorization.

**GraphQL** — query fields have no attribute-based equivalent (they are built by a fluent descriptor
API, not resolved through MVC), so `AddEntityPair`'s third parameter is the counterpart:

```csharp
options.GraphQL.AddEntityPair<OrderExt, Order>("orders", "order", authorizePolicy: "orders.read");
```

`authorizePolicy` is an ASP.NET Core authorization policy name — the same ones
`AddIyuIdentity`'s `permissionCatalog` registers for OData/MVC. Passing it applies the field
the same way `[Authorize(Policy = "...")]` would; omitting it (the default) leaves the field
exactly as before this parameter existed, covered only by whatever `FallbackPolicy` is
configured. The first `AddEntityPair` call that uses this parameter also wires the bridge
HotChocolate needs to evaluate that policy against `IAuthorizationService` — HotChocolate ships
the `.Authorize(policy)` descriptor extension but no default handler that checks it against
ASP.NET Core's own authorization services, so without this a policy name on a field would have
nothing to enforce it. No separate registration call is needed; a schema that never passes
`authorizePolicy` never pays for it.

**When the registration is not yours to edit**, e.g. a single generated file that calls
`AddEntityPair(queryName, mutationPrefix)` once per pair with no per-call-site control, apply the
policy afterward instead of at registration:

```csharp
RegisterGeneratedEntities(options);   // registration you may not own

options.GraphQL.Restrict("orders", "orders.read");
```

`Restrict` requires the field to already be registered via `AddEntityPair` — it throws if it is
not — and reaches the schema identically to declaring `authorizePolicy` at `AddEntityPair` time,
since both read the same live state rather than a value captured at registration. Unlike
`options.ODataModel.Restrict`, this one **must run before `ApplyTo`** — `ApplyTo` decides
synchronously, during service configuration, whether to wire the authorization handler into DI, so
a `Restrict` call made afterward throws rather than silently registering a policy nothing will
ever enforce.

### How far a read policy reaches

A read policy protects the **data**, not the one route you declared it on. Both surfaces enforce it
that way, and both did not always — the paragraphs below describe what the framework does now,
because the difference is invisible from a successful response.

**OData — every set an `$expand` reaches is checked.** `RestrictPolicy` is enforced by an
`AuthorizeFilter` on the restricted set's own controller action, and an expand is served by the
*addressed* set's action without ever entering the other one. So the check runs separately, against
each set the expand reaches, at any depth:

```http
GET /$data/Orders?$expand=Customer
```

If `Customers` carries a `readPolicy` the caller does not satisfy, the request is refused even
though `Orders` is open — `401` without an identity, `403` with one, the same split `Customers`
produces on its own route. The check runs before the action, so a refusal costs no query.

It is deliberately **fail-closed** on an `$expand` it cannot interpret: such a request is answered
`400` rather than passed along, because an expression this check rejects and the query pipeline
later accepts would be a way around it.

Expand **depth** is not this framework's setting: nothing here sets `MaxExpansionDepth`, so the
ceiling is the one `EnableQueryAttribute` applies. Measured against it, that ceiling is **2** — a
third hop is refused with `400` and a message naming the limit. Raise it on your own
`EnableQueryAttribute` or `ODataValidationSettings` if your app needs more.

Authorization does not rest on that number either way: the check walks an expand to the bottom, so
a set is authorized however deep it is reached, and raising the ceiling does not widen what a
caller can see without the policy.

**GraphQL — the policy is on the object type, not only on the root field.** `authorizePolicy`
attaches to the query field *and* to the read type it returns. A field on some other type that
returns the protected type reaches the data without the root field's resolver being involved, so a
selection through such a field is refused at that field's own path rather than answering `null`. A
caller that holds the policy is unaffected either way.

For the same reason a read type may be exposed **once**. `AddEntityPair` throws if the type already
backs another query field: two fields are two doors to the same data, each with its own
`authorizePolicy`, so the door without one would decide what the door with one protects. Expose it
once and restrict that field — the exception names the field that already holds the type.

> **When this starts to matter.** Both paths open the moment a read type carries a navigation
> property to another read type, whether you wrote it or a generator emitted it. Until then no
> selection can traverse between read types and there is nothing for either check to refuse. Set the
> policies before that property exists rather than after — an expand path is not visible in a
> response that succeeded.

### Checking that nothing was left unprotected

Attaching a policy per entity is one thing; knowing you attached it **everywhere** is another, and
the two look identical from outside. An entity set with no policy and one whose policy the caller
happens to satisfy both answer `200` — the difference only shows when someone stands up a caller
who *should* be refused. Surfaces make that worse: a pair registers successfully on OData and
GraphQL independently, so protecting one and forgetting the other fails silently, and a surface
added later is not covered by any check written before it.

`IAuthorizationSurfaceReport` answers it from the registrations themselves:

```csharp
var report = app.Services.GetRequiredService<IAuthorizationSurfaceReport>();

// Pin it as a contract test — new entities and new surfaces widen it automatically.
Assert.Empty(report.Unprotected);
```

`Entries` is every (surface, entity, operation) triple with the policy attached to it, where the
operation is read, write, or delete; `Unprotected` is the subset whose policy is `null`. Registered
automatically by `AddIyuMainServer`.

A delete row reports the policy that actually runs: a set with no `deletePolicy` shows its
`writePolicy` there, not `null`. The row exists even then, so the report answers "what protects
deletes on this set" rather than leaving the reader to infer it.

> 🔴 **`null` means "not attached through this framework", not "reachable by anyone."**
> The report sees what `RestrictPolicy` (OData) and `AddEntityPair`/`Restrict` (GraphQL) attached.
> A policy applied some other way — an `[Authorize]` attribute on a hand-written controller, an MVC
> convention over controller models, endpoint metadata, a gateway in front — is **invisible here**.
>
> So an app that authorizes through a controller convention will see *every* entry come back
> `null`. That is not a finding, and reading it as one leads somewhere worse than not looking:
> a report that cries wolf gets ignored. **To make this report mean something, attach policies
> where the framework can see them** — `RestrictPolicy` / `Restrict`. Doing so also removes the
> parallel entity→policy map such a convention needs, since the registration becomes the one place
> that knows.

Two entries are deliberately *not* emitted, because a row that can never be given a policy would
sit in `Unprotected` forever and make the empty-list assertion unusable:

- a set whose `readOnlyVerbs` refuse every write verb has no write half, and one that withdraws
  `Delete` alone keeps its write row but has no delete half;
- GraphQL emits reads only — it records a `mutationPrefix` but generates no mutations yet.

### Rules on the generic write path

`AddEntityPair` opens generic writes (OData `PATCH`, GraphQL) over an entity. Guarding a field on
one of those entry points does not guard the others, and does not guard the next one added — so a
rule like *"this field cannot change once the order is billed"* has to sit where every entry point
converges, at save time.

Derive from `EntityWriteRule<T>` and register by scanning:

```csharp
public sealed class OrderVatTypeLock : EntityWriteRule<Order>
{
    protected override void Apply(WriteRuleContext<Order> ctx)
    {
        if (!ctx.IsUpdated || !ctx.IsModified(nameof(Order.VatType))) return;
        if (ctx.Entity.BilledDate is not null)
            throw new DomainRuleException("Cannot change VAT handling after invoicing.");
    }
}

builder.Services.AddIyuWriteRules(typeof(Program).Assembly);
```

The context carries what a rule needs without re-deriving it: `Entity`, `IsAdded` / `IsUpdated`,
`IsModified(name)`, `Original<T>(name)` / `Current<T>(name)`, and `Db` for rules that write a row
of their own (a change log added there is saved by the same `SaveChanges`).

| Kind of rule | Shape |
|---|---|
| Conditional lock / permission | check `IsUpdated` + `IsModified`, then **throw** — the exception propagates unchanged, so your own domain exception and its response mapping stay yours |
| Default, derivation, normalization | check `IsAdded` (or both) and assign to `ctx.Entity` |
| Change log | read `Original<T>` / `Current<T>`, `ctx.Db.Add(...)` the record |

Notes that matter in practice:

- **`IsModified` is always `true` on an insert** — every property of a new row is being written.
  A lock that should only guard edits must test `IsUpdated` first.
- **Deletes are not dispatched.** A rule about a field's value has nothing to say about a row on
  its way out.
- **Rules run in one pass and do not see entries created by other rules** — a log row one rule adds
  is not dispatched to the others. ⚠ That guarantee covers *entries*, not *values*: a field another
  rule **assigned** is read from the live entry, so a rule that runs later does see it as modified.
  Scanning promises no order, so a pair where one rule assigns a field and another keys off that
  field being modified is order-dependent and can flip on a recompile. Keep such a pair in one rule,
  or verify it in both orders — see the boundary section below.
- A rule on a base type covers derived entities.
- **Scanning is the point, not a convenience.** A hand-written registration line per rule fails by
  omission, and an omitted rule is indistinguishable at runtime from one whose condition never
  fired: nothing throws, nothing logs, the invariant is simply not enforced.

#### What belongs here, and what does not

A consumer who first meets this primitive tends to count every `SaveChanges` interceptor they own
and expect all of them to move. Most do; some should not, and the line is worth stating rather than
letting each app rediscover it.

| The rule touches | Belongs here | Why |
|---|---|---|
| **fields of the entity it is declared for** | ✅ | This is the primitive. Lock, permission check, default, derivation, normalization, and the change-log read all live here |
| **creating another entity** (`ctx.Db.Add(...)`) | ✅ | Supported, and the change-log shape above is exactly this — the new rows are saved by the same `SaveChanges` |
| **modifying another entity's fields** | ❌ | Recomputing a parent's state from the children in this save is an aggregate concern, not a field rule — it reads entries this rule cannot see and would make the one-pass, order-free guarantee a lie |
| **anything after the save** — a second `SaveChanges`, work that needs the generated keys | ❌ | `Apply` runs *before* the write. Post-save work is `SavedChanges`/`SavedChangesAsync` on an interceptor, which is also where a second save round belongs |

The two on the right are not gaps to be filled later; they are a different axis, and a plain
`ISaveChangesInterceptor` remains the right home for them. **Moving every rule is not the goal** —
moving the ones that are field rules is, and the wiring those shed (the `SavingChanges` /
`SavingChangesAsync` double override, the `ChangeTracker.Entries<T>()` walk, the null guard, and
the hand-written registration line) is the same for every one that moves.

> ⚠ **The one and only ordering hazard, stated plainly.** A rule that assigns to a field makes that
> field `IsModified`, and a rule dispatched **after** it on the same entry sees that — a rule
> dispatched before does not. So a pair like *"copy this value in"* + *"normalize it if it changed"*
> produces different results depending on which was registered first, and **assembly scanning does
> not promise an order**: the pair can flip on a recompile, silently, with nothing thrown and
> nothing logged.
>
> This is narrower than it sounds — it needs two rules on the same entity where one writes a field
> the other keys off. If you have that pair, the reliable fix is to make it **one rule** (assign,
> then normalize, in the order you wrote). If you keep it as two, pin it with a test that runs both
> registration orders rather than reasoning about it. Both directions of this behavior are pinned by
> a test in this repo, so a change to it will be a deliberate one.

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
`$metadata` ("server-supplied, do not send on insert/update"). A `POST` naming it anyway
is not rejected: the value is silently dropped from what the generic controller copies
onto the write entity, the same way `Id`/`CreatedAt`/`UpdatedAt` already are. A `PATCH`
naming it is dropped on the same terms *unless the whole request is unwritable* — see
[What a `PATCH` can store](#what-a-patch-can-store) below. A write straight to the
write-side `DbSet` — a dedicated endpoint reached through your own controller action, for
instance — is unaffected; it never goes through the generic controller's copy step at all.

Same read-type rule as `Exclude<T>()`, for the same reason: request bodies bind to
`TRead`, so name that side. Also order-independent and callable after the fact from a
generated registration, exactly like `Exclude<T>()`.

### What a `PATCH` can store

A partial update names properties on `TRead`, and not all of them have somewhere to go:
a derived column the view computes has no counterpart on `TWrite`, `ExcludeFromWrite`
marks one deliberately, and `Id`/`CreatedAt`/`UpdatedAt` are the server's.

Those are **dropped in silence whenever the same request also carries something writable**.
That is what lets a client read an object and send it back whole, derived fields included,
and still have the one field it edited applied — the ordinary round-trip, and the reason
the drop exists at all.

When **every** property in the request is unwritable, the update is refused with `400` and
a body keyed by property:

```json
{ "error": { "details": [
  { "target": "ItemCount", "message": "The property is read-only: it is derived and has no stored counterpart." }
] } }
```

The distinction the caller gets from this is the one they cannot draw themselves: a value
refused because it is derived, a value refused because the model locks it, and a value
refused because the server owns it read the same from outside. Answering `204` to a
request that could not have stored anything reads as "accepted, and the field is
protected" — which is a different fact, and callers have acted on it.

Two boundaries are deliberate:

- **An empty body is still `204`.** Nothing was sent, so nothing was refused.
- **A property the model never declares never reaches this.** Deserialization refuses it
  first, with OData's own error. Only a property `TRead` declares gets this far.

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

## Chat (`Iyu.Server.Chat`)

A thin adapter over BareChat (`PackageReference Include="BareChat"`) that maps iyu's own claim
conventions onto BareChat's user context, gated behind one configuration flag:

```csharp
builder.Services.AddIyuChat(builder.Configuration);   // reads the "Chat" section

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseIyuChat();   // must come after host authentication — BareChat reads HttpContext.User
```

```jsonc
// appsettings.json
"Chat": {
  "Enabled": true
  // every other BareChatOptions field is valid here too — the section binds straight into it
}
```

**`Chat:Enabled` is the only flag this adapter adds.** `false` or a missing section makes both
`AddIyuChat` and `UseIyuChat` complete no-ops — no services registered, no routes mounted. When
`true`, the rest of the `Chat` section binds directly into `BareChatOptions`, so every option
BareChat itself exposes is reachable here without this package re-declaring it.

**Display names, not login IDs.** iyu's claim convention puts the login ID in `ClaimTypes.Name`
and the human-readable name in `ClaimTypes.GivenName`; BareChat's default provider shows
`Identity.Name` (the login ID) as the chat display name. `IyuChatAuthProvider` replaces that
default to prefer `GivenName`, falling back to the login ID only when no given-name claim is
present. The user identifier stays `ClaimTypes.NameIdentifier`, BareChat's own standard.

For everything past these two calls — message model, moderation, storage — see BareChat's own
documentation; this package only adapts identity and toggles the feature on.

## Scheduled reports (`Iyu.VaultAi`)

A hosted scheduler plus embedded viewer SPA that generates Markdown reports via an LLM agent
and serves them at a mounted route. Registration is conditional: both
`AddVaultAiReports(configuration)` and `UseVaultAiReports()` no-op (the feature is entirely
absent — no hosted service, no routes) when the `VaultAi:Url` configuration key is blank, so
leaving the section out of `appsettings.json` is how a consumer opts out.

```csharp
builder.Services.AddVaultAiReports(builder.Configuration);   // reads the "VaultAi" section

var app = builder.Build();
app.UseVaultAiReports();   // API + SPA at VaultAiSettings.BasePath (default "/vault-ai-reports")
```

```jsonc
// appsettings.json
"VaultAi": {
  "Url": "https://vault-ai.internal",
  "Token": "...",
  "ReportAgentId": "...",
  "ReportPath": "reports"   // see below
}
```

**`ReportPath` accepts an absolute path, and that is the supported way to keep report history
across deployments.** When it is relative (the default, `"reports"`), both the scheduler and
the API middleware resolve it under `IWebHostEnvironment.ContentRootPath` — the app's own
folder. A deployment pattern that replaces that folder on every release (a zero-downtime
release-folder swap, or an Azure App Service slot swap) therefore loses everything written
there, because the runtime-generated `reports/*/output` and `reports/*/logs` are not part of
the publish payload and the old folder is discarded. Setting `ReportPath` to an absolute path
outside the deploy lifecycle (e.g. `"C:/data/vault-ai-reports"`) sidesteps this entirely —
`Path.IsPathRooted` is checked before combining with `ContentRootPath`, so a rooted value is
used exactly as given, in both `ReportSchedulerService` and the middleware's request handling.

Report folders are content-addressed by name (`{ReportPath}/{slug}/info.json` +
`prompt.md`, with `output/*.md` and `logs/*.log` populated at runtime) and are not created by
this package — a consumer seeds them ahead of the first scheduled run.

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

Version **0.29.0**. Unit and integration tests run against every project on each
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
