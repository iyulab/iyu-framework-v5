# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

**Recorded from 0.9.0 onward.** Earlier releases are described by their git tags
(`v0.4.1` … `v0.8.0`) and commit history. They are not reconstructed here, because a
record written after the fact can describe something that did not happen.

## How to read a release here

**Every `Iyu.*` package shares one version number.** A release publishes all ten at the
new number whether or not each one changed, so the version alone cannot tell you if the
code you depend on moved. Each entry below therefore opens with **Packages affected** —
if your dependency is not listed, that release changed nothing you consume, and upgrading
across it is a version bump and nothing else.

**Upgrading across more than one release?** Read every entry between your current version
and the target, not just the newest. Each release states its own breaking changes only.

**Two kinds of change, marked differently.** A signature or a type's shape stops your build the
moment you upgrade. What a request answers does not — the code still compiles, and the difference
surfaces wherever the response is consumed, if anyone is looking. Entries below mark the second
kind, **🔇 no build-time signal**, because that is the kind an upgrade carries past you.

One test decides the mark: *does the compiler refuse the old code?* Nothing wider. A mark that also
covered "easy to overlook" would end up on every entry and stop meaning anything. Used from 0.27.0
onward; earlier entries state the same consequence in prose where it applies.

## [Unreleased]

**Packages affected:** `Iyu.Server.OData`, `Iyu.MainServer`

🔴 **Two breaking changes**, both 🔇 no build-time signal — the body of every refusal the generic OData
controller makes, and the namespace the OData model is published under.

### Every refusal of the generic OData controller carries a named code

A caller handling `/$data` errors had to parse several shapes: an OData error with an empty
`error.code` for most refusals, the same with `"409"` as the code for a shared-key conflict, an
OData primitive value (`{"@odata.context":"…#Edm.String","value":"…"}`) for a verb the set does not
accept, a bare string for an `$expand` it could not interpret, and no body at all for a key that
names no row or a request without a body.

- **Breaking — 🔇 no build-time signal:** each of these now answers
  `{"error":{"code":"<code>","message":"…"}}`, and `code` is one of `ODataErrorCodes`:

  | Status | `error.code` | When |
  |---|---|---|
  | `400` | `InvalidBody` | the body could not be read, or a value fails the model's rules (`details[]` name each property in `target`) |
  | `400` | `UnknownProperty` | the body names a property the set's type does not declare — `details[]` name each one in `target` (was `InvalidBody`-shaped with a generic message and no name) |
  | `400` | `UnwritableProperty` | every property a `PATCH` sent is one the write side does not accept |
  | `400` | `InvalidBody` | the request has no body (was `400` with no body) |
  | `400` | `InvalidQuery` | a query option cannot be applied — an unknown property, a malformed literal, a limit exceeded (code was empty), or an `$expand` this server cannot interpret (was a string) |
  | `400` | `SharedKeyRequired` | a set that shares its key was posted to without a key |
  | `404` | `KeyNotFound` | `GET`, `PATCH` or `DELETE` by a key that names no row (was `404` with no body) |
  | `405` | `ReadOnlySet` | the set is registered read-only for this verb (was a primitive value) |
  | `409` | `SharedKeyPrincipalMissing` | a shared-key row names a principal that does not exist (code was `"409"`) |
  | `409` | `SharedKeyRowExists` | the principal already has its one shared-key row (code was `"409"`) |

- The two query-layer answers come from `IyuEnableQueryAttribute`, which the generic `Get` actions now
  carry in place of `[EnableQuery]` — every option of the stock attribute applies unchanged. A
  controller that overrides `Get` with `[EnableQuery]` of its own keeps the stock answers; put
  `[IyuEnableQuery]` on the override to keep these.
- A subclass test that expected `BadRequestObjectResult` with a `SerializableError` from `Post`/`Patch`,
  or `NotFoundResult` from `Patch`/`Delete`, now receives an `ObjectResult` whose value is an `ODataError`.
- **An undeclared property is named.** A body carrying a name the set's type does not declare was
  refused with *"The value could not be converted to its expected type."* and nothing else, so a caller
  found the offending name by removing fields one at a time. It is now `UnknownProperty` with one
  `details[]` entry per undeclared name — every one in the body, including inside a nested value
  (`Survey.Grde`), not only the first the reader stopped at. Names are matched without regard to case,
  as the reader binds them; the EDM type's name is still not disclosed.
- **A body that did not bind is one error, not two.** The failed bind leaves the body parameter null,
  and MVC's implicit `[Required]` for it added *"The body field is required."* (`delta` for `PATCH`)
  beside the real error — for a request that did carry a body. It is no longer reported when another
  error explains the failure. A check that matched either message loses its anchor; branch on `code`.

### The OData model no longer publishes your CLR namespace

Every type in `$metadata` was named under its CLR namespace — `<App>.Entities.OrderExt` — because the
model builder uses that unless told otherwise. So the service published how the application
organises its code to every caller, and a query error that named a property quoted the same full
name.

- **Breaking — 🔇 no build-time signal:** the model is now published under the namespace `Default`
  (the name the model builder already gives the entity container). A payload's `@odata.type` becomes
  `#Default.OrderExt`, a type-cast segment `…/Default.OrderExt`, and `$metadata` declares
  `Namespace="Default"`. A client generated from the old `$metadata`, or one matching `@odata.type`
  strings, sees different names. Entity set names, property names and URLs without a cast are
  unchanged.
- `options.ODataModel.Namespace` sets another name; `null` restores the CLR namespaces.
- Two exposed types with the same name in different CLR namespaces cannot share one namespace — the
  model now refuses to build and names them, instead of letting one shadow the other.

## [0.31.0] - 2026-09-24

**Packages affected:** `Iyu.Server.GraphQL`, `Iyu.MainServer`

🔴 **One breaking change** — the schema shape of every query field. A client that queries a field
as a list stops validating against the new schema, so it fails loudly rather than silently.

### A GraphQL query field returns one page, not the whole table

A query field returned every row of its set in one response, and a client had no argument to ask
for fewer — the GraphQL counterpart of the OData read that `0.30.0` bounded. Each field is now a
cursor connection:

- **Breaking:** `{ orders { id name } }` becomes `{ orders { nodes { id name } } }` (or `edges { node
  { … } cursor }`), with `pageInfo { hasNextPage endCursor }` and the `first`/`after`/`last`/`before`
  arguments. The connection type is named after the field (`OrdersConnection`).
