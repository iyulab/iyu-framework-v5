# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

**Recorded from 0.9.0 onward.** Earlier releases are described by their git tags
(`v0.4.1` … `v0.8.0`) and commit history. They are not reconstructed here, because a
record written after the fact can describe something that did not happen.

## How to read a release here

**Every `Iyu.*` package shares one version number.** A release publishes all eight at the
new number whether or not each one changed, so the version alone cannot tell you if the
code you depend on moved. Each entry below therefore opens with **Packages affected** —
if your dependency is not listed, that release changed nothing you consume, and upgrading
across it is a version bump and nothing else.

**Upgrading across more than one release?** Read every entry between your current version
and the target, not just the newest. Each release states its own breaking changes only.

## [Unreleased]

Nothing yet.

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
