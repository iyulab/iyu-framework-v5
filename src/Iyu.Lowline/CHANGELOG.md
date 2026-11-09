# Changelog — Iyu.Lowline

All notable changes to this package are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## How to read a release here

**`Iyu.Lowline` is versioned on its own line.** It does not reference `Iyu.Core`, Entity Framework Core or
`Iyu.MainServer`, so a change to those packages does not move this version, and a release of this package does not
move theirs. The other `Iyu.*` packages share one number among themselves (see the repository's `CHANGELOG.md`).
`Iyu.Lowline.EntityFrameworkCore` binds to `Iyu.Core` and ships on that shared line; it depends on a minimum version
of this package, which its own release notes state.

Releases up to 0.47.0 are recorded in the repository's `CHANGELOG.md`; this file starts with the release after it.

## [Unreleased]

### Added

- **`Iyu.Lowline`: `ILowlineHistorySource` — start from the records the application already holds.** A new installation's
  engine has learned nothing, so it suggests nothing until people have settled enough new records, and the host had no way to
  bring the application's older records to it (handing them over as the application's own doing is stored but never counted,
  so it cannot produce a suggestion). Implement the interface for a form (opt-in; scoped, next to its hook) and a background
  pass — `IyuLowlineOptions.HistoryInterval` (five minutes; zero turns it off), `HistoryBatchSize` (500 per space per pass) —
  learns the records the store does not hold yet as typed by a person with no suggestion shown. That is a declaration only the
  application can make, which is why the host asks for the records instead of guessing which ones qualify. A record the engine
  refuses is counted and logged and does not stop the batch. `IyuLowlineHistoryService.RunOnceAsync` runs one pass. No change
  for a form without a source.

### Changed

- **`Iyu.Lowline` and `Iyu.Lowline.EntityFrameworkCore` describe themselves on nuget.org.** Both carried the repository-wide one-line
  description of the other `Iyu.*` packages; each now says what it does and what it does not depend on. Package metadata only.

- **`Iyu.Lowline`: the engine is now `Lowline` 0.11.0.** The package carries the engine's native library for Alpine Linux as
  well (`linux-musl-x64`, `linux-musl-arm64`), so the engine can load in an Alpine-based image; it needs `libgcc` at run time,
  which the .NET images for Alpine have. Alpine has not been exercised with this package yet, so the README no longer says it
  cannot run there but still recommends a Debian-based image. 0.11.0 refuses an attestation that names a field the form does
  not decide; the settle route only attests the checks it drew for the fields the form decides, so its behaviour is unchanged.
  A store written by 0.11.0 records the score a judgement gave an answer, and a reader of the same store older than the engine's
  decider 0.10 refuses it — if other processes read the store, move them to 0.11.0 too.

### Breaking

- **Breaking — `Iyu.Lowline`: the host layer no longer assumes Entity Framework; the EF store moves to the new `Iyu.Lowline.EntityFrameworkCore`.**
  Two files in `Iyu.Lowline` used EF Core and `Iyu.Core` — the store and the registration — yet every host that referenced
  the package carried both, and the only way to turn Lowline on named an EF `DbContext`. `Iyu.Lowline` now registers over two
  ports the host provides, the engine's `ILowlineStore` and `ILowlineRecordIndex`: `AddIyuLowline(o => …)` takes no type
  arguments and the package references neither EF Core nor `Iyu.Core`. A host that registered neither port, or only one, does
  not start, and the message names which. The EF store (`EfLowlineStore<TContext, TRecord, TTuning>`) and the
  `AddIyuLowline<TContext, TRecord, TTuning>` overload are in the new package `Iyu.Lowline.EntityFrameworkCore`, namespace
  `Iyu.Lowline.EntityFrameworkCore`; the overload registers both ports and then calls the one above. **To upgrade:** a host that
  keeps its records in its own tables adds a reference to `Iyu.Lowline.EntityFrameworkCore` and `using Iyu.Lowline.EntityFrameworkCore;`
  — the call and its type arguments stay as they are; any other host registers its own two ports. There is no forwarding
  alias — `EfLowlineStore` and the type-argument overload no longer exist in `Iyu.Lowline`. This is the first publication of
  `Iyu.Lowline.EntityFrameworkCore`.