- A request that names no page size gets `IyuGraphQLSchemaBuilder.DefaultPageSize` rows (100); one
  asking for more than `MaxPageSize` (1000, the same bound as OData's `$top`) is refused. Both are
  settable on `options.GraphQL`; a default larger than the maximum is rejected when the schema is
  built.
- A field can have its own bounds: `options.GraphQL.Page(queryName, maxPageSize, defaultPageSize)`,
  the counterpart of `options.ODataModel.Page(setName, …)`. The two are separate settings — a set's
  OData page does not carry over to its GraphQL field.
- Pages are slices of the key order, so walking them with `after` returns every row exactly once.
  A keyless read type is paged in the order the database returns it.
- HotChocolate's cost analysis still applies on top: a large page of a wide selection can exceed the
  executor's maximum type cost (1000 by default) and be refused — raise it with
  `ModifyCostOptions` if you raise `MaxPageSize` for such queries.

### A read type with a required navigation can be created again

POST binds the body as the read type, and ASP.NET Core treats a non-nullable reference property as
an implicitly required input. A read type that declares a required relationship the way EF Core
recommends — `Parent Parent { get; set; } = null!;` — therefore refused every create with `400`
naming the navigation, although the write path copies only scalars and never reads it.

- Navigation properties of a registered read type (an `IyuEntity`, or a collection of one) are no
  longer validated as input. A body that sends one is not rejected for it; the value is ignored, as
  it always was.
- Every other property is validated exactly as before. Types not registered as the read half of an
  entity pair — your own MVC models — are unaffected.
- If you added a validation-metadata provider or `[ValidateNever]` to get past this, it can go.

## [0.30.0] - 2026-09-23

**Packages affected:** `Iyu.MainServer`, `Iyu.Server.OData`

🔴 **Three breaking changes** — the first three sections below. Two are 🔇 no build-time signal; the
third changes a virtual method's signature, so a subclass that overrides it stops compiling. The rest
is additive.

### An entity addressed by key now answers `$expand`

`GET /$data/Orders(<key>)?$expand=Lines` answered `200` with `Lines` empty, and a reference
navigation came back absent — whatever rows existed. The by-key action materialized the entity
before the query layer saw it, so the expand ran against an object with nothing loaded. The
collection route was unaffected, which is why the same expand behind a `$filter` on the key returned
the rows.

- The by-key action now returns the query rather than the entity, so `$expand` and `$select` are
  composed into it exactly as on the collection route. A missing key is still `404`.
- **Breaking — the compiler reports it:** `IyuODataController<TRead,TWrite>.Get(Guid key,
  CancellationToken ct)` returning `Task<IActionResult>` is now `Get(Guid key)` returning
  `SingleResult<TRead>`. A controller that overrides it must follow the new signature; one that
  does not override it needs no change.
- A set's read policy still decides what an expand may carry, on this route as on the collection
  route — now that the key route loads the navigation, that is pinned for it as well.

### A read returns at most one page, and `$top` has a ceiling

A read of an entity set with no `$top` used to return every row in one response, and no `$top` was
too large — the query layer was configured with no ceiling, and there was no setting to add one.
For a table that grows without bound, one request occupied the database, the server's memory and
the response.

- 🔇 **A read now returns at most 1000 rows per response.** A larger result is cut there and the
  response carries `@odata.nextLink` to the next page; `$count=true` still reports the full total.
- 🔇 **A `$top` above 1000 is refused with `400`**, naming the limit.
- **`IyuEdmModelBuilder.DefaultMaxTop` / `DefaultPageSize`** (both `int?`, default `1000`; `null`
  removes the limit) set the defaults, and **`Page(setName, maxTop, pageSize)`** gives one set its
  own. The values are written as OData model-bound query settings on the set's read type, so a
  set's page size also applies where that type is returned as a collection inside an `$expand`.

Who notices: a client that reads a set of more than 1000 rows without following
`@odata.nextLink` now receives the first 1000 and nothing tells it more exist unless it looks for
the link; a client that asks for `$top` above 1000 is refused. Follow the link, or lift the limit
for the sets that need it with `Page` — `DefaultMaxTop = null` and `DefaultPageSize = null` restore
the previous behaviour everywhere.

### A query error no longer carries a stack trace outside Development

An invalid query option on `/$data` — an unknown property in `$select` or `$filter`, a malformed
literal, a limit exceeded — is refused by the OData query layer with `400`, and that layer builds
the body from the exception itself: an `innererror` holding the exception type and the full stack
trace, in every environment. The `message` above it is what a caller needs to correct the query;
the `innererror` only describes the server.

- 🔇 **`innererror` is now written only when the host environment is Development.** Everywhere else
  the error keeps `code`, `message` and `details` and loses `innererror`. The change is in the
  route's error serializer, so it covers every OData error on the route, not only query options.
- **`IyuMainServerOptions.IncludeODataErrorDetails`** (`bool?`, default `null`) overrides the
  environment either way: `true` writes the details everywhere, `false` nowhere.

Who notices: a client or log pipeline that read `innererror` from a deployed server. Nothing there
was meant for a caller, so the usual response is to stop reading it; set the option to `true` to
keep the old output.

### An entity set can declare that its key is not its own

Some types carry optional extra facts about another type rather than a collection of them, and the
natural key for such a row is the other row's key. Until now the runtime could not tell such a set
apart from an ordinary one, and the difference matters on exactly one axis: who chooses the key.
For an ordinary set the server may invent one, which is why the generic `POST` does. For this
shape an invented key produces a row that refers to nothing and can never be reached through the
navigation it was meant to fill.

- **`IyuEdmModelBuilder.DeclareSharedKey(setName, principalSetName)`** (and
  `IyuEntityPairRegistry.DeclareSharedKey`) says that every row of `setName` is keyed by a row of
  `principalSetName`. It follows `Restrict`'s shape — both sets need only be registered by the time
  the call runs — so a consumer whose registration is code-generated can state it from a file it
  owns.

- **The generic `POST` then refuses three bodies it would otherwise write**, each with the status
  that describes it: no key at all is `400` (the server must not choose it), a key naming no
  principal row is `409`, and a key whose row already exists is `409` (such a set holds at most one
  row per principal). This is a check, not a constraint — a relational provider's foreign key
  enforces the same relationship at save time regardless; what the check adds is a stated status
  and reason in place of whatever a constraint violation would otherwise surface as.

- **A declaration that cannot be true is refused where it is written**: a set cannot share its key
  with itself, either set must already be registered, and a principal that already shares *its* key
  with a third set is rejected — a chain has no unambiguous owner of the key.

The README's *A set whose key is not its own* section documents the write contract and, next to it,
the question that decides which shape a pair should have — whose key it is. That choice is a schema
migration to undo, so it is written where it is made rather than left to be inferred from the
refusals.

**Nothing changes for a set that does not declare this.** Its key is still invented when the body
omits one, and any key the caller supplies is still accepted. Nothing in this section is a breaking
change: the method is additive and the refusals reach only sets that opt in.

## [0.29.0] - 2026-09-23

**Packages affected:** `Iyu.Data`, `Iyu.FileServer`, `Iyu.MainServer`, `Iyu.Server.GraphQL`

🔴 **Five breaking changes, every one of them 🔇 no build-time signal.** Nothing here alters a
signature or a type's shape, so no consumer's source stops compiling. All five change what the
server does at run time or at startup. Three of them close a way past `RestrictPolicy` and
`authorizePolicy`; read those first if you rely on either.

### A read policy now follows the data, not one route to it

A read policy used to be enforced by a filter on the addressed set's own action. A request that
reached a protected set some *other* way never entered that action, so the policy never ran — which
made the declaration a restriction on one route to the data rather than on the data. Measured: an
anonymous caller that is refused `401` on a protected set received the same rows inline by expanding
into that set from an open one.

- **OData: an `$expand` is authorized against every set it reaches**, at any depth, before the
  action runs — so a refusal costs no query. A caller that does not satisfy a reached set's
  `ReadPolicy` gets `401` without an identity and `403` with one: the same split that set produces
  on its own route. An `$expand` expression this check cannot interpret is refused with `400`
  rather than passed along, because an expression the check rejects and the query pipeline later
  accepts would be the way around it. Expand *depth* is untouched by this release — nothing here
  sets `MaxExpansionDepth`, so `EnableQueryAttribute`'s ceiling still applies (measured: **2**), and
  the check does not rest on it: it walks an expand to the bottom, so a set is authorized however
  deep it is reached.

- **GraphQL: a query field's `authorizePolicy` is attached to its object type as well**, not only
  to the root field. A field on some other type that returns the protected type reaches the data
  without the root field's resolver being involved. With the policy on the type, such a selection is
  refused at that field's own path instead of answering `null`, and a caller that holds the policy
  is unaffected on either path.

- **GraphQL: `AddEntityPair` refuses a read type that is already exposed as a query field.** Two
  fields over one read type are two doors to the same data and each door carries its own
  `authorizePolicy`, so the one without a policy decides what the one with a policy protects. The
  second registration used to overwrite the first silently. A host that registers such a pair now
  fails at startup, and the exception names the field that *already* holds the type, because
  restricting that field is the fix and the rejected name alone does not lead anyone to it. The
  OData registry has always refused a duplicate read type; the two surfaces disagreed and this was
  the lenient one.

> **Whether any of this was reachable against you.** Both gaps open the moment a read type carries a
> navigation property to another read type — written by hand or produced by a generator. If your
> read types have no such property, nothing above could have been used against you, and nothing
> above changes what your server answers.

### The framework's own defaults no longer displace what the host supplied

- **`IyuDbContext` adds its interceptors as defaults rather than unconditionally.** EF Core keeps
  every registered interceptor and runs application interceptors in the order they were added, so
  appending in `OnConfiguring` — which runs *after* the options the consumer built — let this
  library's system-clock `IyuTimestampInterceptor` write last and beat an instance the consumer had
  supplied on the same options. That closed the seam `IyuTimestampInterceptor`'s own documentation
  points at (replace the clock by passing a `TimeProvider`) for everyone deriving from
  `IyuDbContext`, leaving a context that re-registers after `base.OnConfiguring` as the only way
  through. Supplying an instance of either interceptor type now leaves that instance the only one;
  supplying neither behaves as before. The default stays in `OnConfiguring` rather than moving to
  service registration, because `OnConfiguring` runs however the context was built — `AddDbContext`,
  a passed `DbContextOptions`, or `new` in a test — and a default that arrives through only one of
  those paths is not a default.

- **`AddIyuFileGateway` and `AddIyuIdentity` register `TimeProvider` with `TryAddSingleton`.** They
  used `AddSingleton`, and the last registration of a service wins, so a host that had registered
  its own clock first had it silently replaced — the clock that access-token expiry, JWT expiry and
  secret-rotation timestamps are read from. A host that registers no clock is unaffected.

## [0.28.0] - 2026-09-18

**Packages affected:** `Iyu.Data`

🔴 **One breaking change, 🔇 no build-time signal.** `AddIyuWriteRules` refuses an assembly it
cannot fully load, where it used to register whichever types did load and drop the rest. A host
that passes such an assembly now fails at startup instead of starting with some rules missing;
the exception names the assembly, lists what the loader could not resolve, and gives the two ways
to resolve it (pass an assembly whose dependencies the host resolves, or deploy the missing
dependencies alongside it). Nothing in a consumer's source changes, so the compiler has nothing to
object to — the difference appears the first time the application starts.

Scanning for rules exists so that a rule cannot be missed by omission: an omitted rule is
indistinguishable at runtime from one whose condition never fired, so an invariant is simply not
enforced and nothing says so. Tolerating a partial load reopened that hole from the other side, and
a write rule is an invariant guard. No narrower rule was available —
`ReflectionTypeLoadException.LoaderExceptions` names the dependency that could not be resolved, not
the types that needed it, so "tolerate only the dropped types that were not rules" cannot be decided
at that point.

## [0.27.1] - 2026-09-17

**Packages affected:** `Iyu.MainServer`, `Iyu.Server.OData`

`$filter` collection constants (`in (...)`) over an enum carrying `[EnumMember(Value = ...)]` now
resolve the same wire values `eq` already accepted. Previously a value that worked in
`Kind eq 'wire_value'` threw and returned 500 in `Kind in ('wire_value', ...)`, because the query
binder resolved a single constant through the model's EDM-to-CLR member map but parsed each item
of a collection constant against the CLR member name instead. An undeclared value was, and still
is, rejected with 400 at URI parsing — that part was already correct.

## [0.27.0] - 2026-09-09

**Packages affected:** `Iyu.MainServer`, `Iyu.Server.OData`

🔴 **Three breaking changes, all of them small. Two stop your build; the third does not.** Take
them together before upgrading:

1. **`IServiceClientStore.UpdateSecretAsync` receives `DateTimeOffset rotatedAt` before `ct`.**
   Implementations must persist it. The interface changed, so an implementation that has not
   caught up does not compile.
2. **`ServiceClientSummary` gains `SecretRotatedAt`, positionally after `LastUsedAt`.** Whatever
   builds the summary must fill it. It is a positional record, so every construction site names
   the new parameter or does not compile.
3. **🔇 no build-time signal** — **a `PATCH` whose properties are *all* unwritable now answers
   `400` where it answered `204`.** A round trip that carries derived properties alongside a
   writable one is unaffected. Nothing here refuses to compile: a caller that treated `204` as
   "stored" keeps building, keeps running, and stops storing.

<sub>⚠ **The first sentence was amended after publication.** It read "all of them silent if
missed", which is true of the third and not of the first two — the mark above exists so that
claim cannot be made loosely again.</sub>

### Fixed

- **A partial update that could not have stored anything reported success.** A `PATCH` names
  properties on the read type, and not all of them have somewhere to go: a derived column the view
  computes has no counterpart on the write type, `ExcludeFromWrite` marks one deliberately, and
  `Id`/`CreatedAt`/`UpdatedAt` belong to the server. All of those were skipped in silence and the
  request was answered `204` — including when they were the *whole* request. A caller reads that
  as "accepted, and the field is protected", which is a different fact, and a non-interactive
  client has no way to notice the difference.

  The refusal is scoped to "every property is unwritable", and the boundary matters. The
  counter-case is the ordinary round trip — an object read and sent back whole, derived fields
  included, still applies the field that changed — and that is the reason the silent drop exists
  at all. Refusing on *any* unwritable property would kill it.

  The `400` is keyed by property and separates derived from excluded from server-managed, so error
  handling stays uniform with validation. An empty body is still `204`: nothing was sent, so
  nothing was refused.

  ⚠ **A property the model never declares was already refused**, by deserialization, with a `400`
  — that path did not change here, and is now pinned by a test rather than assumed. If you built a
  characterization test on the belief that an unrecognized property name returns `204` and leaves
  the value untouched, it was measuring something else.

- **`ExcludeFromWrite`'s `PATCH` flips with it**, because its `204` was never an independent
  decision — the test asserting it said so, "same precedent as a computed-only patch". `POST` keeps
  dropping the marked value silently, and that asymmetry is deliberate: a create stores the rest of
  the body, so it had an effect.

### Added

- **`ServiceClientSummary.SecretRotatedAt` — when the credential's secret was last replaced.** An
  owner reading a listing could not tell a credential whose holder is presenting the *previous*
  secret from one that never worked at all. Both stop authenticating, both look active, and the
  token endpoint answers every rejection with one `invalid_client`. Telling them apart meant
  reading the credential rows directly, which only whoever implements the store can do.

  Read it against `LastUsedAt`: a rotation newer than the last successful use is a stale secret in
  a holder's hands, and a null `LastUsedAt` on an active client is one that was mis-delivered at
  issuance.

  It names one event rather than being a general last-modified. Folding rotation, permission
  changes and revocation into one column puts the owner back to inferring which of them happened,
  from rows they cannot see.

  `IServiceClientStore.UpdateSecretAsync` receives the rotation timestamp rather than leaving the
  store to stamp it, the same way `TouchServiceClientAsync` already takes its own. The field's
  whole purpose is to be compared against `LastUsedAt`, and two clocks make that comparison
  meaningless.

- **Rejected `client_credentials` requests are logged with the cause** — no such client, revoked,
  expired, secret mismatch. **The response is unchanged**: one undifferentiated `invalid_client`
  with equalized timing, which is what stops the endpoint confirming which client ids exist. That
  defence is aimed at the caller, and had been hitting the operator too — who has the database and
  was still left with no more information than an attacker. The secret is never logged in any form.

## [0.26.0] - 2026-09-08

**Packages affected:** `Iyu.Core`, `Iyu.Data`, `Iyu.MainServer`, `Iyu.Server.OData`

### Added

- **`RestrictPolicy(..., deletePolicy:)` — "may edit" and "may delete" as separate permissions.**

  ```csharp
  options.ODataModel.RestrictPolicy("orders",
      readPolicy: "orders.read", writePolicy: "orders.write", deletePolicy: "orders.delete");
  ```

  The parameter is optional and defaults to `null`, which leaves DELETE governed by `writePolicy`
  exactly as before — an app that does not separate the two never sees it.

  `Restrict` could already withdraw `ODataVerb.Delete` on its own, so per-verb discrimination
  existed on the *availability* axis while the authorization axis collapsed edit and delete into a
  single policy. An app that needed them apart had to leave `RestrictPolicy` and attach policies
  through a controller convention instead — at which point `IAuthorizationSurfaceReport` saw
  nothing attached and reported the whole surface as unprotected. Two features of this library
  excluded each other; neither asymmetry had a reason recorded for it.

### Changed

- **`AuthorizationSurfaceOperation` gains `Delete`, and the report emits a delete row per set.**
  The row carries the policy that actually runs: a set with no `deletePolicy` shows its
  `writePolicy` there, not `null`. That keeps `Assert.Empty(report.Unprotected)` passing for every
  app that never asked for the separation, while letting the report answer "what protects deletes on
  this set" rather than leaving it to be inferred. A set that withdraws `Delete` alone keeps its
  write row and emits no delete row — the same rule that already omits the write half of a set whose
  `readOnlyVerbs` refuse every write verb.

  `Write` now means POST/PATCH and GraphQL mutations only. Code that enumerates `Entries` or
  switches over the enum sees one more row per registered set and one more member.

### Fixed

- **The write-rule ordering guarantee was stated more broadly than it holds.** `0.25.0` documented
  that rules "run in one pass and do not see entries created by other rules, so ordering never
  becomes a hidden contract." The first half is true; the conclusion drawn from it is not. The
  dispatcher fixes the *entry* list up front, but `IsModified` reads the live entry — so a field
  that one rule **assigns** is visible as modified to a rule dispatched after it. Assembly scanning
  promises no order, so a pair where one rule writes a field another keys off is order-dependent and
  can flip on a recompile, with nothing thrown and nothing logged.

  The guarantee is restated at its true scope, the hazard is documented with its reliable fix (keep
  such a pair in one rule, or pin it with a test that runs both registration orders), and both
  directions are now pinned by tests here so a change to this behavior is a deliberate one.
  **No behavior changed** — the dispatcher does what it did; the documentation shipped inside
  `0.25.0` claimed more than it delivered, and an app may have moved rules on the strength of it.

### Documentation

- **What belongs on the generic write path, and what does not.** `EntityWriteRule<T>` invites
  "move every `SaveChanges` interceptor here", and most do move. Two should not: modifying *another*
  entity's fields (an aggregate concern that reads entries a rule cannot see, and would make the
  one-pass guarantee a lie) and anything after the save (`Apply` runs before the write; post-save
  work and a second save round belong on `SavedChanges`/`SavedChangesAsync`). Those are a different
  axis rather than gaps to be filled later — a plain `ISaveChangesInterceptor` stays their right
  home, and moving every rule was never the goal.

