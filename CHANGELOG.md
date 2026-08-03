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

**Packages affected**: none functionally — a test-only addition. No API or behaviour change.

### Added

- **The README's "Namespaces a consumer needs" list is now verified, not just written.**
  A test compiles consumer-shaped code — an entity, an attribute, a context, a controller,
  an options type, each named simply — against the `global using` lines that section
  publishes, reading them from the README rather than from a copy. Until now the packages
  were tested here and the guidance was published here, but the two only ever met in a
  consuming project's build, where a gap appears as `CS0246` after release. A missing or
  stale namespace now fails our suite instead of someone else's build.

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