## [0.25.0] - 2026-09-08

**Packages affected:** `Iyu.Core`, `Iyu.Data`, `Iyu.MainServer`, `Iyu.Server.OData`, `Iyu.Server.GraphQL`

### Added

- **`IAuthorizationSurfaceReport` — check that nothing registered was left unprotected.** An entity
  set with no policy and one whose policy the caller happens to satisfy both answer `200`; the
  difference only shows when someone stands up a caller who *should* be refused. And because a pair
  registers on OData and GraphQL independently, protecting one surface and forgetting the other
  fails silently. Resolve the report and pin it as a contract test:

  ```csharp
  var report = app.Services.GetRequiredService<IAuthorizationSurfaceReport>();
  Assert.Empty(report.Unprotected);
  ```

  `Entries` is every (surface, entity, read/write) triple with the policy attached to it. Surfaces
  register themselves as `IAuthorizationSurfaceProvider`, so a surface added later joins the report
  — and the assertion above — without the test changing. Registered by `AddIyuMainServer`.

  🔴 **`null` means "not attached through this framework", not "reachable by anyone."** The report
  sees what `RestrictPolicy` (OData) and `AddEntityPair`/`Restrict` (GraphQL) attached; a policy
  applied by an `[Authorize]` attribute, an MVC convention over controller models, endpoint
  metadata, or a gateway is invisible to it. An app that authorizes through a controller convention
  will therefore see *every* entry come back `null` — that is not a finding. To make the report
  mean something, attach policies where the framework can see them.

  Two entries are deliberately not emitted, because a row that can never be given a policy would
  sit in `Unprotected` forever and make the empty-list assertion unusable: a set whose
  `readOnlyVerbs` refuse every write verb has no write half, and GraphQL emits reads only — it
  records a `mutationPrefix` but generates no mutations yet.

- **`IyuGraphQLSchemaBuilder.GetAuthorizePolicy(queryName)`.** The builder could be asked what it
  exposes (`QueryNames`) and how mutations are named (`GetMutationPrefix`) but not what protected
  any of it. Returns `null` for a field registered without a policy and for an unregistered name —
  use `QueryNames` to tell those apart.

- **`EntityWriteRule<T>` — a place to put rules on the generic write path.** `AddEntityPair` opens
  generic writes (OData `PATCH`, GraphQL) over an entity, and guarding a field at one entry point
  does not guard the others or the next one added. Rules run at save time, after every entry point
  converges:

  ```csharp
  public sealed class OrderVatTypeLock : EntityWriteRule<Order>
  {
      protected override void Apply(WriteRuleContext<Order> ctx)
      {
          if (!ctx.IsUpdated || !ctx.IsModified(nameof(Order.VatType))) return;
          if (ctx.Entity.BilledDate is not null) throw new DomainRuleException("…");
      }
  }

  services.AddIyuWriteRules(typeof(Program).Assembly);
  ```

  The context carries `Entity`, `IsAdded`/`IsUpdated`, `IsModified(name)`, `Original<T>`/`Current<T>`,
  and `Db` for rules that write a row of their own. An exception thrown from a rule propagates out
  of `SaveChanges` unchanged, so an application keeps its own domain exception type and whatever
  maps it to a response.

  Registration is by assembly scan rather than a line per rule on purpose: a hand-written list fails
  by omission, and an omitted rule is indistinguishable at runtime from one whose condition never
  fired — nothing throws, nothing logs, the invariant is simply not enforced.

  Notes that bite in practice: **`IsModified` is always `true` on an insert** (every property of a
  new row is being written), so a lock guarding an edit must test `IsUpdated` first; deletes are not
  dispatched; rules run in one pass and do not see entries created by other rules, so ordering never
  becomes a hidden contract; a rule on a base type covers derived entities.

### Changed

- `AddIyuMainServer` now registers the `DbContext` through `AddDbContext`'s `(sp, options)` overload
  so the write-rule interceptor can be resolved from the application container. A consumer's
  `configureDb` callback is unaffected.

## [0.24.0] - 2026-09-05

**Packages affected:** `Iyu.MainServer`, `Iyu.Server.OData`

### Added

- **Structured error responses for OData write failures.** `AddIyuMainServer` now always wires
  ASP.NET Core's `IExceptionHandler`/`AddProblemDetails()` pipeline: a `DbUpdateException` (or
  `DbUpdateConcurrencyException`) thrown from a generic write action now returns a 409
  `application/problem+json` response instead of leaking the underlying provider exception, and
  every other unhandled exception falls through to `AddProblemDetails()`'s generic 500. No
  per-provider error classification (unique/foreign-key/concurrency) is attempted — the framework
  has no direct dependency on Npgsql/SqlClient to classify against, so the response stays
  provider-agnostic by design.

  ⚠ **Added after the fact, because this entry did not say it and the consequence is silent.**
  Two facts belong with the paragraph above:

  1. **The wiring displaces an exception-catching middleware placed before `UseIyuMainServer`.**
     That call installs `app.UseExceptionHandler()` as its first step, so a `try`/`catch` middleware
     registered ahead of it sits outside the handler and a write-path `DbUpdateException` no longer
     reaches it. An application that classified those failures itself keeps answering `409` and only
     the response body changes — nothing fails at build time, and nothing appears in a log.
  2. **To keep that classification, register your own `IExceptionHandler` before
     `AddIyuMainServer`.** Handlers are called in registration order and the first to return `true`
     wins, so yours answers what it recognises and returns `false` for the rest, leaving the
     provider-neutral `409` as the fallback behind it. Registered *after* `AddIyuMainServer`, the
     neutral handler answers first instead. See "Write failure responses" in the README.

### Fixed

- **A model-binding failure on POST/PATCH could leak internal EDM type names in the 400
  response.** `IyuODataController` checked for a null body before consulting `ModelState`, so a
  binder-level failure (a malformed payload the OData deserializer itself rejected) fell through
  to the wrong branch and exposed the raw error. The check order is now reversed, and the
  model-state error is classified by whether `ModelError.Exception` is null — a binder/
  deserialization failure now gets a fixed placeholder message, while a `DataAnnotations`
  validation error keeps surfacing its own message.

## [0.23.0] - 2026-09-03

**Packages affected:** `Iyu.MainServer`, `Iyu.Server.OData`

### Added

- **`IyuEdmModelBuilder.RestrictPolicy(setName, readPolicy?, writePolicy?)`.** Requires an
  ASP.NET Core authorization policy to touch a registered OData entity set — GET requires
  `readPolicy`, POST/PATCH/DELETE require `writePolicy`. This is the OData counterpart of
  `IyuGraphQLSchemaBuilder.Restrict(queryName, authorizePolicy)`: the two surfaces expose the
  same registry-backed capability, and only GraphQL previously had a way to require a policy
  without hand-rolling an `IApplicationModelConvention`. The first registered set that uses
  either parameter wires the enforcement automatically — no separate registration step.

## [0.22.0] - 2026-09-03

**Packages affected:** `Iyu.MainServer`

### Added

- **`IdentityTokenService.IssueUserToken(claims, lifetimeOverride?)`.** Issues a signed JWT for an
  already-authenticated human principal — the counterpart to `IssueClientCredentialsAsync` for
  clients that cannot use the cookie scheme, such as a native mobile or desktop app completing its
  own sign-in flow. Reuses the existing signing pipeline and `IdentityTokenOptions`; no new
  configuration surface. Synchronous, since this path performs no store lookup.

## [0.21.0] - 2026-09-01

**Packages affected:** `Iyu.Server.GraphQL`

### Added

- **`IyuGraphQLSchemaBuilder.Restrict(queryName, authorizePolicy)`.** Applies or replaces the
  authorization policy for an already-registered query field, from a location that does not
  own the original `AddEntityPair` call site — e.g. a code-generated registration file. This is
  the GraphQL counterpart of `IyuEdmModelBuilder.Restrict` on the OData surface, added for the
  same reason: code that emits `AddEntityPair(queryName, mutationPrefix)` without a per-call-site
  `authorizePolicy` argument still needs a way to layer authorization on afterward, from the
  consumer's own composition root. Unlike the OData version, `Restrict` must be called before
  `ApplyTo` — `ApplyTo` decides synchronously whether to wire the authorization handler into DI,
  so a call afterward throws rather than silently registering a policy nothing will enforce.

## [0.20.0] - 2026-08-31

**Packages affected:** `Iyu.Server.GraphQL`

### Added

- **`AddEntityPair` takes an optional `authorizePolicy` parameter.** GraphQL query fields
  registered this way previously had no authorization hook: the OData surface's
  `IyuODataController` gets per-entity authorization for free from ASP.NET Core MVC's
  convention pipeline, but GraphQL's fluent schema-building API closes each field at
  registration time with no equivalent extension point, so an entity restricted on OData was
  still fully readable via GraphQL with only base authentication enforced. The first
  `AddEntityPair` call that passes a policy auto-registers a bridge handler evaluating it
  against ASP.NET Core's `IAuthorizationService` (HotChocolate ships the `@authorize`
  directive but no default handler for it), so there is no separate registration step to
  forget. A policy name that does not resolve fails closed via HotChocolate's own
  `PolicyNotFound`, instead of throwing an unhandled exception.

## [0.19.1] - 2026-08-28

**Packages affected:** `Iyu.Data`

### Fixed

- **A `DateTimeOffset` bound without an explicit UTC offset (OData model binding,
  `DateTimeOffset.Parse`) no longer fails the save with an unhandled 500.** It commonly carries
  the server process's local offset instead of zero, and the PostgreSQL provider rejects any
  non-zero offset for `timestamp with time zone`. `IyuDateTimeOffsetNormalizationInterceptor`
  now re-expresses every non-UTC `DateTimeOffset` on `Added`/`Modified` entries at offset zero
  via `ToUniversalTime()` at `SavingChanges` — same instant, value's meaning unchanged.
  Registered in `IyuDbContext.OnConfiguring` alongside the existing `IyuTimestampInterceptor`,
  so every save path (OData, direct EF usage, seeding) behaves the same regardless of provider.

## [0.19.0] - 2026-08-28

**Packages affected:** `Iyu.Data`, `Iyu.FileServer`, `Iyu.MainServer`, `Iyu.Report`, `Iyu.Server.OData`

### Added

- **`PATCH /api/service-clients/{id}/permissions`** — replaces a service client's permission
  grant without rotating its secret. `rotate` and the other three operations key on `id`, but
  none of them let an owner adjust *what* a client can do without also reissuing *how* it
  authenticates — forcing a secret rotation just to narrow or widen scope means the far end
  has to redeploy a credential it did not need to change, and the client is unreachable for
  however long that takes. Subject to the same `subset ⊆ owner` rule `POST` already enforces:
  a request exceeding the owner's own permissions is rejected with
  `400 { error: "permissions_exceed_owner", exceeding: [...] }`; otherwise the client's
  permission set is replaced (not merged) with the intersection of the request and the
  owner's permissions.

### Changed

- **Dependency versions bumped, patch/minor only, no new public API.**
  `Microsoft.EntityFrameworkCore`/`.Relational` `10.0.5` → `10.0.11` · `Azure.Storage.Blobs`
  `12.24.0` → `12.29.2` · `Microsoft.AspNetCore.Authentication.JwtBearer` `10.0.0` → `10.0.11` ·
  `System.IdentityModel.Tokens.Jwt` `8.19.1` → `8.22.0` · `Microsoft.AspNetCore.OData` `9.4.1`
  → `9.5.0` · `DocuChef` `0.4.0` → `0.5.0`.

### Changed — breaking

- **`IServiceClientStore` gains a required member**, `UpdatePermissionsAsync`. Every
  implementation must add it; the framework provides no default deliberately, for the same
  reason `ListServiceClientsByOwnerAsync` on `IIdentityStore` (0.12.0) has none — a default
  that silently no-ops would let an un-updated store compile and then accept permission
  updates that never actually take effect.

  **To migrate**, replace the client's permission rows within the same transaction as the
  ownership check:

  ```csharp
  public async Task<bool> UpdatePermissionsAsync(Guid id, Guid ownerUserId,
      IReadOnlyList<string> permissions, CancellationToken ct)
  {
      var client = await _db.ServiceClients
          .FirstOrDefaultAsync(c => c.Id == id && c.OwnerUserId == ownerUserId, ct);
      if (client is null) return false;

      _db.ServiceClientPermissions.RemoveRange(
          _db.ServiceClientPermissions.Where(p => p.ServiceClientId == id));
      _db.ServiceClientPermissions.AddRange(
          permissions.Select(code => new ServiceClientPermission { ServiceClientId = id, Code = code }));
      await _db.SaveChangesAsync(ct);
      return true;
  }
  ```

  Scope strictly by `(id, ownerUserId)` — the same pair `DeactivateAsync`/`UpdateSecretAsync`
  already key on — so a caller who does not own the client gets the same "not found" outcome
  the other three operations already give, not a different error shape.

## [0.18.1] - 2026-08-28

**Packages affected:** `Iyu.Server.GraphQL`, `Iyu.MainServer`

### Changed

- **HotChocolate upgraded `14.3.1` → `16.6.1`.** No new public API — this is an internal
  dependency bump plus one bug fix uncovered by it.

### Fixed

- **A host with no GraphQL entity pairs registered (OData-only consumers) failed to start.**
  HotChocolate 16 rejects a `Query` type with zero fields at warmup instead of at first
  request. `MainServerExtensions` now only wires `AddGraphQLServer()`/`MapGraphQL()` when at
  least one GraphQL entity pair is registered, regardless of HotChocolate version — there is
  no reason to stand up an empty `/graphql` endpoint for a consumer that never uses it.

## [0.18.0] - 2026-08-27

**Packages affected:** `Iyu.Server.OData`

### Added

- **`IyuEdmModelBuilder.ExcludeFromWrite<T>()`** — marks one or more properties of a
  registered read type as not writable through the generic OData POST/PATCH surface,
  while leaving them fully readable (`$select`/`$filter`/`$orderby` unaffected). For a
  domain field that must only change through a dedicated action endpoint (a state
  transition, say) and never through a client handing the generic write path a plain new
  value — `Exclude<T>()` is the wrong tool for this, since it removes the property from
  reads too. Advertises the property as the standard `Org.OData.Core.V1.Computed` term on
  `$metadata`, and `IyuODataController<TRead,TWrite>` silently drops it from what it
  copies onto the write entity — the same two-layer enforcement `AddEntityPair`'s
  `readOnlyVerbs` parameter already uses for whole-set restrictions. Order-independent,
  like `Exclude<T>()`: callable before or after `AddEntityPair`.

## [0.17.0] - 2026-08-25

**Packages affected:** `Iyu.DocConvert`

### Added

- **New optional module: `Iyu.DocConvert` — document-to-PDF conversion.**
  `AddIyuDocConvert()` registers `IDocumentConverter.ConvertToPdfAsync(Stream, string
  contentType, CancellationToken)` against a self-hosted [Gotenberg](https://gotenberg.dev)
  instance (a MIT-licensed HTTP wrapper around LibreOffice — no local process management, no
  commercial licensing). The default `GotenbergDocumentConverter` reads the caller's stream
  without disposing it, buffering to bytes before building the multipart request so the
  caller's stream lifetime is unaffected. Completely independent of the rest of this
  framework: no `Iyu.Core` reference, and unrelated to (but composable with) `Iyu.Report` —
  the two can be chained (render a template, then convert the result) but neither depends on
  the other. See the README's "Document conversion" section for setup and MIME-type coverage.

## [0.16.0] - 2026-08-25

**Packages affected:** `Iyu.Report`

### Added

- **New optional module: `Iyu.Report` — office-document template rendering.** A thin DI
  wrapper around [DocuChef](https://github.com/iyulab/DocuChef)'s `Chef`/`IRecipe`/`IDish`
  API: `AddIyuReport()` registers `Chef` as a scoped service, wired to the host's
  `ILoggerFactory`. The module holds no template storage and maps no endpoints — a
  consumer loads a template, binds data, and saves the rendered document; template syntax
  and binding rules are DocuChef's surface, not re-documented here (see the package README
  for the minimal usage shape).
  Completely independent of the rest of this framework: no `Iyu.Core` reference, no
  `Microsoft.AspNetCore.App` framework reference, and unrelated to `Iyu.VaultAi`'s
  scheduled-report generation despite the similar name — `Iyu.VaultAi` schedules and
  generates its own report content, `Iyu.Report` fills a template with data the caller
  supplies, on demand.

## [0.15.0] - 2026-08-24

**Packages affected:** `Iyu.Server.OData`

### Added

- **A set's read-only verb restriction can now be set or changed after `AddEntityPair` already
  registered it.** `IyuEdmModelBuilder.Restrict(setName, params ODataVerb[] readOnlyVerbs)` (backed
  by a new `IyuEntityPairRegistry.Restrict`) updates an already-registered set in place instead of
  requiring the restriction at the original `AddEntityPair` call site. For a consumer whose entity
  registration is code-generated — one generated file calling `AddEntityPair(setName)` per set, with
  no per-call-site control — the `readOnlyVerbs` parameter added in `0.13.0` was unreachable: it can
  only be supplied by whoever writes the call, and a generated file is not hand-edited. `Restrict`
  reaches the set from a location the consumer does own, after the generated registration runs.
  Because `GetEdmModel()` reads the registry lazily (the same property `Exclude()` already relies on),
  the restriction still reaches `$metadata` and the generic controller's per-request enforcement even
  though it is applied after registration. `IyuEntityPairRegistry.Register` is unaffected — it still
  throws on a genuine duplicate registration; `Restrict` only updates the verb set of a set already
  known to be registered, and throws if the set was never registered at all.

## [0.14.0] - 2026-08-23

**Packages affected:** `Iyu.Core`, `Iyu.FileServer`, `Iyu.MainServer`, `Iyu.Server.GraphQL`, `Iyu.Server.OData`

### Added

- **The file gateway answers `HEAD` — existence without transfer.** `FileGatewayHandlers.ExistsAsync`
  (routed automatically by `MapIyuFileGateway()`) validates a `Download`-scoped token and reports
  200/404 by opening and immediately discarding the read stream, never writing a body. Until now a
  consumer that needed to confirm a blob actually landed before trusting its own "uploaded" flag had
  no way to ask without a full `GET` — every caller either streamed bytes it would throw away or went
  without the check entirely. Reuses the existing `Download` token operation rather than minting a
  new one: the ability to know a key exists is not a stronger claim than the ability to read it.

- **A read type property's `[Display(Description = "...")]` now surfaces on both API surfaces
  automatically.** `IyuEdmModelBuilder` exposes it as the standard `Org.OData.Core.V1.Description`
  term on `$metadata` (via the built-in `CoreVocabularyModel.DescriptionTerm`), and
  `IyuGraphQLSchemaBuilder` exposes it as the field's `description` in schema introspection (via a
  type extension, the same mechanism `Exclude()` already uses to customize a registered type). No
  new call is needed — both builders read the attribute at `AddEntityPair` / model-build time. A
  property without the attribute keeps no description on either surface, same as before.

### Changed

- **BREAKING:** `IdentityServiceCollectionExtensions` (the `AddIyuIdentity` extension host in
  `Iyu.MainServer.Identity`) is renamed to `IyuIdentityServiceCollectionExtensions`. The old name
  collides with ASP.NET Core Identity's own extensions class of the same simple name — a consumer
  that has both `Iyu.MainServer.Identity` and an ASP.NET Core Identity namespace in scope (exactly
  the position anyone wiring cookie auth alongside this package is in) hit `CS0433` on the ambiguous
  reference. `AddIyuIdentity(...)` itself is unaffected — it is an extension method and consumers
  call it the same way regardless of the host class's name; only code that references the class by
  name (e.g. `IdentityServiceCollectionExtensions.CookiePolicyName`) needs updating to the new name.

## [0.13.0] - 2026-08-18

**Packages affected:** `Iyu.Data`, `Iyu.MainServer`, `Iyu.Server.GraphQL`, `Iyu.Server.OData`

### Added

- **`AddEntityPair` accepts `readOnlyVerbs` so a set can refuse specific write verbs.**
  `IyuEdmModelBuilder.AddEntityPair<TRead, TWrite>(setName, params ODataVerb[] readOnlyVerbs)`
  (`ODataVerb`: `Post` / `Patch` / `Delete`) records the restriction on the entity pair
  registry and enforces it in two places that agree by construction: `$metadata`
  advertises it via the real OData Capabilities vocabulary
  (`Org.OData.Capabilities.V1.InsertRestrictions` / `UpdateRestrictions` /
  `DeleteRestrictions` — not an `Iyu.*` vendor term, since the pinned
  `Microsoft.OData.Edm` only ships `ChangeTracking` as a built-in Capabilities type but
  the vocabulary's namespace and record shape are hand-declared to match the published
  standard exactly), and `IyuODataController<TRead,TWrite>` rejects the matching verb
  with `405 Method Not Allowed` before touching the request body — so a client that reads
  `$metadata` first and one that does not are both refused, not just the one that asked.
  `IyuEntityPairRegistry` is now also registered in DI (`AddSingleton`) and resolved by
  the generic controller's write actions via `[FromServices]`, not constructor injection
  — changing the constructor would have broken every generated controller subclass,
  which calls only `base(context)`. Omit the parameter for the pre-existing behavior
  (every verb allowed). GraphQL needs no change yet: `IyuGraphQLSchemaBuilder` does not
  wire mutations in this runtime scaffold, so there is nothing there to restrict —
  `ReadOnlyVerbs` must be consulted once a future mutation generator adds one.
  (`Iyu.Server.GraphQL` picked up only that forward-reference comment — no behavior
  changed there.)

### Fixed

- **`/api` (MVC) now serializes `[EnumMember(Value = ...)]`-annotated enums by their wire
  name, matching `/$data` (OData) instead of disagreeing with it.** `AddJsonOptions`
  registered a plain `JsonStringEnumConverter`, which always writes the CLR member name.
  Once `IyuEdmModelBuilder` was fixed (`0.12.1`) to honor `EnumMemberAttribute` for
  `/$data`, the two surfaces of the same server started spelling the same stored enum
  value two different ways — a value read from `/$data` no longer matched the same
  value read from a hand-rolled `/api` controller. A new `EnumMemberJsonConverterFactory`
  (`Iyu.Data`) — sharing the same reflection-built wire-name lookup
  `EnumMemberConverter<TEnum>` (the EF value converter) already used, now factored into
  `EnumWireNames<TEnum>` so the two cannot drift apart again — is registered ahead of the
  plain converter and claims only enum types carrying at least one `EnumMemberAttribute`;
  an enum with none falls through to the plain converter unchanged. Deserialization still
  accepts the old CLR-cased spelling (case-insensitively) alongside the wire name, and a
  raw numeric value still deserializes too (matching the plain converter's default
  `AllowIntegerValues` behavior) — so existing request bodies keep working and only the
  **output** changes. Consumer code that string-compares an `/api` response against the
  old CLR-cased value (e.g. `status === "Verdict"`) needs updating to the wire form
  (`"verdict"`) once this ships.

## [0.12.1] - 2026-08-18

**Packages affected:** `Iyu.Server.OData`

### Fixed

- **The EDM now names enum members after `[EnumMember(Value = ...)]`, not the CLR
  member name.** `IyuEdmModelBuilder` wraps `ODataConventionModelBuilder`, which
  discovers enum types and names each EDM member after the CLR enum member —
  `EnumMemberAttribute` was never consulted. A generated model's enums declare their
  wire form there, the same attribute the rest of the wire (`System.Text.Json`
  included) already honors, so `$metadata` and deserialization disagreed: a client
  built from `$metadata` sends the declared wire spelling and gets an unexplained 400,
  because only the CLR spelling deserialized. Every enum reachable from a registered
  entity pair's read type is now pre-registered and its members renamed before the
  model is built, so `$metadata` and deserialization agree. No consumer action needed
  — this only changes what the EDM was already supposed to say for any enum property
  that carries `EnumMemberAttribute`; an enum with no such attributes on any member is
  unaffected.

## [0.12.0] - 2026-08-04

**Packages affected:** `Iyu.Server.OData`, `Iyu.Server.GraphQL`, `Iyu.MainServer`, `Iyu.VaultAi`

> **Two changes here can stop an app that upgrades without reading.** Both are under
> **Changed — breaking** below, each with the code to migrate: an `IIdentityStore`
> implementation stops compiling, and an `Exclude<T>` call that names the wrong type stops
> the app at startup. Everything else is additive.

### Added

- `GET /api/service-clients` — the owner's own service clients, revoked ones included and marked
  `isActive: false`. `rotate` and `revoke` both key on an `id` that was returned exactly once, at
  issuance, and nothing enumerated clients: an owner who lost the issuing response could not retire
  a credential **even after its secret leaked**. The three existing operations were only
  conditionally usable until this one existed.

  Returns `ServiceClientSummary` — a record with no secret-bearing member at all — rather than the
  stored client, which carries `SecretHash`. That makes "no secret material leaves here" a property
  of the type instead of a rule every caller has to remember. It carries `lastUsedAt`, already
  maintained on token issuance, because that is how a dead key is told from a live one.

### Changed — breaking

- **`IIdentityStore` gains a required member**, `ListServiceClientsByOwnerAsync`. Every
  implementation must add it; the framework provides no default deliberately, because a default
  returning an empty list would let an un-updated store compile and then tell every owner they had
  issued nothing — reproducing, more quietly, the failure this endpoint fixes.

  **To migrate**, project your client rows to `ServiceClientSummary`:

  ```csharp
  public Task<IReadOnlyList<ServiceClientSummary>> ListServiceClientsByOwnerAsync(
      Guid ownerUserId, CancellationToken ct) =>
      _db.ServiceClients
          .Where(c => c.OwnerUserId == ownerUserId)     // scope strictly to the owner
          .OrderByDescending(c => c.CreatedAt)
          .Select(c => new ServiceClientSummary(
              c.Id, c.ClientId, c.DisplayName,
              c.Permissions.Select(p => p.Code).ToList(),   // resolve in the same query, not per row
              c.CreatedAt, c.ExpiresAt, c.LastUsedAt, c.IsActive))
          .ToListAsync(ct);
  ```

  Include revoked clients rather than filtering them out — a listing that hides them answers "is
  that credential still out there?" the same way as "it never existed". `CreatedAt` is not nullable;
  the store supplies it.

- `Exclude<T>(...)` on either builder now **fails at startup when it names a type the
  surface does not expose**, instead of quietly excluding nothing. Both builders had a
  hole, with different symptoms, and both left the caller believing a stored value was
  hidden when it was not:

  - `IyuEdmModelBuilder` *declared* the named type on the underlying model builder. An
    attempt to hide one property of a type the model did not expose therefore **added that
    type to `$metadata`** — publishing the rest of its shape while hiding nothing.
  - `IyuGraphQLSchemaBuilder` registered a type extension for it. HotChocolate discards an
    extension whose target type no field returns, so the call was a **silent no-op**.

  The error names the type to pass instead, because passing the wrong one is the whole
  failure mode.

  End-to-end coverage came with it, on a pair whose read and write types are **distinct classes** —
  including `POST`/`PATCH` rejection and the stored value surviving a rejected patch. The previous
  tests registered a pair as `<T, T>`, so they could not distinguish which of the two types carries
  the exclusion, which is precisely what the corrected guidance below got wrong.

- **Corrected guidance that made the above reachable.** 0.11.0 told callers to apply the
  exclusion "to the write type as well … when the value must not be settable through the
  generic write path". That is wrong in both directions: the generic controller binds
  request bodies to the **read** type, so excluding the read type already rejects a `POST`
  or `PATCH` naming the property — and excluding the write type does not protect the write
  path, it only triggers the `$metadata` growth above. A caller who followed the sentence
  as "the write type guards writes" and excluded only that type was left with the read
  surface and the write path both fully open.

  **If you added a write-type exclusion on 0.11.0, remove it** — the read-type exclusion is
  what protects both surfaces, and the write-type call now throws at startup.

### Changed

- Exclusions are applied when the model is finalized rather than when `Exclude` is called, so it may
  now be called **before or after** `AddEntityPair` on either builder. Malformed property
  expressions still throw at the call site.

- The report scheduler's failure handling is now covered by tests. Its whole purpose is that a
  report which fails to generate leaves a **visible marker** instead of a silent hole, and that a
  run of failures escalates to `Critical` — behaviour an operator relies on and nothing verified.
  No behaviour changed; one scheduler pass became reachable to the test assembly so the assertions
  do not depend on how far the background loop runs before the host's start call returns.

### Fixed

- `$metadata` and the service document now answer **under a test host**, not only in a deployed app.
  Both are served by OData's own `MetadataController`, which is never the entry assembly, so MVC
  reached it through the entry assembly's dependency graph — the deployed app's graph contains it,
  a test runner's does not. `AddRouteComponents` published the route either way, so an integration
  test asking for `$metadata` got a **404 that reads as a modelling mistake** rather than a hosting
  artifact. `AddIyuMainServer` now registers that assembly alongside the consumer assemblies it
  already registered for the same reason; the existing dedup guard makes it a no-op where discovery
  had already found it, so nothing changes for a deployed app.

- The README's Status section announced the **wrong version** at two releases running. It is now
  checked against the version the shipped assembly carries, so it cannot be skipped silently — the
  known-gaps list under it is only as trustworthy as the version above it. The hand-kept test total
  that sat beside it is gone rather than guarded: it went stale on every commit that added a test,
  and no reader acted on the number.

## [0.11.0] - 2026-08-04

**Packages affected:** `Iyu.Server.OData`, `Iyu.Server.GraphQL`, `Iyu.Core`, `Iyu.MainServer`
(the OData/GraphQL model builders and a shared expression helper). No behaviour changes for
consumers that do not call the new API.

### Added

- `IyuEdmModelBuilder.Exclude<T>(...)` and `IyuGraphQLSchemaBuilder.Exclude<T>(...)` — remove a
  property from the model a consumer exposes. Until now there was **no way to keep a stored value
  off the API surface**: both builders wrapped their underlying model builder privately, exposing
  only `AddEntityPair`, so every public property of a read type was reachable through
  `$data` and GraphQL. For an entity holding a password hash or a client secret, that meant the
  value was served to any caller holding the entity set's permission — and could be probed one
  character at a time with `$filter=startswith(...)` even without reading it.

  The property is **removed from the model**, not blanked: `$select`, `$filter` and `$orderby`
  naming it are rejected, and the GraphQL schema has no such field. Blanking would be
  indistinguishable from "this row has no value" and would leave the probing route open.

  Both are callable **after** `AddEntityPair`, because neither builder finalises until
  `GetEdmModel()` / `ApplyTo()`. That ordering is the point: consumers whose entity registration
  is code-generated cannot edit the registration, but they can subtract from it afterwards.

  Properties are named by expression (`x => x.SecretHash`) rather than by string. An exclusion
  whose failure mode is "silently exposed the field you meant to hide" must not be able to fail
  by typo; a nested or non-property expression throws where it is written.

  Apply it to the write type as well when the value must not be **settable** through the generic
  write path — a hash that can be written is a password that can be chosen.

- `ExposedProperty.Resolve<T>(...)` in `Iyu.Core` — shared property-selector resolution behind both.


## [0.10.2] - 2026-08-04

**Packages affected**: none functionally — no API or behaviour change. The guidance that
moved ships as XML documentation in `Iyu.FileServer` and `Iyu.MainServer`, so it reaches
you through IntelliSense rather than through this file alone.

### Added

- **The README's "Namespaces a consumer needs" list is now verified, not just written.**
  A test compiles consumer-shaped code — an entity, an attribute, a context, a controller,
  an options type, each named simply — against the `global using` lines that section
  publishes, reading them from the README rather than from a copy. Until now the packages
  were tested here and the guidance was published here, but the two only ever met in a
  consuming project's build, where a gap appears as `CS0246` after release. A missing or
  stale namespace now fails our suite instead of someone else's build.

### Documentation

- **A service client's `id` is returned once, and that is now said out loud.** Rotate and
  revoke both take an `id`, and no endpoint enumerates service clients, so the creation
  response is the only place one appears. The secret being shown once was documented; the
  `id` being shown once was not — and losing that response leaves a credential impossible
  to revoke even after its secret leaks. Stated where a caller meets it: the identity
  integration guide, and the remarks on the creation handler. **Persist the `id`.**
- **`FileGatewayOptions.MaxBytes` now points at the wall a lower limit hides.** The remarks
  listed every ceiling that can cap an upload below this value but said nothing about rate.
  Those are different walls, and only the first is visible while the limit is low: raise it
  and a large transfer over a slow link becomes subject to the host's slow-POST defences,
  where a single non-resumable request is lost rather than continued. Size the limit against
  the slowest link that must succeed, not only against the largest file.
- **The README's Status line was stale in both of its numbers** (version and test count).
  That sentence is what dates the known-gaps list printed beneath it, so a reader deciding
  whether a gap still applies was reading it against the wrong release.

## [0.10.1] - 2026-08-01

**Packages affected**: none functionally — all eight are a documentation and packaging
release. No API or behaviour change.

### Added

- **This file, and it now travels with the code.** `CHANGELOG.md` is packed into every
  `Iyu.*` package next to the README, and `PackageReleaseNotes` points at it, so a
  consumer deciding whether a release affects them does not have to leave the package or
  compare git tags. Upgrade guidance previously lived in a README section, where it was
  present but not findable under that name.

## [0.10.0] - 2026-08-01

**Packages affected**: `Iyu.Server.OData`. The other seven are a version bump only.

### Changed — breaking

- **`IyuODataController<TRead,TWrite>.Patch` now evaluates the read type's validation
  annotations** against the properties a request actually carries, and answers `400` when
  one is violated. Previously only create checked them, so the same value could be refused
  on one verb and stored through the other — and `NOT NULL` does not stop an empty string,
  so no layer refused it.

  **Partial-update semantics are unchanged.** A property the request does not mention is
  not validated, so an entity with required fields is still patchable one field at a time.
  What changed is that a value the request *does* send is now judged. A caller sending, say,
  an empty string into a required field starts receiving `400`; the response names the field
  and reads exactly as the equivalent create failure does, because the same validator
  produces both.

  **Type-level rules are deliberately excluded** — a class-level `ValidationAttribute` or
  `IValidatableObject` reports against the object rather than a property, and enforcing it
  here would make any entity with a cross-field rule impossible to patch, since the rule
  would be judged against fields the request never carried.

  A missing key is still answered `404` before the payload is examined.

### Added

- **README documents the namespaces a consuming project declares.** The types a generated
  application uses are spread across several packages, so the same `using` lines repeat in
  most files; declaring them once with `global using` is usually less friction. Take the
  lines for the packages you actually reference.

## [0.9.0] - 2026-07-31

**Packages affected**: `Iyu.FileServer`. The other seven are a version bump only —
in particular, `Iyu.MainServer` does not depend on the file gateway, so an application that
does not use `Iyu.FileServer` directly is unaffected by this release.

### Changed — breaking

- **`IAttachmentStorage.OpenReadAsync` returns `Task<Stream?>`**, where `null` means
  "nothing is stored at this key". Custom implementations must normalise their backend's
  not-found signal into `null` instead of letting it throw. Callers get a compiler error
  until they handle the `null`, which is the point: the previous behaviour surfaced a
  missing object as an unhandled exception and a `500`, misclassifying a normal storage
  state (deletion while a valid token is in flight, orphan collection, a delete race) as a
  server fault.
- **`AllowedContentTypes` fails closed.** If an allowlist is configured, a token that does
  not declare a content type is now rejected (`content_type_required`) instead of bypassing
  the check entirely. Set `FileAccessToken.ContentType` when minting.
- **An oversized upload answers `413`** instead of `400`. The body is unchanged
  (`{"Error":"too_large"}`), so a client that reads the payload needs no change; one that
  branches on the status code does. The other two rejections stay `400`.

### Added

- **Range requests on download**, so a large transfer resumes instead of restarting.
- **Rejection logging** under the category `Iyu.FileServer.FileGateway`
  (`FileGatewayExtensions.LogCategory`), filterable on its own: deployment misconfiguration
  logs at `Warning`, caller-side conditions at `Information`. Tokens are never logged, and
  successful transfers are not logged either — that is the host's request log.

### Fixed

- **The configured upload ceiling is now reachable.** Host defaults (Kestrel, HTTP.sys, and
  IIS in-process all cap at 30,000,000 bytes) sat below the gateway's 50 MB default, so
  `MaxBytes` could never trip and uploads in that band fell out as a bare `413` that a
  caller could not tell apart from an infrastructure refusal. Both ceilings now trip at the
  same byte, and the host's `413` carries the gateway's structured body.
- **Allowlist matching ignores media-type parameters.** Identity of a media type is its
  `type/subtype`; a parameter qualifies it. Comparing the whole string rejected
  `image/jpeg; charset=…` against an `image/jpeg` entry. `type/subtype` must still match
  exactly, and wildcards are not expanded — this only ever accepts more than before.
